#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.Service.ServerHealth;

namespace ProtonVPN.Service.Tests.ServerHealth;

[TestClass]
public class ServerHealthPingProbeTest
{
    private static readonly IPAddress _address = IPAddress.Parse("192.0.2.1");

    [TestMethod]
    public async Task QuickCheck_ReturnsAfterFirstReplyWithActualOneSampleNotFour()
    {
        int sent = 0, delays = 0;
        ServerHealthPingProbe probe = new((_, _, _) => { sent++; return Task.FromResult((IPStatus.Success, 22L)); },
            (_, _) => { delays++; return Task.CompletedTask; });
        ServerHealthProbeResultIpcEntity result = await probe.MeasureAsync(_address, CancellationToken.None, true);
        Assert.AreEqual(1, sent);
        Assert.AreEqual(0, delays);
        Assert.AreEqual(1, result.TotalSamples);
        Assert.AreEqual(1, result.SuccessfulSamples);
        Assert.AreEqual(22d, result.AverageLatencyMilliseconds);
        Assert.AreEqual(ServerHealthProbeOutcome.Reply, result.Samples!.Single().Outcome);
    }

    [TestMethod]
    public async Task QuickCheck_FirstPacketLost_AllowsOneFallbackAndThenReturns()
    {
        Queue<(IPStatus, long)> replies = new([(IPStatus.TimedOut, 0), (IPStatus.Success, 35), (IPStatus.Success, 99)]);
        List<TimeSpan> delays = [];
        ServerHealthPingProbe probe = new((_, _, _) => Task.FromResult(replies.Dequeue()),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        ServerHealthProbeResultIpcEntity result = await probe.MeasureAsync(_address, CancellationToken.None, true);
        Assert.AreEqual(1, replies.Count);
        Assert.AreEqual(2, result.TotalSamples);
        Assert.AreEqual(1, result.SuccessfulSamples);
        Assert.AreEqual(35d, result.AverageLatencyMilliseconds);
        Assert.AreEqual(50d, result.PacketLossPercent);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(25) }, delays);
        CollectionAssert.AreEqual(new[] { ServerHealthProbeOutcome.TimedOut, ServerHealthProbeOutcome.Reply },
            result.Samples!.Select(sample => sample.Outcome).ToArray());
    }

    [TestMethod]
    public async Task QuickCheck_NoReplies_StopsAfterTwoAttemptsWithoutInventingLatency()
    {
        int sent = 0;
        ServerHealthPingProbe probe = new((_, _, _) => { sent++; return Task.FromResult((IPStatus.TimedOut, 0L)); },
            (_, _) => Task.CompletedTask);
        ServerHealthProbeResultIpcEntity result = await probe.MeasureAsync(_address, CancellationToken.None, true);
        Assert.AreEqual(2, sent);
        Assert.AreEqual(2, result.TotalSamples);
        Assert.AreEqual(0, result.SuccessfulSamples);
        Assert.IsNull(result.AverageLatencyMilliseconds);
        Assert.IsTrue(result.Samples!.All(sample => sample.Outcome == ServerHealthProbeOutcome.TimedOut));
    }

    [TestMethod]
    public async Task NormalCheck_StillAveragesFourActualAttempts()
    {
        Queue<(IPStatus, long)> replies = new([(IPStatus.Success, 20), (IPStatus.Success, 30),
            (IPStatus.TimedOut, 0), (IPStatus.Success, 40)]);
        ServerHealthPingProbe probe = new((_, _, _) => Task.FromResult(replies.Dequeue()), (_, _) => Task.CompletedTask);
        ServerHealthProbeResultIpcEntity result = await probe.MeasureAsync(_address, CancellationToken.None);
        Assert.AreEqual(0, replies.Count);
        Assert.AreEqual(4, result.TotalSamples);
        Assert.AreEqual(3, result.SuccessfulSamples);
        Assert.AreEqual(30d, result.AverageLatencyMilliseconds);
        Assert.AreEqual(25d, result.PacketLossPercent);
    }

    [TestMethod]
    public async Task NormalCheck_StartsFourEchoesWhileTheFirstDeadlineIsPending()
    {
        TaskCompletionSource<(IPStatus, long)> firstReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<TimeSpan> deadlines = [];
        List<TimeSpan> offsets = [];
        TestClock clock = new();
        ServerHealthPingProbe probe = new((_, timeout, _) =>
        {
            deadlines.Add(timeout);
            return deadlines.Count == 1 ? firstReply.Task : Task.FromResult((IPStatus.Success, 35L));
        }, (offset, _) =>
        {
            offsets.Add(offset);
            clock.Advance(offset);
            return Task.CompletedTask;
        }, clock);

        Task<ServerHealthProbeResultIpcEntity> pending = probe.MeasureAsync(_address, CancellationToken.None);
        try
        {
            CollectionAssert.AreEqual(Enumerable.Repeat(TimeSpan.FromMilliseconds(500), 4).ToArray(), deadlines);
            CollectionAssert.AreEqual(Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 3).ToArray(), offsets);
            Assert.IsFalse(pending.IsCompleted);
        }
        finally
        {
            firstReply.SetResult((IPStatus.TimedOut, 0));
        }

        ServerHealthProbeResultIpcEntity result = await pending;
        DateTime firstSend = result.Samples![0].SentAtUtc;
        CollectionAssert.AreEqual(new double[] { 0, 100, 200, 300 }, result.Samples
            .Select(sample => (sample.SentAtUtc - firstSend).TotalMilliseconds).ToArray());
        Assert.AreEqual(ServerHealthProbeOutcome.TimedOut, result.Samples[0].Outcome);
    }

    [TestMethod]
    public async Task QuickCheck_WhenTheSendFailsLocally_SkipsTimeoutFallback()
    {
        int started = 0;
        ServerHealthPingProbe probe = new((_, _, _) =>
        {
            started++;
            throw new PingException("simulated local send failure");
        }, (_, _) => Task.CompletedTask);

        ServerHealthProbeResultIpcEntity result = await probe.MeasureAsync(_address, CancellationToken.None, true);

        Assert.AreEqual(1, started);
        Assert.AreEqual(0, result.TotalSamples);
        Assert.AreEqual(0d, result.PacketLossPercent);
        Assert.AreEqual(ServerHealthProbeOutcome.Unavailable, result.Samples!.Single().Outcome);
    }

    [TestMethod]
    public async Task NormalCheck_SeparatesTimeoutsFromUnreachableAndLocalSendFailure()
    {
        int started = 0;
        ServerHealthPingProbe probe = new((_, _, _) => ++started switch
        {
            1 => Task.FromResult((IPStatus.Success, 0L)),
            2 => Task.FromResult((IPStatus.TimedOut, 0L)),
            3 => Task.FromResult((IPStatus.DestinationNetworkUnreachable, 0L)),
            _ => throw new InvalidOperationException("simulated local send failure"),
        }, (_, _) => Task.CompletedTask);

        ServerHealthProbeResultIpcEntity result = await probe.MeasureAsync(_address, CancellationToken.None);
        ServerHealthProbeSampleIpcEntity[] samples = result.Samples!;

        CollectionAssert.AreEqual(new[] { ServerHealthProbeOutcome.Reply, ServerHealthProbeOutcome.TimedOut,
            ServerHealthProbeOutcome.Unreachable, ServerHealthProbeOutcome.Unavailable },
            samples.Select(sample => sample.Outcome).ToArray());
        Assert.AreEqual(2, result.TotalSamples);
        Assert.AreEqual(1, result.SuccessfulSamples);
        Assert.AreEqual(0d, samples[0].RoundTripTimeMilliseconds);
        Assert.IsTrue(samples.Skip(1).All(sample => sample.RoundTripTimeMilliseconds is null));
    }

    [TestMethod]
    public async Task CancelledFixedCheck_AwaitsStartedEchoesAndSkipsLaterSends()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<(IPStatus, long)> firstEcho = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sent = 0;
        ServerHealthPingProbe probe = new((_, _, _) => { sent++; return firstEcho.Task; },
            (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));

        Task pending = probe.MeasureAsync(_address, cancellation.Token);
        cancellation.Cancel();
        Assert.IsFalse(pending.IsCompleted);
        firstEcho.SetResult((IPStatus.Success, 22));
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreEqual(1, sent);
    }

    [TestMethod]
    public async Task CancelledQuickCheck_DoesNotSendAnotherPacket()
    {
        using CancellationTokenSource cancellation = new();
        int sent = 0;
        ServerHealthPingProbe probe = new((_, _, _) => { sent++; cancellation.Cancel(); return Task.FromResult((IPStatus.TimedOut, 0L)); },
            (_, token) => Task.Delay(1, token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.MeasureAsync(_address, cancellation.Token, true));
        Assert.AreEqual(1, sent);
    }

    private sealed class TestClock : TimeProvider
    {
        private TimeSpan _elapsed;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _elapsed.Ticks;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;

        public void Advance(TimeSpan duration) => _elapsed += duration;
    }
}
