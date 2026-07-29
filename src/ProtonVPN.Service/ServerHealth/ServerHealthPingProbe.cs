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
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Service.ServerHealth;

internal sealed class ServerHealthPingProbe : IServerHealthPingProbe
{
    internal const int ProbeSampleCount = 4;
    private const int PROBE_TIMEOUT_IN_MILLISECONDS = 500;
    private static readonly TimeSpan _delayBetweenSamples = TimeSpan.FromMilliseconds(100);

    public async Task<ServerHealthProbeResultIpcEntity> MeasureAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken)
    {
        List<long> successfulRoundTrips = [];

        using Ping ping = new();
        for (int sampleIndex = 0; sampleIndex < ProbeSampleCount; sampleIndex++)
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

            if (sampleIndex < ProbeSampleCount - 1)
            {
                await Task.Delay(_delayBetweenSamples, cancellationToken);
            }
        }

        return new()
        {
            AverageLatencyMilliseconds = successfulRoundTrips.Count > 0
                ? successfulRoundTrips.Average()
                : null,
            PacketLossPercent =
                (ProbeSampleCount - successfulRoundTrips.Count) * 100d / ProbeSampleCount,
            SuccessfulSamples = successfulRoundTrips.Count,
            TotalSamples = ProbeSampleCount,
            CheckedAtUtc = DateTime.UtcNow,
            UsedPhysicalRoute = true,
            Error = successfulRoundTrips.Count == 0
                ? "No ICMP replies were received. The server may block ping; this does not necessarily mean it is offline."
                : null,
        };
    }
}
