/*
 * Copyright (c) 2025 Proton AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using ProtonVPN.Common.Core.Extensions;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;
using ProtonVPN.NetworkFilter;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.ProTun.Contracts.Adapters;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.Settings;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;
using ProtonVPN.Vpn.SplitTunnel;
using Action = ProtonVPN.NetworkFilter.Action;
using CoreNetworkAddress = ProtonVPN.Common.Core.Networking.NetworkAddress;

namespace ProtonVPN.Service.SplitTunneling;

public class SplitTunnel : ISplitTunnel, IServiceSettingsAware
{
    private bool _reverseEnabled;
    private bool _enabled;
    private VpnState _lastVpnState = VpnState.Default;
    private SplitTunnelContext? _context;
    private VpnConfig? _activeRoutingConfig;
    private string[] _configuredRemoteAddresses = [];
    private string[] _domainRemoteAddresses = [];

    private readonly object _stateSync = new();
    private readonly ILogger _logger;
    private readonly ISplitTunnelRouting _splitTunnelRouting;
    private readonly INetworkUtilities _networkUtilities;
    private readonly ISystemNetworkInterfaces _networkInterfaces;
    private readonly IConfiguration _config;
    private readonly IServiceSettings _serviceSettings;
    private readonly ISplitTunnelClient _splitTunnelClient;
    private readonly IAppFilter _appFilter;
    private readonly IPermittedRemoteAddress _permittedRemoteAddress;
    private readonly IAdapterDetailsCache _proTunAdapterDetailsCache;
    private readonly ISplitTunnelDomainPoller _domainPoller;

    public SplitTunnel(
        ILogger logger,
        ISplitTunnelRouting splitTunnelRouting,
        INetworkUtilities networkUtilities,
        ISystemNetworkInterfaces networkInterfaces,
        IConfiguration config,
        IServiceSettings serviceSettings,
        ISplitTunnelClient splitTunnelClient,
        IAppFilter appFilter,
        IPermittedRemoteAddress permittedRemoteAddress,
        IAdapterDetailsCache proTunAdapterDetailsCache,
        ISplitTunnelDomainPoller domainPoller)
    {
        _logger = logger;
        _splitTunnelRouting = splitTunnelRouting;
        _networkUtilities = networkUtilities;
        _networkInterfaces = networkInterfaces;
        _config = config;
        _serviceSettings = serviceSettings;
        _splitTunnelClient = splitTunnelClient;
        _appFilter = appFilter;
        _permittedRemoteAddress = permittedRemoteAddress;
        _proTunAdapterDetailsCache = proTunAdapterDetailsCache;
        _domainPoller = domainPoller;
        _domainPoller.AddressesChanged += OnDomainAddressesChanged;
    }

    public SplitTunnel(
        bool enabled,
        bool reverseEnabled,
        ILogger logger,
        ISplitTunnelRouting splitTunnelRouting,
        INetworkUtilities networkUtilities,
        ISystemNetworkInterfaces networkInterfaces,
        IConfiguration config,
        IServiceSettings serviceSettings,
        ISplitTunnelClient splitTunnelClient,
        IAppFilter appFilter,
        IPermittedRemoteAddress permittedRemoteAddress,
        IAdapterDetailsCache proTunAdapterDetailsCache,
        ISplitTunnelDomainPoller domainPoller)
        : this(
            logger,
            splitTunnelRouting,
            networkUtilities,
            networkInterfaces,
            config,
            serviceSettings,
            splitTunnelClient,
            appFilter,
            permittedRemoteAddress,
            proTunAdapterDetailsCache,
            domainPoller)
    {
        _enabled = enabled;
        _reverseEnabled = reverseEnabled;
    }

    public void OnVpnConnecting(VpnState vpnState)
    {
        lock (_stateSync)
        {
            _lastVpnState = vpnState;
            ClearDomainSplitTunnelState();
            DisableReversed();
            Disable();
            DeleteActiveRoutes();

            _appFilter.RemoveAll();
            _permittedRemoteAddress.RemoveAll();

            if (_serviceSettings.SplitTunnelSettings.Mode == SplitTunnelModeIpcEntity.Permit)
            {
                _appFilter.Add(
                    _serviceSettings.SplitTunnelSettings.AppPaths,
                    [
                        Tuple.Create(Layer.AppAuthConnectV4, Action.SoftBlock),
                        Tuple.Create(Layer.AppAuthConnectV6, Action.SoftBlock),
                    ]);
            }
        }
    }

    public void OnVpnConnected(VpnState state)
    {
        lock (_stateSync)
        {
            _lastVpnState = state;
            ApplySplitTunnelSettings(state);
        }
    }

    public void UpdateContext(SplitTunnelContext context)
    {
        lock (_stateSync)
        {
            _context = context;
        }
    }

    public void OnVpnDisconnected(VpnState state)
    {
        lock (_stateSync)
        {
            _lastVpnState = state;
            string[] configuredRemoteAddresses = _configuredRemoteAddresses;
            ClearDomainSplitTunnelState();

            if (state.Error == VpnError.None)
            {
                DisableSplitTunnel();
                _appFilter.RemoveAll();
                _permittedRemoteAddress.RemoveAll();
                _context = null;
            }
            else
            {
                _permittedRemoteAddress.Add(configuredRemoteAddresses, Action.HardPermit);
                DeleteActiveRoutes();
            }
        }
    }

    public void OnServiceSettingsChanged(MainSettingsIpcEntity settings)
    {
        lock (_stateSync)
        {
            if (_lastVpnState.Status == VpnStatus.Connected)
            {
                ApplySplitTunnelSettings(_lastVpnState);
            }
        }
    }

    private void ApplySplitTunnelSettings(VpnState state)
    {
        DisableSplitTunnel();
        _appFilter.RemoveAll();
        _permittedRemoteAddress.RemoveAll();

        if (_serviceSettings.SplitTunnelSettings.Mode == SplitTunnelModeIpcEntity.Disabled)
        {
            return;
        }

        bool isBlockMode = _serviceSettings.SplitTunnelSettings.Mode == SplitTunnelModeIpcEntity.Block;
        string[] resolvedAddresses = isBlockMode
            ? GetConfiguredRemoteAddresses(_serviceSettings.IsIpv6Enabled)
            : GetResolvedSplitTunnelAddresses(_serviceSettings.IsIpv6Enabled);

        _configuredRemoteAddresses = resolvedAddresses;
        _domainRemoteAddresses = [];
        SetUpApps(state, resolvedAddresses);
        SetUpIps(state, resolvedAddresses);

        if (isBlockMode)
        {
            _domainPoller.ReplaceRules(GetDomainRules());
            _domainPoller.Start();
        }
    }

    private void SetUpApps(VpnState state, string[] resolvedAddresses)
    {
        switch (_serviceSettings.SplitTunnelSettings.Mode)
        {
            case SplitTunnelModeIpcEntity.Block:
                Enable(state, resolvedAddresses);
                break;
            case SplitTunnelModeIpcEntity.Permit:
                EnableReversed(state);
                break;
        }
    }

    private void SetUpIps(VpnState state, string[] resolvedAddresses)
    {
        if (_context is null)
        {
            _logger.Warn<SplitTunnelLog>("Split tunnel context is missing, routes won't be added.");
            return;
        }

        string? localIpv4Address = state.LocalIp;
        if (string.IsNullOrEmpty(localIpv4Address))
        {
            _logger.Warn<SplitTunnelLog>("Local IPv4 address is missing, split tunneling routes won't be added.");
            return;
        }

        VpnConfig effectiveConfig = CreateEffectiveRoutingConfig(_context.Config, resolvedAddresses);
        _activeRoutingConfig = effectiveConfig;

        bool isIpv6Supported = _serviceSettings.IsIpv6Enabled && _context.Endpoint.Server.IsIpv6Supported;
        _splitTunnelRouting.SetUpRoutingTable(effectiveConfig, localIpv4Address, isIpv6Supported);
    }

    private VpnConfig CreateEffectiveRoutingConfig(VpnConfig source, string[] resolvedAddresses)
    {
        return new VpnConfig(new VpnConfigParameters
        {
            Ports = source.Ports,
            CustomDns = source.CustomDns,
            SplitTunnelMode = MapSplitTunnelMode(_serviceSettings.SplitTunnelSettings.Mode),
            SplitTunnelIPs = resolvedAddresses,
            OpenVpnAdapter = source.OpenVpnAdapter,
            VpnProtocol = source.VpnProtocol,
            PreferredProtocols = source.PreferredProtocols,
            NetShieldMode = source.NetShieldMode,
            SplitTcp = source.SplitTcp,
            ModerateNat = source.ModerateNat,
            PortForwarding = source.PortForwarding,
            IsIpv6Enabled = source.IsIpv6Enabled,
            WireGuardConnectionTimeout = source.WireGuardConnectionTimeout,
            DnsBlockMode = source.DnsBlockMode,
            ShouldDisableWeakHostSetting = source.ShouldDisableWeakHostSetting,
            IsWireGuardServerRouteEnabled = source.IsWireGuardServerRouteEnabled,
        });
    }

    private static SplitTunnelMode MapSplitTunnelMode(SplitTunnelModeIpcEntity mode)
    {
        return mode switch
        {
            SplitTunnelModeIpcEntity.Block => SplitTunnelMode.Block,
            SplitTunnelModeIpcEntity.Permit => SplitTunnelMode.Permit,
            _ => SplitTunnelMode.Disabled,
        };
    }

    private void DisableSplitTunnel()
    {
        ClearDomainSplitTunnelState();
        Disable();
        DisableReversed();
        DeleteActiveRoutes();
    }

    private void DeleteActiveRoutes()
    {
        if (_activeRoutingConfig is null)
        {
            return;
        }

        _splitTunnelRouting.DeleteRoutes(_activeRoutingConfig);
        _activeRoutingConfig = null;
    }

    private void Enable(VpnState state, string[] resolvedAddresses)
    {
        string excludedHardwareId = _config.GetHardwareId(state.VpnProtocol, _serviceSettings.OpenVpnAdapter);
        IPAddress localIpv4Address = _networkUtilities.GetBestInterfaceIPv4Address(excludedHardwareId);
        INetworkInterface bestInterface = _networkInterfaces.GetBestInterfaceExcludingHardwareId(excludedHardwareId);

        IPAddress? localIpv6Address = null;
        if (_serviceSettings.IsIpv6Enabled && !string.IsNullOrEmpty(bestInterface.Id))
        {
            localIpv6Address = bestInterface.GetPreferredIpv6UnicastAddress();
        }

        string[] appPaths = _serviceSettings.SplitTunnelSettings.AppPaths ?? [];

        _splitTunnelClient.EnableExcludeMode(appPaths, localIpv4Address, localIpv6Address);

        if (appPaths.Length > 0)
        {
            List<Tuple<Layer, Action>> appFilters =
            [
                Tuple.Create(Layer.AppAuthConnectV4, Action.HardPermit),
                Tuple.Create(Layer.AppAuthConnectV6, localIpv6Address is null ? Action.HardBlock : Action.HardPermit),
            ];

            _appFilter.Add(appPaths, [.. appFilters]);
        }

        if (resolvedAddresses.Length > 0)
        {
            _permittedRemoteAddress.Add(resolvedAddresses, Action.HardPermit);
        }

        _enabled = true;
    }

    private string[] GetResolvedSplitTunnelAddresses(bool allowIpv6)
    {
        return (_serviceSettings.SplitTunnelSettings.Ips ?? [])
            .SelectMany(rawAddress => ResolveSplitTunnelAddress(rawAddress, allowIpv6))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string[] GetConfiguredRemoteAddresses(bool allowIpv6)
    {
        return (_serviceSettings.SplitTunnelSettings.Ips ?? [])
            .SelectMany(rawAddress => GetConfiguredRemoteAddresses(rawAddress, allowIpv6))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetConfiguredRemoteAddresses(string rawAddress, bool allowIpv6)
    {
        string address = rawAddress.Trim();
        if (CoreNetworkAddress.TryParse(address, out CoreNetworkAddress networkAddress) &&
            (!networkAddress.IsIpV6 || allowIpv6))
        {
            yield return networkAddress.ToString();
        }
    }

    private string[] GetDomainRules()
    {
        return (_serviceSettings.SplitTunnelSettings.Ips ?? [])
            .Select(rawAddress => rawAddress.Trim())
            .Where(rawAddress => !CoreNetworkAddress.TryParse(rawAddress, out _))
            .Select(rawAddress => DomainRule.TryCreate(rawAddress, out DomainRule? rule) ? rule : null)
            .Where(rule => rule is not null)
            .Select(rule => rule!.Domain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void OnDomainAddressesChanged(object? sender, string[] domainAddresses)
    {
        lock (_stateSync)
        {
            if (_lastVpnState.Status != VpnStatus.Connected ||
                _serviceSettings.SplitTunnelSettings.Mode != SplitTunnelModeIpcEntity.Block)
            {
                return;
            }

            _domainRemoteAddresses = domainAddresses;
            string[] combinedAddresses = _configuredRemoteAddresses
                .Concat(_domainRemoteAddresses)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _permittedRemoteAddress.Add(combinedAddresses, Action.HardPermit);
            DeleteActiveRoutes();
            SetUpIps(_lastVpnState, combinedAddresses);
        }
    }

    private void ClearDomainSplitTunnelState()
    {
        _domainPoller.Stop();
        _configuredRemoteAddresses = [];
        _domainRemoteAddresses = [];
    }

    private static IEnumerable<string> ResolveSplitTunnelAddress(string rawAddress, bool allowIpv6)
    {
        string address = rawAddress.Trim();
        if (CoreNetworkAddress.TryParse(address, out CoreNetworkAddress networkAddress))
        {
            if (!networkAddress.IsIpV6 || allowIpv6)
            {
                yield return networkAddress.ToString();
            }

            yield break;
        }

        foreach (IPAddress ipAddress in ResolveHostname(address, allowIpv6))
        {
            yield return ipAddress.ToString();
        }
    }

    private static IEnumerable<IPAddress> ResolveHostname(string hostname, bool allowIpv6)
    {
        if (!IsValidHostname(hostname))
        {
            return [];
        }

        try
        {
            return System.Net.Dns.GetHostAddresses(hostname)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork ||
                             (allowIpv6 && ip.AddressFamily == AddressFamily.InterNetworkV6));
        }
        catch
        {
            return [];
        }
    }

    private static bool IsValidHostname(string hostname)
    {
        return !string.IsNullOrWhiteSpace(hostname)
            && !hostname.Contains('/')
            && !hostname.Contains('*')
            && Uri.CheckHostName(hostname) == UriHostNameType.Dns;
    }

    private void Disable()
    {
        if (_enabled)
        {
            _splitTunnelClient.Disable();
            _appFilter.RemoveAll();
            _permittedRemoteAddress.RemoveAll();
            _enabled = false;
        }
    }

    private void EnableReversed(VpnState vpnState)
    {
        IPAddress? localIpv6Address = null;
        if (vpnState.VpnProtocol.IsWireGuard())
        {
            IPAddress.TryParse(_config.WireGuard.DefaultClientIpv6Address, out localIpv6Address);
        }
        else if (vpnState.VpnProtocol.IsProTun())
        {
            IPAddress.TryParse(_proTunAdapterDetailsCache.ClientIpv6Address, out localIpv6Address);
        }
        else if (vpnState.VpnProtocol.IsOpenVpn())
        {
            // ProtonVPN's OpenVPN server does not provide GUA IPv6, so block all IPv6 tunnel traffic.
            _appFilter.Add(
                _serviceSettings.SplitTunnelSettings.AppPaths,
                [Tuple.Create(Layer.AppAuthConnectV6, Action.HardBlock)]);
        }

        string[] appPaths = _serviceSettings.SplitTunnelSettings.AppPaths ?? [];

        _splitTunnelClient.EnableIncludeMode(appPaths, IPAddress.Parse(vpnState.LocalIp!), localIpv6Address);
        _reverseEnabled = true;
    }

    private void DisableReversed()
    {
        if (_reverseEnabled)
        {
            _splitTunnelClient.Disable();
            _reverseEnabled = false;
        }
    }
}
