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
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts.Routing;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.Settings;
using NetworkAddress = ProtonVPN.Common.Core.Networking.NetworkAddress;

namespace ProtonVPN.Service.ServerHealth;

internal sealed class ServerHealthProbeService : IServerHealthProbeService
{
    private const int ROUTE_SETTLE_DELAY_IN_MILLISECONDS = 50;

    private readonly IConfiguration _configuration;
    private readonly IServiceSettings _serviceSettings;
    private readonly ISystemNetworkInterfaces _networkInterfaces;
    private readonly IRoutingTableHelper _routingTableHelper;
    private readonly IServerHealthPermitManager _permitManager;
    private readonly IServerHealthPingProbe _pingProbe;
    private readonly IIpv6 _ipv6;
    private readonly SemaphoreSlim _probeSlots = new(8, 8);
    private readonly object _addressLocksSync = new();
    private readonly Dictionary<string, AddressLock> _addressLocks = new(StringComparer.OrdinalIgnoreCase);

    internal int ActiveAddressLockCount
    {
        get
        {
            lock (_addressLocksSync)
            {
                return _addressLocks.Count;
            }
        }
    }

    public ServerHealthProbeService(
        IConfiguration configuration,
        IServiceSettings serviceSettings,
        ISystemNetworkInterfaces networkInterfaces,
        IRoutingTableHelper routingTableHelper,
        IServerHealthPermitManager permitManager,
        IServerHealthPingProbe pingProbe,
        IIpv6 ipv6)
    {
        _configuration = configuration;
        _serviceSettings = serviceSettings;
        _networkInterfaces = networkInterfaces;
        _routingTableHelper = routingTableHelper;
        _permitManager = permitManager;
        _pingProbe = pingProbe;
        _ipv6 = ipv6;
    }

    public async Task<ServerHealthProbeResultIpcEntity> ProbeAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ipAddress) ||
            ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return CreateUnavailableResult("Only IPv4 server endpoints can currently be probed directly.");
        }

        using AddressLockLease addressLock =
            await AcquireAddressLockAsync(ipAddress.ToString(), cancellationToken);
        await _probeSlots.WaitAsync(cancellationToken);
        try
        {
            try
            {
                return await ProbeThroughPhysicalAdapterAsync(ipAddress, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return CreateUnavailableResult("The direct server health check could not be completed.");
            }
        }
        finally
        {
            _probeSlots.Release();
        }
    }

    private async Task<ServerHealthProbeResultIpcEntity> ProbeThroughPhysicalAdapterAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken)
    {
        string excludedHardwareId = _configuration.GetHardwareId(_ipv6.VpnProtocol, _serviceSettings.OpenVpnAdapter);
        INetworkInterface physicalInterface = _networkInterfaces.GetBestInterfaceExcludingHardwareId(excludedHardwareId);

        if (!HasUsableGateway(physicalInterface))
        {
            return CreateUnavailableResult("No usable physical network gateway was found.");
        }

        RouteConfiguration directRoute = new()
        {
            Destination = new NetworkAddress(ipAddress),
            Gateway = new NetworkAddress(physicalInterface.DefaultGateway),
            InterfaceIndex = physicalInterface.Index,
            Metric = 1,
            IsIpv6 = false,
        };

        bool routeAlreadyExisted = _routingTableHelper.RouteExists(directRoute);
        bool ownsRoute = false;
        IServerHealthPermitLease? permitLease = null;
        try
        {
            permitLease = _permitManager.TryCreate(ipAddress);
            if (permitLease is null)
            {
                return CreateUnavailableResult("The firewall permit for the direct health check could not be created.");
            }

            if (!routeAlreadyExisted)
            {
                ownsRoute = _routingTableHelper.TryCreateRoute(directRoute);
                if (!ownsRoute && !TryRouteExists(directRoute))
                {
                    return CreateUnavailableResult("The direct route through the physical adapter could not be created.");
                }

                if (ownsRoute && !_routingTableHelper.RouteExists(directRoute))
                {
                    return CreateUnavailableResult("The direct route through the physical adapter could not be created.");
                }
            }

            await Task.Delay(ROUTE_SETTLE_DELAY_IN_MILLISECONDS, cancellationToken);
            return await _pingProbe.MeasureAsync(ipAddress, cancellationToken);
        }
        finally
        {
            if (ownsRoute)
            {
                _routingTableHelper.TryDeleteRoute(directRoute);
            }

            permitLease?.Dispose();
        }
    }

    private bool TryRouteExists(RouteConfiguration route)
    {
        try
        {
            return _routingTableHelper.RouteExists(route);
        }
        catch
        {
            return false;
        }
    }

    private async Task<AddressLockLease> AcquireAddressLockAsync(
        string address,
        CancellationToken cancellationToken)
    {
        AddressLock addressLock;
        lock (_addressLocksSync)
        {
            if (!_addressLocks.TryGetValue(address, out addressLock!))
            {
                addressLock = new AddressLock();
                _addressLocks.Add(address, addressLock);
            }

            addressLock.ReferenceCount++;
        }

        try
        {
            await addressLock.Semaphore.WaitAsync(cancellationToken);
            return new AddressLockLease(this, address, addressLock);
        }
        catch
        {
            ReleaseAddressLockReference(address, addressLock, releaseSemaphore: false);
            throw;
        }
    }

    private void ReleaseAddressLockReference(
        string address,
        AddressLock addressLock,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            addressLock.Semaphore.Release();
        }

        lock (_addressLocksSync)
        {
            addressLock.ReferenceCount--;
            if (addressLock.ReferenceCount == 0 &&
                _addressLocks.TryGetValue(address, out AddressLock? current) &&
                ReferenceEquals(current, addressLock))
            {
                _addressLocks.Remove(address);
                addressLock.Semaphore.Dispose();
            }
        }
    }

    private static bool HasUsableGateway(INetworkInterface networkInterface)
    {
        return networkInterface is not null &&
               networkInterface.Index > 0 &&
               networkInterface.DefaultGateway is not null &&
               !networkInterface.DefaultGateway.Equals(IPAddress.Any) &&
               !networkInterface.DefaultGateway.Equals(IPAddress.None);
    }

    private static ServerHealthProbeResultIpcEntity CreateUnavailableResult(string error)
    {
        return new()
        {
            AverageLatencyMilliseconds = null,
            PacketLossPercent = 100,
            SuccessfulSamples = 0,
            TotalSamples = ServerHealthPingProbe.ProbeSampleCount,
            CheckedAtUtc = DateTime.UtcNow,
            UsedPhysicalRoute = false,
            Error = error,
        };
    }

    private sealed class AddressLock
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class AddressLockLease : IDisposable
    {
        private readonly ServerHealthProbeService _owner;
        private readonly string _address;
        private readonly AddressLock _addressLock;
        private bool _isDisposed;

        public AddressLockLease(
            ServerHealthProbeService owner,
            string address,
            AddressLock addressLock)
        {
            _owner = owner;
            _address = address;
            _addressLock = addressLock;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _owner.ReleaseAddressLockReference(
                _address,
                _addressLock,
                releaseSemaphore: true);
        }
    }
}
