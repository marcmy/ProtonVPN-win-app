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
using System.Net;
using System.Threading.Tasks;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.PortForwarding;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.ConnectionLogs;
using ProtonVPN.Service.Settings;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.PortMapping;
using Timer = System.Timers.Timer;

namespace ProtonVPN.Service.PortMapping;

internal sealed class PortForwardingForAppsRouteShim : IDisposable
{
    private const string ProtonNatPmpGatewayIp = "10.2.0.1";
    private static readonly TimeSpan RouteReconciliationInterval = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly object _reconcileSync = new();
    private readonly ILogger _logger;
    private readonly IServiceSettings _serviceSettings;
    private readonly IPortMappingProtocolClient _portMappingProtocolClient;
    private readonly IPortForwardingRouteOperations _routeOperations;
    private readonly Timer _reconciliationTimer;

    private VpnState _vpnState = VpnState.Default;
    private int? _routeInterfaceIndex;
    private bool _isStopped;
    private bool _isDisposed;

    public PortForwardingForAppsRouteShim(
        ILogger logger,
        IServiceSettings serviceSettings,
        IPortMappingProtocolClient portMappingProtocolClient,
        IPortForwardingRouteOperations routeOperations)
        : this(
            logger,
            serviceSettings,
            portMappingProtocolClient,
            routeOperations,
            RouteReconciliationInterval)
    {
    }

    internal PortForwardingForAppsRouteShim(
        ILogger logger,
        IServiceSettings serviceSettings,
        IPortMappingProtocolClient portMappingProtocolClient,
        IPortForwardingRouteOperations routeOperations,
        TimeSpan reconciliationInterval)
    {
        _logger = logger;
        _serviceSettings = serviceSettings;
        _portMappingProtocolClient = portMappingProtocolClient;
        _routeOperations = routeOperations;

        _reconciliationTimer = new(reconciliationInterval)
        {
            AutoReset = true,
        };
        _reconciliationTimer.Elapsed += OnReconciliationTimerElapsed;

        _serviceSettings.SettingsChanged += OnSettingsChanged;
        _portMappingProtocolClient.StateChanged += OnPortMappingStateChanged;
        _reconciliationTimer.Start();
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
        lock (_sync)
        {
            _isStopped = true;
        }

        _reconciliationTimer.Stop();

        lock (_reconcileSync)
        {
            RemoveRouteIfNeeded();
        }

        await Task.CompletedTask;
    }

    private void OnSettingsChanged(object? sender, ProtonVPN.ProcessCommunication.Contracts.Entities.Settings.MainSettingsIpcEntity e)
    {
        ReconcileState();
    }

    private void OnPortMappingStateChanged(object? sender, EventArgs<PortForwardingState> e)
    {
        ReconcileState();
    }

    private void OnReconciliationTimerElapsed(object? sender, EventArgs e)
    {
        ReconcileState();
    }

    private void ReconcileState()
    {
        lock (_reconcileSync)
        {
            if (ShouldKeepRouteInstalled())
            {
                AddRouteIfNeeded();
            }
            else
            {
                RemoveRouteIfNeeded();
            }
        }
    }

    private bool ShouldKeepRouteInstalled()
    {
        lock (_sync)
        {
            // NAT-PMP clients need this route while creating and renewing mappings, so the
            // transient PortMappingStatus must not control the route lifetime.
            return _serviceSettings.IsPortForwardingForAppsEnabled &&
                   !_isStopped &&
                   !_isDisposed &&
                   _vpnState.Status == VpnStatus.Connected &&
                   _vpnState.PortForwarding &&
                   IPAddress.TryParse(_vpnState.LocalIp, out _);
        }
    }

    private string? LocalIp
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
        string? localIp = LocalIp;
        int interfaceIndex = _routeOperations.GetInterfaceIndexForLocalIp(localIp);
        if (interfaceIndex <= 0)
        {
            _logger.Error<ConnectionLog>($"Could not find Proton VPN interface index for local IP {localIp}.");
            return;
        }

        bool isTrackedOnInterface;
        lock (_sync)
        {
            isTrackedOnInterface = _routeInterfaceIndex == interfaceIndex;
        }

        if (isTrackedOnInterface && IsRoutePresent(interfaceIndex))
        {
            return;
        }

        if (isTrackedOnInterface)
        {
            lock (_sync)
            {
                if (_routeInterfaceIndex == interfaceIndex)
                {
                    _routeInterfaceIndex = null;
                }
            }

            _logger.Warn<ConnectionLog>($"Tracked app port forwarding NAT-PMP route shim is missing and will be recreated. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}.");
        }

        RemoveRouteIfNeeded();
        TryDeleteRoute(interfaceIndex);

        try
        {
            _routeOperations.AddRoute(interfaceIndex);
            lock (_sync)
            {
                _routeInterfaceIndex = interfaceIndex;
            }

            _logger.Info<ConnectionLog>($"Added app port forwarding NAT-PMP route shim. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}.");

            if (!ShouldKeepRouteInstalled())
            {
                RemoveRouteIfNeeded();
            }
        }
        catch (Exception e)
        {
            TryDeleteRoute(interfaceIndex);
            _logger.Error<ConnectionLog>($"Failed to add app port forwarding NAT-PMP route shim. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}.", e);
        }
    }

    private void RemoveRouteIfNeeded()
    {
        int? interfaceIndex;
        lock (_sync)
        {
            interfaceIndex = _routeInterfaceIndex;
        }

        if (interfaceIndex is null)
        {
            return;
        }

        if (TryDeleteRoute(interfaceIndex.Value))
        {
            lock (_sync)
            {
                if (_routeInterfaceIndex == interfaceIndex)
                {
                    _routeInterfaceIndex = null;
                }
            }
        }
    }

    private bool TryDeleteRoute(int interfaceIndex)
    {
        try
        {
            _routeOperations.DeleteRoute(interfaceIndex);
            _logger.Info<ConnectionLog>($"Removed app port forwarding NAT-PMP route shim. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}.");
            return true;
        }
        catch (Exception e)
        {
            _logger.Warn<ConnectionLog>($"App port forwarding NAT-PMP route shim was not present or could not be removed. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}. {e.Message}");
            return false;
        }
    }

    private bool IsRoutePresent(int interfaceIndex)
    {
        try
        {
            return _routeOperations.RouteExists(interfaceIndex);
        }
        catch (Exception e)
        {
            _logger.Warn<ConnectionLog>($"Could not verify the app port forwarding NAT-PMP route shim. InterfaceIndex={interfaceIndex}, NextHop={ProtonNatPmpGatewayIp}. {e.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isStopped = true;
        }

        _reconciliationTimer.Stop();
        _reconciliationTimer.Elapsed -= OnReconciliationTimerElapsed;
        _reconciliationTimer.Dispose();
        _serviceSettings.SettingsChanged -= OnSettingsChanged;
        _portMappingProtocolClient.StateChanged -= OnPortMappingStateChanged;
        lock (_reconcileSync)
        {
            RemoveRouteIfNeeded();
        }
    }
}
