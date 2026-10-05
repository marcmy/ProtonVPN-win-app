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

namespace ProtonVPN.Client.Models.Connections;

// Discovery must not instantiate thousands of UI items for a collapsed country list.
internal sealed class ServerPingSource(Server server) : IServerHealthSource
{
    public string HealthServerId => server.Id;
    public double HealthServerLoad => server.Load / 100d;
    public string? HealthProbeAddress => server.Servers
        .Select(physicalServer => physicalServer.EntryIp)
        .Concat(server.Servers.SelectMany(physicalServer => physicalServer.RelayIpByProtocol.Values))
        .FirstOrDefault(ipAddress => !string.IsNullOrWhiteSpace(ipAddress));

    public async Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken)
    {
        string? address = HealthProbeAddress;
        if (string.IsNullOrWhiteSpace(address)) { return Unavailable("No probe address is available for this server."); }
        cancellationToken.ThrowIfCancellationRequested();
        var result = await App.GetService<IVpnServiceCaller>().ProbeServerHealthAsync(new ServerHealthProbeRequestIpcEntity
        { Address = address });
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            return Unavailable(string.IsNullOrWhiteSpace(result.Error)
                ? "The VPN service did not complete the direct health check." : result.Error);
        }
        ServerHealthProbeResultIpcEntity response = result.Value;
        return new(response.AverageLatencyMilliseconds, response.SuccessfulSamples, response.TotalSamples,
            new DateTimeOffset(DateTime.SpecifyKind(response.CheckedAtUtc, DateTimeKind.Utc)),
            response.UsedPhysicalRoute, response.Error, HealthServerLoad);
    }

    private ServerHealthProbeMeasurement Unavailable(string error) =>
        new(null, 0, 4, DateTimeOffset.UtcNow, false, error, HealthServerLoad);
}
