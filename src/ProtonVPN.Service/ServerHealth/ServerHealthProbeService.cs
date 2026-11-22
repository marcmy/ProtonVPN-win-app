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
    private const int MAX_CONCURRENT_ECHOES = 32;
    private const int MAX_CONCURRENT_FIXED_CHECKS = 4;

    private readonly IConfiguration _configuration;
    private readonly IServiceSettings _serviceSettings;
    private readonly ISystemNetworkInterfaces _networkInterfaces;
    private readonly IRoutingTableHelper _routingTableHelper;
    private readonly IServerHealthPermitManager _permitManager;
    private readonly IServerHealthPingProbe _pingProbe;
    private readonly IIpv6 _ipv6;
    private readonly Func<RouteConfiguration, string?> _getRouteContext;
    private readonly SemaphoreSlim _echoSlots = new(MAX_CONCURRENT_ECHOES, MAX_CONCURRENT_ECHOES);
    private readonly SemaphoreSlim _fixedCheckSlots = new(MAX_CONCURRENT_FIXED_CHECKS, MAX_CONCURRENT_FIXED_CHECKS);
    private readonly SemaphoreSlim _fixedReservation = new(1, 1);
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
        : this(configuration, serviceSettings, networkInterfaces, routingTableHelper, permitManager, pingProbe,
            ipv6, ServerHealthRouteContext.Get)
    {
    }

    internal ServerHealthProbeService(
        IConfiguration configuration,
        IServiceSettings serviceSettings,
        ISystemNetworkInterfaces networkInterfaces,
        IRoutingTableHelper routingTableHelper,
        IServerHealthPermitManager permitManager,
        IServerHealthPingProbe pingProbe,
        IIpv6 ipv6,
        Func<RouteConfiguration, string?> getRouteContext)
    {
        _configuration = configuration;
        _serviceSettings = serviceSettings;
        _networkInterfaces = networkInterfaces;
        _routingTableHelper = routingTableHelper;
        _permitManager = permitManager;
        _pingProbe = pingProbe;
        _ipv6 = ipv6;
        _getRouteContext = getRouteContext;
    }

    public async Task<ServerHealthProbeResultIpcEntity> ProbeAsync(
        string address,
        CancellationToken cancellationToken,
        bool quickFirstResponse = false)
    {
        // An unspecified address would create a default route.
        if (!IPAddress.TryParse(address, out IPAddress? ipAddress) ||
            ipAddress.AddressFamily != AddressFamily.InterNetwork || ipAddress.Equals(IPAddress.Any))
        {
            return CreateUnavailableResult("The endpoint must be a valid IPv4 server address.");
        }

        using AddressLockLease addressLock =
            await AcquireAddressLockAsync(ipAddress.ToString(), cancellationToken);
        await ReserveEchoSlotsAsync(quickFirstResponse, cancellationToken);
        try
        {
            try
            {
                return await ProbeThroughPhysicalAdapterAsync(ipAddress, cancellationToken, quickFirstResponse);
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
            if (quickFirstResponse)
            {
                _echoSlots.Release();
            }
            else
            {
                _echoSlots.Release(ServerHealthPingProbe.ProbeSampleCount);
                _fixedCheckSlots.Release();
            }
        }
    }

    private async Task ReserveEchoSlotsAsync(bool quickFirstResponse, CancellationToken cancellationToken)
    {
        if (quickFirstResponse)
        {
            await _echoSlots.WaitAsync(cancellationToken);
            return;
        }

        // Keep capacity for discovery and reserve the entire burst before taking a route lease.
        await _fixedCheckSlots.WaitAsync(cancellationToken);
        int reserved = 0;
        try
        {
            // Only one burst can hold a partial reservation while waiting for more slots.
            await _fixedReservation.WaitAsync(cancellationToken);
            try
            {
                while (reserved < ServerHealthPingProbe.ProbeSampleCount)
                {
                    await _echoSlots.WaitAsync(cancellationToken);
                    reserved++;
                }
            }
            finally
            {
                _fixedReservation.Release();
            }
        }
        catch
        {
            if (reserved > 0)
            {
                _echoSlots.Release(reserved);
            }

            _fixedCheckSlots.Release();
            throw;
        }
    }

    private async Task<ServerHealthProbeResultIpcEntity> ProbeThroughPhysicalAdapterAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken,
        bool quickFirstResponse)
    {
        string excludedHardwareId = _configuration.GetHardwareId(_ipv6.VpnProtocol, _serviceSettings.OpenVpnAdapter);
        INetworkInterface? physicalInterface = _networkInterfaces.GetBestInterfaceExcludingHardwareId(excludedHardwareId);
        uint interfaceIndex = physicalInterface?.Index ?? 0;
        IPAddress? gateway = physicalInterface?.DefaultGateway;

        if (interfaceIndex == 0 || gateway is null || gateway.AddressFamily != AddressFamily.InterNetwork ||
            gateway.Equals(IPAddress.Any) || gateway.Equals(IPAddress.None))
        {
            return CreateUnavailableResult("No usable physical network gateway was found.");
        }

        RouteConfiguration directRoute = new()
        {
            Destination = new NetworkAddress(ipAddress),
            Gateway = new NetworkAddress(gateway),
            InterfaceIndex = interfaceIndex,
            Metric = 1,
            IsIpv6 = false,
        };

        bool routeAlreadyExisted = _routingTableHelper.RouteExists(directRoute);
        bool ownsRoute = false;
        IServerHealthPermitLease? permitLease = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            string? routeContext = _getRouteContext(directRoute);
            if (routeContext is null)
            {
                return CreateUnavailableResult("The direct route through the physical adapter could not be verified.");
            }

            ServerHealthProbeResultIpcEntity result =
                await _pingProbe.MeasureAsync(ipAddress, cancellationToken, quickFirstResponse);
            cancellationToken.ThrowIfCancellationRequested();
            if (_getRouteContext(directRoute) != routeContext)
            {
                return CreateUnavailableResult("The direct route changed during the health check.");
            }

            result.UsedPhysicalRoute = true;
            result.RouteContext = routeContext;
            return result;
        }
        finally
        {
            try
            {
                if (ownsRoute)
                {
                    _routingTableHelper.TryDeleteRoute(directRoute);
                }
            }
            finally
            {
                permitLease?.Dispose();
            }
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

    private static ServerHealthProbeResultIpcEntity CreateUnavailableResult(string error)
    {
        return new()
        {
            CheckedAtUtc = DateTime.UtcNow,
            Samples = [],
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
