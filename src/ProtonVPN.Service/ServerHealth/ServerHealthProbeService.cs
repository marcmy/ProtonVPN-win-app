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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.NetworkFilter;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts.Routing;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.Settings;
using FilterAction = ProtonVPN.NetworkFilter.Action;
using FilterNetworkAddress = ProtonVPN.NetworkFilter.NetworkAddress;
using ServiceIpFilter = ProtonVPN.Service.Firewall.IpFilter;

namespace ProtonVPN.Service.ServerHealth;

public sealed class ServerHealthProbeService : IServerHealthProbeService
{
    private const int PROBE_SAMPLE_COUNT = 4;
    private const int PROBE_TIMEOUT_IN_MILLISECONDS = 500;
    private const int ROUTE_SETTLE_DELAY_IN_MILLISECONDS = 50;
    private static readonly TimeSpan _delayBetweenSamples = TimeSpan.FromMilliseconds(100);

    private readonly IConfiguration _configuration;
    private readonly IServiceSettings _serviceSettings;
    private readonly ISystemNetworkInterfaces _networkInterfaces;
    private readonly IRoutingTableHelper _routingTableHelper;
    private readonly ServiceIpFilter _ipFilter;
    private readonly IpLayer _ipLayer;
    private readonly SemaphoreSlim _probeSlots = new(8, 8);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _addressLocks = new(StringComparer.OrdinalIgnoreCase);

    public ServerHealthProbeService(
        IConfiguration configuration,
        IServiceSettings serviceSettings,
        ISystemNetworkInterfaces networkInterfaces,
        IRoutingTableHelper routingTableHelper,
        ServiceIpFilter ipFilter,
        IpLayer ipLayer)
    {
        _configuration = configuration;
        _serviceSettings = serviceSettings;
        _networkInterfaces = networkInterfaces;
        _routingTableHelper = routingTableHelper;
        _ipFilter = ipFilter;
        _ipLayer = ipLayer;
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

        await _probeSlots.WaitAsync(cancellationToken);
        try
        {
            SemaphoreSlim addressLock = _addressLocks.GetOrAdd(ipAddress.ToString(), _ => new SemaphoreSlim(1, 1));
            await addressLock.WaitAsync(cancellationToken);
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
            finally
            {
                addressLock.Release();
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
        string excludedHardwareId = _configuration.GetHardwareId(_serviceSettings.OpenVpnAdapter);
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
        List<Guid> permitFilterIds = [];

        try
        {
            permitFilterIds = CreatePermitFilters(ipAddress);
            if (permitFilterIds.Count == 0)
            {
                return CreateUnavailableResult("The firewall permit for the direct health check could not be created.");
            }

            if (!routeAlreadyExisted)
            {
                _routingTableHelper.CreateRoute(directRoute);
                ownsRoute = _routingTableHelper.RouteExists(directRoute);
                if (!ownsRoute)
                {
                    return CreateUnavailableResult("The direct route through the physical adapter could not be created.");
                }
            }

            await Task.Delay(ROUTE_SETTLE_DELAY_IN_MILLISECONDS, cancellationToken);
            return await MeasureAsync(ipAddress, cancellationToken);
        }
        finally
        {
            if (ownsRoute)
            {
                try
                {
                    _routingTableHelper.DeleteRoute(directRoute);
                }
                catch
                {
                }
            }

            RemovePermitFilters(permitFilterIds);
        }
    }

    private async Task<ServerHealthProbeResultIpcEntity> MeasureAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken)
    {
        List<long> successfulRoundTrips = [];

        using Ping ping = new();
        for (int sampleIndex = 0; sampleIndex < PROBE_SAMPLE_COUNT; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                PingReply reply = await ping.SendPingAsync(ipAddress, PROBE_TIMEOUT_IN_MILLISECONDS);
                if (reply.Status == IPStatus.Success)
                {
                    successfulRoundTrips.Add(reply.RoundtripTime);
                }
            }
            catch (Exception exception) when (exception is PingException or InvalidOperationException)
            {
            }

            if (sampleIndex < PROBE_SAMPLE_COUNT - 1)
            {
                await Task.Delay(_delayBetweenSamples, cancellationToken);
            }
        }

        return new ServerHealthProbeResultIpcEntity
        {
            AverageLatencyMilliseconds = successfulRoundTrips.Count > 0
                ? successfulRoundTrips.Average()
                : null,
            PacketLossPercent = (PROBE_SAMPLE_COUNT - successfulRoundTrips.Count) * 100d / PROBE_SAMPLE_COUNT,
            SuccessfulSamples = successfulRoundTrips.Count,
            TotalSamples = PROBE_SAMPLE_COUNT,
            CheckedAtUtc = DateTime.UtcNow,
            UsedPhysicalRoute = true,
            Error = successfulRoundTrips.Count == 0
                ? "No ICMP replies were received. The server may block ping; this does not necessarily mean it is offline."
                : null,
        };
    }

    private List<Guid> CreatePermitFilters(IPAddress ipAddress)
    {
        List<Guid> filterIds = [];

        try
        {
            _ipLayer.ApplyToIpv4(layer =>
            {
                Guid filterId = _ipFilter.DynamicSublayer.CreateRemoteNetworkIPFilter(
                    new DisplayData("ProtonVPN server health direct probe", string.Empty),
                    FilterAction.HardPermit,
                    layer,
                    14,
                    FilterNetworkAddress.FromIpv4(ipAddress.ToString(), "255.255.255.255"));
                filterIds.Add(filterId);
            });

            return filterIds;
        }
        catch
        {
            RemovePermitFilters(filterIds);
            return [];
        }
    }

    private void RemovePermitFilters(IEnumerable<Guid> filterIds)
    {
        foreach (Guid filterId in filterIds)
        {
            try
            {
                _ipFilter.DynamicSublayer.DestroyFilter(filterId);
            }
            catch
            {
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
            TotalSamples = PROBE_SAMPLE_COUNT,
            CheckedAtUtc = DateTime.UtcNow,
            UsedPhysicalRoute = false,
            Error = error,
        };
    }
}
