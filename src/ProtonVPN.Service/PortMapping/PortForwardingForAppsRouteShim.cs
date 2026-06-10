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
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.PortForwarding;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.ConnectionLogs;
using ProtonVPN.Service.Settings;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.PortMapping;

namespace ProtonVPN.Service.PortMapping;

internal sealed class PortForwardingForAppsRouteShim : IDisposable
{
    private const string ProtonNatPmpGatewayIp = "10.2.0.1";
    private const string DefaultRoutePrefix = "0.0.0.0/0";

    private readonly object _sync = new();
    private readonly ILogger _logger;
    private readonly IServiceSettings _serviceSettings;
    private readonly IPortMappingProtocolClient _portMappingProtocolClient;

    private VpnState _vpnState = VpnState.Default;
    private PortForwardingState _portForwardingState = PortForwardingState.Default;
    private int? _routeInterfaceIndex;

    public PortForwardingForAppsRouteShim(
        ILogger logger,
        IServiceSettings serviceSettings,
        IPortMappingProtocolClient portMappingProtocolClient)
    {
        _logger = logger;
        _serviceSettings = serviceSettings;
        _portMappingProtocolClient = portMappingProtocolClient;

        _serviceSettings.SettingsChanged += OnSettingsChanged;
        _portMappingProtocolClient.StateChanged += OnPortMappingStateChanged;
    }

    public void SetVpnState(VpnState vpnState)
    {
        lock (_sync)
        {
            _vpnState = vpnState ?? VpnState.Default;
        }

        ReconcileState();
    }

    public async Task StopAsync()
    {
        RemoveRouteIfNeeded();
        await Task.CompletedTask;
    }

    private void OnSettingsChanged(object sender, ProtonVPN.ProcessCommunication.Contracts.Entities.Settings.MainSettingsIpcEntity e)
    {
        ReconcileState();
    }

    private void OnPortMappingStateChanged(object sender, EventArgs<PortForwardingState> e)
    {
        lock (_sync)
        {
            _portForwardingState = e.Data ?? PortForwardingState.Default;
        }

        ReconcileState();
    }

    private void ReconcileState()
    {
        if (ShouldRun())
        {
            AddRouteIfNeeded();
        }
        else
        {
            RemoveRouteIfNeeded();
        }
    }

    private bool ShouldRun()
    {
        lock (_sync)
        {
            return _serviceSettings.IsPortForwardingForAppsEnabled &&
                   _vpnState.Status == VpnStatus.Connected &&
                   _vpnState.PortForwarding &&
                   IPAddress.TryParse(_vpnState.LocalIp, out _) &&
                   _portForwardingState.Status == PortMappingStatus.SleepingUntilRefresh &&
                   _portForwardingState.MappedPort?.MappedPort?.ExternalPort > 0;
        }
    }

    private string LocalIp
    {
        get
        {
            lock (_sync)
            {
                return _vpnState.LocalIp;
            }
        }
    }

    private void AddRouteIfNeeded()
    {
        int interfaceIndex = GetInterfaceIndexForLocalIp(LocalIp);
        if (interfaceIndex <= 0)
        {
            _logger.Error<ConnectionLog>($"Could not find Proton VPN interface index for local IP {LocalIp}.");
            return;
        }

        lock (_sync)
        {
            if (_routeInterfaceIndex == interfaceIndex)
            {
                return;
            }
        }

        RemoveRouteIfNeeded();
        DeleteRoute(interfaceIndex);

        try
        {
            RunNetsh($"interface ipv4 add route prefix={DefaultRoutePrefix} interface={interfaceIndex} nexthop={ProtonNatPmpGatewayIp} metric=1 store=active");
            lock (_sync)
            {
                _routeInterfaceIndex = interfaceIndex;
            }

            _logger.Info<ConnectionLog>($"Added app port forwarding NAT-PMP route shim. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}.");
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>($"Failed to add app port forwarding NAT-PMP route shim. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}.", e);
        }
    }

    private void RemoveRouteIfNeeded()
    {
        int? interfaceIndex;
        lock (_sync)
        {
            interfaceIndex = _routeInterfaceIndex;
            _routeInterfaceIndex = null;
        }

        if (interfaceIndex is null)
        {
            return;
        }

        DeleteRoute(interfaceIndex.Value);
    }

    private void DeleteRoute(int interfaceIndex)
    {
        try
        {
            RunNetsh($"interface ipv4 delete route prefix={DefaultRoutePrefix} interface={interfaceIndex} nexthop={ProtonNatPmpGatewayIp} store=active");
            _logger.Info<ConnectionLog>($"Removed app port forwarding NAT-PMP route shim. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}.");
        }
        catch (Exception e)
        {
            _logger.Warn<ConnectionLog>($"App port forwarding NAT-PMP route shim was not present or could not be removed. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}. {e.Message}");
        }
    }

    private static int GetInterfaceIndexForLocalIp(string localIp)
    {
        if (!IPAddress.TryParse(localIp, out IPAddress address))
        {
            return 0;
        }

        foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties = networkInterface.GetIPProperties();
            UnicastIPAddressInformation match = properties.UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     a.Address.Equals(address));

            if (match is not null)
            {
                return properties.GetIPv4Properties()?.Index ?? 0;
            }
        }

        return 0;
    }

    private static void RunNetsh(string arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }
        };

        process.Start();

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill();
            }
            catch
            {
            }

            throw new InvalidOperationException($"netsh timed out. Arguments: {arguments}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"netsh failed with exit code {process.ExitCode}. Arguments: {arguments}. Output: {output}. Error: {error}");
        }
    }

    public void Dispose()
    {
        _serviceSettings.SettingsChanged -= OnSettingsChanged;
        _portMappingProtocolClient.StateChanged -= OnPortMappingStateChanged;
        RemoveRouteIfNeeded();
    }
}
