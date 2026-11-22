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

using ProtonVPN.Client.Common.UI.ServerHealth;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using System.Net;
using System.Net.Sockets;

namespace ProtonVPN.Client.Models.Connections;

// Discovery must not instantiate thousands of UI items for a collapsed country list.
internal sealed class ServerPingSource(Server server) : IServerHealthSource
{
    public string HealthServerId => server.Id;
    public double HealthServerLoad => server.Load / 100d;
    public string? HealthProbeAddress { get; } = server.Servers
        .Select(physicalServer => physicalServer.EntryIp)
        .Concat(server.Servers.SelectMany(physicalServer => physicalServer.RelayIpByProtocol.Values))
        .Select(GetIpv4Address)
        .FirstOrDefault(ipAddress => ipAddress is not null);

    public Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken) =>
        ProbeHealthAsync(cancellationToken, quickFirstResponse: false);

    public Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken, bool quickFirstResponse) =>
        ProbeAddressAsync(App.GetService<IVpnServiceCaller>(), HealthProbeAddress, HealthServerLoad, quickFirstResponse, cancellationToken);

    internal static string? GetIpv4Address(string? address) =>
        IPAddress.TryParse(address, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? ip.ToString()
            : null;

    internal static async Task<ServerHealthProbeMeasurement> ProbeAddressAsync(
        IVpnServiceCaller service, string? address, double load, bool quickFirstResponse, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (address is null)
        {
            return Unavailable("No IPv4 probe address is available for this server.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await service.ProbeServerHealthAsync(new()
            {
                Address = address,
                QuickFirstResponse = quickFirstResponse,
            }, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                return Unavailable("The VPN service could not complete the direct ICMP check.");
            }

            ServerHealthProbeResultIpcEntity response = result.Value;
            ServerHealthObservation[]? observations = response.Samples?.Select(sample => new ServerHealthObservation(
                sample.Outcome switch
                {
                    ServerHealthProbeOutcome.Reply => ServerHealthObservationOutcome.Reply,
                    ServerHealthProbeOutcome.TimedOut => ServerHealthObservationOutcome.TimedOut,
                    ServerHealthProbeOutcome.Unreachable => ServerHealthObservationOutcome.Unreachable,
                    _ => ServerHealthObservationOutcome.Unavailable,
                },
                sample.RoundTripTimeMilliseconds,
                new DateTimeOffset(DateTime.SpecifyKind(sample.SentAtUtc, DateTimeKind.Utc)))).ToArray();
            return new(response.AverageLatencyMilliseconds, response.SuccessfulSamples, response.TotalSamples,
                new DateTimeOffset(DateTime.SpecifyKind(response.CheckedAtUtc, DateTimeKind.Utc)),
                response.UsedPhysicalRoute, response.Error, load, IsDiscovery: quickFirstResponse,
                RouteContext: response.RouteContext, Observations: observations);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            return Unavailable("The VPN service health request reached its deadline.");
        }

        ServerHealthProbeMeasurement Unavailable(string error) =>
            new(null, 0, 0, DateTimeOffset.UtcNow, false, error, load, IsDiscovery: quickFirstResponse);
    }
}
