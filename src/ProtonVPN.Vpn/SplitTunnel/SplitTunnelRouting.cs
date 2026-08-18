/*
 * Copyright (c) 2026 Proton AG
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
using System.Net;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.NetworkLogs;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts.Routing;
using ProtonVPN.Vpn.Gateways;
using Timer = System.Timers.Timer;

namespace ProtonVPN.Vpn.SplitTunnel;

public class SplitTunnelRouting : ISplitTunnelRouting, IDisposable
{
    private const int PERMIT_ROUTE_METRIC = 32000;
    private const uint FALLBACK_EXCLUDE_ROUTE_METRIC = 1;
    private static readonly TimeSpan RouteReconciliationInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger;
    private readonly IStaticConfiguration _config;
    private readonly IGatewayCache _gatewayCache;
    private readonly IIpv4GatewayResolver _ipv4GatewayResolver;
    private readonly IRoutingTableHelper _routingTableHelper;
    private readonly INetworkUtilities _networkUtilities;
    private readonly ISystemNetworkInterfaces _networkInterfaces;
    private readonly INetworkInterfaceProvider _networkInterfaceProvider;
    private readonly object _trackedRoutesSync = new();
    private readonly List<RouteConfiguration> _trackedRoutes = [];
    private readonly Timer _reconciliationTimer;

    private bool _isDisposed;

    public SplitTunnelRouting(
        ILogger logger,
        IStaticConfiguration config,
        IGatewayCache gatewayCache,
        IIpv4GatewayResolver ipv4GatewayResolver,
        IRoutingTableHelper routingTableHelper,
        INetworkUtilities networkUtilities,
        ISystemNetworkInterfaces networkInterfaces,
        INetworkInterfaceProvider networkInterfaceProvider)
    {
        _logger = logger;
        _config = config;
        _gatewayCache = gatewayCache;
        _ipv4GatewayResolver = ipv4GatewayResolver;
        _routingTableHelper = routingTableHelper;
        _networkUtilities = networkUtilities;
        _networkInterfaces = networkInterfaces;
        _networkInterfaceProvider = networkInterfaceProvider;

        _reconciliationTimer = new(RouteReconciliationInterval)
        {
            AutoReset = true,
        };
        _reconciliationTimer.Elapsed += OnReconciliationTimerElapsed;
        _reconciliationTimer.Start();
    }

    public void SetUpRoutingTable(VpnConfig vpnConfig, string localIp, bool isIpv6Supported)
    {
        ClearTrackedRoutes();

        INetworkInterface tunnelInterface = _networkInterfaceProvider.GetByVpnProtocol(vpnConfig.VpnProtocol, vpnConfig.OpenVpnAdapter);
        INetworkInterface[] networkInterfaces = _networkInterfaces.GetInterfaces();

        switch (vpnConfig.SplitTunnelMode)
        {
            case SplitTunnelMode.Permit:
                SetUpPermitModeRoutes(vpnConfig, localIp, isIpv6Supported, tunnelInterface, networkInterfaces);
                break;
            case SplitTunnelMode.Block:
                SetUpBlockModeRoutes(vpnConfig, tunnelInterface, networkInterfaces);
                break;
        }
    }

    private void SetUpPermitModeRoutes(
        VpnConfig vpnConfig,
        string localIpv4Address,
        bool isIpv6Supported,
        INetworkInterface tunnelInterface,
        INetworkInterface[] networkInterfaces)
    {
        IPAddress? gatewayAddress = _gatewayCache.Get();
        if (gatewayAddress is null)
        {
            _logger.Error<NetworkLog>("Failed to configure IP split tunnel routes because the gateway address is missing.");
            return;
        }

        NetworkAddress.TryParse("0.0.0.0/0", out NetworkAddress defaultIpv4NetworkAddress);
        NetworkAddress.TryParse("::/0", out NetworkAddress defaultIpv6NetworkAddress);
        NetworkAddress.TryParse(localIpv4Address, out NetworkAddress localNetworkIpv4Address);
        NetworkAddress serverGatewayIpv4Address = new(gatewayAddress);

        NetworkAddress.TryParse(_config.WireGuard.DefaultServerGatewayIpv6Address, out NetworkAddress serverGatewayIpv6Address);

        _routingTableHelper.DeleteRoute(new()
        {
            Destination = defaultIpv4NetworkAddress,
            Gateway = localNetworkIpv4Address,
            InterfaceIndex = tunnelInterface.Index,
            IsIpv6 = false,
        });

        CreateTrackedRoute(new()
        {
            Destination = defaultIpv4NetworkAddress,
            Gateway = localNetworkIpv4Address,
            InterfaceIndex = tunnelInterface.Index,
            Metric = PERMIT_ROUTE_METRIC,
            IsIpv6 = false,
        });

        CreateTrackedRoute(new()
        {
            Destination = serverGatewayIpv4Address,
            Gateway = localNetworkIpv4Address,
            InterfaceIndex = tunnelInterface.Index,
            Metric = PERMIT_ROUTE_METRIC,
            IsIpv6 = false,
        });

        if (isIpv6Supported)
        {
            _routingTableHelper.DeleteRoute(new()
            {
                Destination = defaultIpv6NetworkAddress,
                Gateway = defaultIpv6NetworkAddress,
                InterfaceIndex = tunnelInterface.Index,
                IsIpv6 = true,
            });

            CreateTrackedRoute(new()
            {
                Destination = defaultIpv6NetworkAddress,
                Gateway = defaultIpv6NetworkAddress,
                InterfaceIndex = tunnelInterface.Index,
                Metric = PERMIT_ROUTE_METRIC,
                IsIpv6 = true,
            });

            NetworkAddress? ipv6GatewayAddress = _networkUtilities.GetDefaultIpv6Gateway(tunnelInterface, networkInterfaces);
            if (ipv6GatewayAddress is null)
            {
                CreateTrackedRoute(GetIpv6LoopbackRoute(defaultIpv6NetworkAddress));
            }
        }

        foreach (string ip in vpnConfig.SplitTunnelIPs)
        {
            if (NetworkAddress.TryParse(ip, out NetworkAddress address))
            {
                CreateTrackedRoute(new()
                {
                    Destination = address,
                    Gateway = address.IsIpV6 ? serverGatewayIpv6Address : localNetworkIpv4Address,
                    InterfaceIndex = tunnelInterface.Index,
                    Metric = PERMIT_ROUTE_METRIC,
                    IsIpv6 = address.IsIpV6,
                });
            }
        }
    }

    private RouteConfiguration GetIpv6LoopbackRoute(NetworkAddress destination)
    {
        return new()
        {
            Destination = destination,
            Gateway = null,
            InterfaceIndex = _routingTableHelper.GetLoopbackInterfaceIndex() ?? 0,
            Metric = 0,
            IsIpv6 = true,
        };
    }

    private void SetUpBlockModeRoutes(VpnConfig vpnConfig, INetworkInterface tunnelInterface, INetworkInterface[] networkInterfaces)
    {
        if (!TryGetBlockModeIpv4Gateway(vpnConfig, out INetworkInterface? bestIpv4Interface, out NetworkAddress? ipv4GatewayAddress, out uint ipv4RouteMetric))
        {
            return;
        }

        NetworkAddress? ipv6GatewayAddress = _networkUtilities.GetDefaultIpv6Gateway(tunnelInterface, networkInterfaces);
        uint? loopbackInterfaceIndex = _routingTableHelper.GetLoopbackInterfaceIndex();

        foreach (string ip in vpnConfig.SplitTunnelIPs)
        {
            if (NetworkAddress.TryParse(ip, out NetworkAddress address))
            {
                NetworkAddress? gateway = address.IsIpV6 ? ipv6GatewayAddress : ipv4GatewayAddress;
                uint? interfaceIndex = gateway is null ? loopbackInterfaceIndex : bestIpv4Interface!.Index;

                if (interfaceIndex is null)
                {
                    _logger.Error<NetworkLog>($"Ignoring route create with IP {address} address due to a missing interface index.");
                    continue;
                }

                CreateTrackedRoute(new()
                {
                    Destination = address,
                    Gateway = gateway,
                    InterfaceIndex = interfaceIndex.Value,
                    Metric = ipv4RouteMetric,
                    IsIpv6 = address.IsIpV6,
                });
            }
        }
    }

    private bool TryGetBlockModeIpv4Gateway(
        VpnConfig vpnConfig,
        out INetworkInterface? bestIpv4Interface,
        out NetworkAddress? ipv4GatewayAddress,
        out uint ipv4RouteMetric)
    {
        string excludedHardwareId = _config.GetHardwareId(vpnConfig.VpnProtocol, vpnConfig.OpenVpnAdapter);

        if (_ipv4GatewayResolver.TryGetBestIpv4Gateway(excludedHardwareId, out Ipv4GatewayInfo? ipv4GatewayInfo) && ipv4GatewayInfo is not null)
        {
            bestIpv4Interface = ipv4GatewayInfo.Interface;
            ipv4GatewayAddress = ipv4GatewayInfo.GatewayAddress;
            ipv4RouteMetric = ipv4GatewayInfo.InterfaceMetric;
            return true;
        }

        bestIpv4Interface = _networkInterfaces.GetBestInterfaceExcludingHardwareId(excludedHardwareId);
        IPAddress? defaultGateway = bestIpv4Interface?.DefaultGateway;

        if (bestIpv4Interface is null ||
            bestIpv4Interface.Index == 0 ||
            defaultGateway is null ||
            defaultGateway.Equals(IPAddress.Any) ||
            defaultGateway.Equals(IPAddress.None) ||
            !NetworkAddress.TryParse(defaultGateway.ToString(), out NetworkAddress fallbackGatewayAddress))
        {
            _logger.Error<NetworkLog>("Failed to configure split tunnel exclusion routes because no usable physical IPv4 gateway was found.");
            ipv4GatewayAddress = null;
            ipv4RouteMetric = 0;
            return false;
        }

        ipv4GatewayAddress = fallbackGatewayAddress;
        ipv4RouteMetric = FALLBACK_EXCLUDE_ROUTE_METRIC;
        _logger.Warn<NetworkLog>(
            $"IPv4 gateway metric lookup failed for interface {bestIpv4Interface.Index}; " +
            $"using the physical gateway {defaultGateway} with split tunnel route metric {FALLBACK_EXCLUDE_ROUTE_METRIC}.");
        return true;
    }

    private void CreateTrackedRoute(RouteConfiguration route)
    {
        lock (_trackedRoutesSync)
        {
            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(SplitTunnelRouting));
            }

            _routingTableHelper.CreateRoute(route);
            _trackedRoutes.Add(route);
        }
    }

    private void ClearTrackedRoutes()
    {
        lock (_trackedRoutesSync)
        {
            _trackedRoutes.Clear();
        }
    }

    private void OnReconciliationTimerElapsed(object? sender, EventArgs e)
    {
        ReconcileTrackedRoutes();
    }

    internal void ReconcileTrackedRoutes()
    {
        lock (_trackedRoutesSync)
        {
            if (_isDisposed)
            {
                return;
            }

            foreach (RouteConfiguration route in _trackedRoutes)
            {
                try
                {
                    if (_routingTableHelper.RouteExists(route))
                    {
                        continue;
                    }

                    _logger.Warn<NetworkLog>(
                        $"Tracked split tunnel route is missing and will be recreated. " +
                        $"Destination={route.Destination}, InterfaceIndex={route.InterfaceIndex}, Gateway={route.Gateway}.");
                    _routingTableHelper.CreateRoute(route);
                }
                catch (Exception e)
                {
                    _logger.Error<NetworkLog>(
                        $"Failed to reconcile split tunnel route. Destination={route.Destination}, " +
                        $"InterfaceIndex={route.InterfaceIndex}, Gateway={route.Gateway}.",
                        e);
                }
            }
        }
    }

    public void DeleteRoutes(VpnConfig vpnConfig)
    {
        ClearTrackedRoutes();

        switch (vpnConfig.SplitTunnelMode)
        {
            case SplitTunnelMode.Block:
                foreach (string ip in vpnConfig.SplitTunnelIPs)
                {
                    if (NetworkAddress.TryParse(ip, out NetworkAddress address))
                    {
                        _routingTableHelper.DeleteRoute(address.Ip.ToString(), address.IsIpV6);
                    }
                }
                break;
            case SplitTunnelMode.Permit:
                foreach (string ip in vpnConfig.SplitTunnelIPs)
                {
                    if (NetworkAddress.TryParse(ip, out NetworkAddress address))
                    {
                        _routingTableHelper.DeleteRoute(address.Ip.ToString(), address.IsIpV6);
                    }
                }

                if (NetworkAddress.TryParse("::/0", out NetworkAddress defaultIpv6NetworkAddress))
                {
                    _routingTableHelper.DeleteRoute(GetIpv6LoopbackRoute(defaultIpv6NetworkAddress));
                }
                break;
        }
    }

    public void Dispose()
    {
        lock (_trackedRoutesSync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _trackedRoutes.Clear();
        }

        _reconciliationTimer.Stop();
        _reconciliationTimer.Elapsed -= OnReconciliationTimerElapsed;
        _reconciliationTimer.Dispose();
    }
}
