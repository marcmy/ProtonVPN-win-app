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
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Service.ServerHealth;

internal sealed class ServerHealthPingProbe : IServerHealthPingProbe
{
    internal const int ProbeSampleCount = 4;
    private static readonly TimeSpan _probeTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _fixedSendInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _quickRetryDelay = TimeSpan.FromMilliseconds(25);

    private readonly Func<IPAddress, TimeSpan, CancellationToken, Task<(IPStatus Status, long RoundTripTime)>> _send;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _clock;

    public ServerHealthPingProbe() : this(SendAsync, Task.Delay, TimeProvider.System)
    {
    }

    internal ServerHealthPingProbe(
        Func<IPAddress, TimeSpan, CancellationToken, Task<(IPStatus Status, long RoundTripTime)>> send,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider? clock = null)
    {
        _send = send;
        _delay = delay;
        _clock = clock ?? TimeProvider.System;
    }

    private static async Task<(IPStatus Status, long RoundTripTime)> SendAsync(
        IPAddress address,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using Ping ping = new();
        PingReply reply = await ping.SendPingAsync(address, timeout, cancellationToken: cancellationToken);
        IPStatus status = reply.Status == IPStatus.Success && !reply.Address.Equals(address)
            ? IPStatus.Unknown
            : reply.Status;
        return (status, reply.RoundtripTime);
    }

    public async Task<ServerHealthProbeResultIpcEntity> MeasureAsync(
        IPAddress ipAddress,
        CancellationToken cancellationToken,
        bool quickFirstResponse = false)
    {
        ServerHealthProbeSampleIpcEntity[] samples = quickFirstResponse
            ? await SendQuickAsync(ipAddress, cancellationToken)
            : await SendFixedAsync(ipAddress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        int replies = 0;
        int timeouts = 0;
        double totalRoundTripTime = 0;
        foreach (ServerHealthProbeSampleIpcEntity sample in samples)
        {
            if (sample.Outcome == ServerHealthProbeOutcome.Reply)
            {
                replies++;
                totalRoundTripTime += sample.RoundTripTimeMilliseconds.GetValueOrDefault();
            }
            else if (sample.Outcome == ServerHealthProbeOutcome.TimedOut)
            {
                timeouts++;
            }
        }

        int classified = replies + timeouts;
        return new()
        {
            AverageLatencyMilliseconds = replies > 0 ? totalRoundTripTime / replies : null,
            PacketLossPercent = classified > 0 ? timeouts * 100d / classified : 0d,
            SuccessfulSamples = replies,
            TotalSamples = classified,
            CheckedAtUtc = _clock.GetUtcNow().UtcDateTime,
            Samples = samples,
            Error = classified != samples.Length
                ? "One or more ICMP attempts could not be measured."
                : replies == 0 ? "No ICMP replies were received." : null,
        };
    }

    private async Task<ServerHealthProbeSampleIpcEntity[]> SendQuickAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        ServerHealthProbeSampleIpcEntity first = await SendSampleAsync(address, cancellationToken);
        if (first.Outcome != ServerHealthProbeOutcome.TimedOut)
        {
            return [first];
        }

        await _delay(_quickRetryDelay, cancellationToken);
        return [first, await SendSampleAsync(address, cancellationToken)];
    }

    private async Task<ServerHealthProbeSampleIpcEntity[]> SendFixedAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        long startedAt = _clock.GetTimestamp();
        Task<ServerHealthProbeSampleIpcEntity>[] sends = new Task<ServerHealthProbeSampleIpcEntity>[ProbeSampleCount];
        for (int sampleIndex = 0; sampleIndex < sends.Length; sampleIndex++)
        {
            sends[sampleIndex] = SendAtOffsetAsync(address, startedAt, _fixedSendInterval * sampleIndex, cancellationToken);
        }

        // Await all native operations before the caller releases its route and permit.
        return await Task.WhenAll(sends);
    }

    private async Task<ServerHealthProbeSampleIpcEntity> SendAtOffsetAsync(
        IPAddress address,
        long startedAt,
        TimeSpan offset,
        CancellationToken cancellationToken)
    {
        TimeSpan wait = offset - _clock.GetElapsedTime(startedAt);
        if (wait > TimeSpan.Zero)
        {
            await _delay(wait, cancellationToken);
        }

        return await SendSampleAsync(address, cancellationToken);
    }

    private async Task<ServerHealthProbeSampleIpcEntity> SendSampleAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTime sentAt = _clock.GetUtcNow().UtcDateTime;
        try
        {
            (IPStatus status, long roundTripTime) = await _send(address, _probeTimeout, cancellationToken);
            ServerHealthProbeOutcome outcome = status switch
            {
                IPStatus.Success => ServerHealthProbeOutcome.Reply,
                IPStatus.TimedOut => ServerHealthProbeOutcome.TimedOut,
                IPStatus.DestinationUnreachable or IPStatus.DestinationNetworkUnreachable or
                IPStatus.DestinationHostUnreachable or IPStatus.DestinationProtocolUnreachable or
                IPStatus.DestinationPortUnreachable => ServerHealthProbeOutcome.Unreachable,
                _ => ServerHealthProbeOutcome.Unavailable,
            };
            return new()
            {
                Outcome = outcome,
                SentAtUtc = sentAt,
                RoundTripTimeMilliseconds = outcome == ServerHealthProbeOutcome.Reply ? roundTripTime : null,
            };
        }
        catch (Exception exception) when (exception is PingException or SocketException or InvalidOperationException or
                                         ArgumentException or NotSupportedException)
        {
            return new() { Outcome = ServerHealthProbeOutcome.Unavailable, SentAtUtc = sentAt };
        }
    }
}
