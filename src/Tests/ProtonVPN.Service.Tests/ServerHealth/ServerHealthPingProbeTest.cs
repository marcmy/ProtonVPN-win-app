#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Service.ServerHealth;

namespace ProtonVPN.Service.Tests.ServerHealth;

[TestClass]
public class ServerHealthPingProbeTest
{
    [TestMethod]
    public async Task QuickCheck_ReturnsAfterFirstReplyWithActualOneSampleNotFour()
    {
        int sent = 0, delays = 0;
        ServerHealthPingProbe probe = new((_, _, _) => { sent++; return Task.FromResult<long?>(22); },
            (_, _) => { delays++; return Task.CompletedTask; });
        var result = await probe.MeasureAsync(IPAddress.Parse("192.0.2.1"), CancellationToken.None, true);
        Assert.AreEqual(1, sent);
        Assert.AreEqual(0, delays);
        Assert.AreEqual(1, result.TotalSamples);
        Assert.AreEqual(1, result.SuccessfulSamples);
        Assert.AreEqual(22d, result.AverageLatencyMilliseconds);
    }

    [TestMethod]
    public async Task QuickCheck_FirstPacketLost_AllowsOneFallbackAndThenReturns()
    {
        Queue<long?> replies = new([null, 35, 99, 99]);
        List<TimeSpan> delays = [];
        ServerHealthPingProbe probe = new((_, _, _) => Task.FromResult(replies.Dequeue()),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        var result = await probe.MeasureAsync(IPAddress.Parse("192.0.2.1"), CancellationToken.None, true);
        Assert.AreEqual(2, replies.Count);
        Assert.AreEqual(2, result.TotalSamples);
        Assert.AreEqual(1, result.SuccessfulSamples);
        Assert.AreEqual(35d, result.AverageLatencyMilliseconds);
        Assert.AreEqual(50d, result.PacketLossPercent);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromMilliseconds(25) }, delays);
    }

    [TestMethod]
    public async Task QuickCheck_NoReplies_StopsAfterTwoAttemptsWithoutInventingLatency()
    {
        int sent = 0;
        ServerHealthPingProbe probe = new((_, _, _) => { sent++; return Task.FromResult<long?>(null); }, (_, _) => Task.CompletedTask);
        var result = await probe.MeasureAsync(IPAddress.Parse("192.0.2.1"), CancellationToken.None, true);
        Assert.AreEqual(2, sent);
        Assert.AreEqual(2, result.TotalSamples);
        Assert.AreEqual(0, result.SuccessfulSamples);
        Assert.IsNull(result.AverageLatencyMilliseconds);
    }

    [TestMethod]
    public async Task NormalCheck_StillAveragesFourActualAttempts()
    {
        Queue<long?> replies = new([20, 30, null, 40]);
        ServerHealthPingProbe probe = new((_, _, _) => Task.FromResult(replies.Dequeue()), (_, _) => Task.CompletedTask);
        var result = await probe.MeasureAsync(IPAddress.Parse("192.0.2.1"), CancellationToken.None);
        Assert.AreEqual(0, replies.Count);
        Assert.AreEqual(4, result.TotalSamples);
        Assert.AreEqual(3, result.SuccessfulSamples);
        Assert.AreEqual(30d, result.AverageLatencyMilliseconds);
        Assert.AreEqual(25d, result.PacketLossPercent);
    }

    [TestMethod]
    public async Task CancelledQuickCheck_DoesNotSendAnotherPacket()
    {
        using CancellationTokenSource cancellation = new();
        int sent = 0;
        ServerHealthPingProbe probe = new((_, _, _) => { sent++; cancellation.Cancel(); return Task.FromResult<long?>(null); },
            (_, token) => Task.Delay(1, token));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => probe.MeasureAsync(IPAddress.Parse("192.0.2.1"), cancellation.Token, true));
        Assert.AreEqual(1, sent);
    }
}
