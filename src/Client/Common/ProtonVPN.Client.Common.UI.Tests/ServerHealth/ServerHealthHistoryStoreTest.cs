using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthHistoryStoreTest
{
    private readonly FakeServerHealthClock _clock = new();

    [TestMethod]
    public async Task LocalFailure_PublishesImmediatelyWithoutSyntheticLoss()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(new ServerHealthProbeMeasurement(null, 0, 0, _clock.UtcNow, false, "Route unavailable", 0));
        using ServerHealthHistoryStore store = new(_clock);

        ServerHealthSnapshot snapshot = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(1, source.ProbeCount);
        Assert.AreEqual(0, _clock.Delays.Count);
        Assert.AreEqual(0, snapshot.Aggregate!.TotalSamples);
        Assert.AreEqual("Check unavailable", ServerHealthPresentation.FromSnapshot(snapshot).StatusText);
        Assert.AreEqual("—", ServerHealthPresentation.FromSnapshot(snapshot).PacketLossText);
    }

    [TestMethod]
    public async Task AllTimeouts_ReportsNoReplyWithoutClaimingAnOutage()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(ServerHealthSamples.FixedCheck(_clock.UtcNow, null, replies: 0));
        using ServerHealthHistoryStore store = new(_clock);

        ServerHealthSnapshot snapshot = await store.ProbeAsync(source, CancellationToken.None);
        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);

        Assert.AreEqual(4, snapshot.Aggregate!.TotalSamples);
        Assert.IsFalse(snapshot.Aggregate.IsLossEstablished);
        Assert.AreEqual("No ICMP reply", presentation.StatusText);
        Assert.AreEqual("—", presentation.PacketLossText);
        Assert.AreEqual(0, presentation.ActiveBarCount);
    }

    [TestMethod]
    public async Task SameAddressRequests_ShareOneProbeAndRetainEachLogicalKey()
    {
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource first = Source();
        QueueServerHealthSource second = Source("server-2");
        first.Enqueue(_ => completion.Task);
        using ServerHealthHistoryStore store = new(_clock);

        Task<ServerHealthSnapshot> one = store.ProbeAsync(first, CancellationToken.None);
        Task<ServerHealthSnapshot> two = store.ProbeAsync(second, CancellationToken.None);
        completion.SetResult(ServerHealthSamples.FixedCheck(_clock.UtcNow));
        ServerHealthSnapshot[] results = await Task.WhenAll(one, two);

        Assert.AreEqual(1, first.ProbeCount);
        Assert.AreEqual(0, second.ProbeCount);
        Assert.AreNotEqual(results[0].Key, results[1].Key);
        Assert.AreEqual(results[0].Aggregate, results[1].Aggregate);
        Assert.AreEqual(1, store.GetSnapshot(results[1].Key).Measurements.Count);
    }

    [TestMethod]
    public async Task SameConsumerAndMode_ReusesRecentFixedMeasurement()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(_ => Task.FromResult(ServerHealthSamples.FixedCheck(_clock.UtcNow, 40)));
        source.Enqueue(_ => Task.FromResult(ServerHealthSamples.FixedCheck(_clock.UtcNow, 45)));
        using ServerHealthHistoryStore store = new(_clock, minimumProbeInterval: TimeSpan.FromSeconds(30));

        ServerHealthSnapshot first = await store.ProbeAsync(source, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(29));
        ServerHealthSnapshot reused = await store.ProbeAsync(source, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(2));
        ServerHealthSnapshot next = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(2, source.ProbeCount);
        Assert.AreEqual(first.Measurements.Count, reused.Measurements.Count);
        Assert.AreEqual(2, next.Measurements.Count);
    }

    [TestMethod]
    public async Task Discovery_DoesNotSuppressImmediateFixedRefinement()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(new ServerHealthProbeMeasurement(18, 1, 1, _clock.UtcNow, true, null, 0));
        source.Enqueue(ServerHealthSamples.FixedCheck(_clock.UtcNow, 30));
        using ServerHealthHistoryStore store = new(_clock);

        ServerHealthSnapshot quick = await store.ProbeAsync(source, CancellationToken.None, quickFirstResponse: true);
        ServerHealthSnapshot refined = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(18d, quick.CurrentLatencyMilliseconds);
        Assert.AreEqual(0, quick.Aggregate!.TotalSamples);
        Assert.AreEqual(30d, refined.CurrentLatencyMilliseconds);
        Assert.AreEqual(4, refined.Aggregate!.TotalSamples);
        Assert.AreEqual(1, refined.Aggregate.MeasurementCount);
    }

    [TestMethod]
    public async Task CoveredEndpointCancellation_PreservesTheRemainingSubscriber()
    {
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource source = Source();
        source.Enqueue(token => completion.Task.WaitAsync(token));
        using ServerHealthHistoryStore store = new(_clock);
        using CancellationTokenSource cancellation = new();
        Task<ServerHealthSnapshot> cancelled = store.ProbeAsync(source, cancellation.Token);
        Task<ServerHealthSnapshot> remaining = store.ProbeAsync(source, CancellationToken.None);

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled);
        completion.SetResult(ServerHealthSamples.FixedCheck(_clock.UtcNow));

        Assert.AreEqual(1, (await remaining).Measurements.Count);
        Assert.AreEqual(1, source.ProbeCount);
    }

    [TestMethod]
    public async Task LastSubscriberCancellation_CancelsProbeAndDoesNotRecordLoss()
    {
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource source = Source();
        source.Enqueue(async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ServerHealthSamples.FixedCheck(_clock.UtcNow);
        });
        using ServerHealthHistoryStore store = new(_clock);
        using CancellationTokenSource cancellation = new();
        Task<ServerHealthSnapshot> pending = store.ProbeAsync(source, cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNull(store.GetSnapshot(ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)).Aggregate);
    }

    [TestMethod]
    public async Task ChangedServiceRoute_ClearsPreviousEvidence()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(ServerHealthSamples.FixedCheck(_clock.UtcNow, 110, replies: 3, route: "route-one"));
        using ServerHealthHistoryStore store = new(_clock);
        await store.ProbeAsync(source, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));
        source.Enqueue(ServerHealthSamples.FixedCheck(_clock.UtcNow, 25, route: "route-two"));
        ServerHealthSnapshot next = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(25d, next.CurrentLatencyMilliseconds);
        Assert.AreEqual(4, next.Aggregate!.TotalSamples);
        Assert.AreEqual(0, next.Aggregate.MissedSamples);
        Assert.AreEqual(1, next.Measurements.Count);
    }

    [TestMethod]
    public async Task DelayedDiscoveryResult_CannotOverwriteNewerRouteOrLoss()
    {
        QueueServerHealthSource source = Source();
        TaskCompletionSource<ServerHealthProbeMeasurement> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Enqueue(_ => reply.Task);
        using ServerHealthHistoryStore store = new(_clock);
        Task<ServerHealthSnapshot> discovery = store.ProbeAsync(source, CancellationToken.None, quickFirstResponse: true);
        DateTimeOffset earlier = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromSeconds(1));
        source.Enqueue(ServerHealthSamples.FixedCheck(_clock.UtcNow, 25, replies: 3, route: "route-two"));
        await store.ProbeAsync(source, CancellationToken.None);

        reply.SetResult(ServerHealthSamples.FixedCheck(earlier, 90, route: "route-one"));
        ServerHealthSnapshot result = await discovery;

        Assert.AreEqual("route-two", result.LatestMeasurement!.RouteContext);
        Assert.AreEqual(25d, result.CurrentLatencyMilliseconds);
        Assert.AreEqual(4, result.Aggregate!.TotalSamples);
        Assert.AreEqual(1, result.Aggregate.MissedSamples);
    }

    [TestMethod]
    public async Task GraphHistoryExpiresAfterTenMinutesAndKeepsSavedLatency()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(ServerHealthSamples.FixedCheck(_clock.UtcNow));
        using ServerHealthHistoryStore store = new(_clock);

        ServerHealthSnapshot snapshot = await store.ProbeAsync(source, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromMilliseconds(1));
        ServerHealthSnapshot saved = store.GetSnapshot(snapshot.Key);

        Assert.AreEqual(0, saved.Measurements.Count);
        Assert.IsTrue(saved.IsCached);
        Assert.AreEqual("42 ms", ServerHealthPresentation.FromSnapshot(saved).LatencyText);
        Assert.AreEqual("—", ServerHealthPresentation.FromSnapshot(saved).PacketLossText);
    }

    private static QueueServerHealthSource Source(string id = "server-1") => new()
    {
        HealthServerId = id,
        HealthProbeAddress = "10.0.0.1",
    };
}
