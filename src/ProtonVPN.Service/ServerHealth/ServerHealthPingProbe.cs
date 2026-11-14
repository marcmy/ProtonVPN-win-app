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
    private readonly Func<IPAddress, int, CancellationToken, Task<long?>> _send;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ServerHealthPingProbe() : this(SendAsync, Task.Delay) { }

    internal ServerHealthPingProbe(Func<IPAddress, int, CancellationToken, Task<long?>> send,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _send = send;
        _delay = delay;
    }

    private static async Task<long?> SendAsync(IPAddress address, int timeout, CancellationToken cancellationToken)
    {
        using Ping ping = new();
        PingReply reply = await ping.SendPingAsync(address, timeout);
        cancellationToken.ThrowIfCancellationRequested();
        return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
    }

    public async Task<ServerHealthProbeResultIpcEntity> MeasureAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken,
        bool quickFirstResponse = false)
    {
        List<long> successfulRoundTrips = [];
        int sampleLimit = quickFirstResponse ? 2 : ProbeSampleCount;
        int attempted = 0;
        for (int sampleIndex = 0; sampleIndex < sampleLimit; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;

            try
            {
                long? latency = await _send(ipAddress, PROBE_TIMEOUT_IN_MILLISECONDS, cancellationToken);
                if (latency is not null)
                {
                    successfulRoundTrips.Add(latency.Value);
                }
            }
            catch (Exception exception) when (exception is PingException or InvalidOperationException)
            {
            }

            if (quickFirstResponse && successfulRoundTrips.Count > 0) { break; }
            if (sampleIndex < sampleLimit - 1)
            {
                await _delay(quickFirstResponse ? TimeSpan.FromMilliseconds(25) : _delayBetweenSamples, cancellationToken);
            }
        }

        return new()
        {
            AverageLatencyMilliseconds = successfulRoundTrips.Count > 0
                ? successfulRoundTrips.Average()
                : null,
            PacketLossPercent =
                (attempted - successfulRoundTrips.Count) * 100d / attempted,
            SuccessfulSamples = successfulRoundTrips.Count,
            TotalSamples = attempted,
            CheckedAtUtc = DateTime.UtcNow,
            UsedPhysicalRoute = true,
            Error = successfulRoundTrips.Count == 0
                ? "No ICMP replies were received. The server may block ping; this does not necessarily mean it is offline."
                : null,
        };
    }
}
