using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthHistoryStoreTest
{
    [TestMethod]
    public async Task FirstFailure_RetriesAfterFiveSecondsAndRecordsSuccessfulRetry()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Success(clock, 40, 4));
        using ServerHealthHistoryStore store = new(clock);

        ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(5) }, clock.Delays);
        Assert.AreEqual(2, source.ProbeCount);
        Assert.AreEqual(1, result.Measurements.Count);
        Assert.IsTrue(result.Measurements[0].WasRetried);
        Assert.IsFalse(result.Measurements[0].IsConfirmedOutage);
    }

    [TestMethod]
    public async Task TwoFailures_RecordOneConfirmedOutage()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Failure(clock, "second"));
        using ServerHealthHistoryStore store = new(clock);

        ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(1, result.Measurements.Count);
        Assert.AreEqual(0, result.Measurements[0].SuccessfulSamples);
        Assert.AreEqual(4, result.Measurements[0].TotalSamples);
        Assert.IsTrue(result.Measurements[0].IsConfirmedOutage);
        Assert.AreEqual(100, result.Aggregate!.PacketLossPercent);
    }

    [TestMethod]
    public async Task PartialLoss_DoesNotRetry()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Success(clock, 40, 2));
        using ServerHealthHistoryStore store = new(clock);

        ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(1, source.ProbeCount);
        Assert.AreEqual(0, clock.Delays.Count);
        Assert.AreEqual(50, result.Aggregate!.PacketLossPercent);
    }

    [TestMethod]
    public async Task ConcurrentSameKeyRequests_UseOneProbe()
    {
        FakeServerHealthClock clock = new();
        TaskCompletionSource<ServerHealthProbeMeasurement> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource source = Source();
        source.Enqueue(_ => completion.Task);
        using ServerHealthHistoryStore store = new(clock);

        Task<ServerHealthSnapshot> first = store.ProbeAsync(source, CancellationToken.None);
        Task<ServerHealthSnapshot> second = store.ProbeAsync(source, CancellationToken.None);
        completion.SetResult(Success(clock, 40, 4));
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, source.ProbeCount);
        Assert.AreSame(first.Result, second.Result);
    }

    [TestMethod]
    public async Task RequestDuringRetryDelay_JoinsLogicalBatch()
    {
        FakeServerHealthClock clock = new() { BlockDelays = true };
        QueueServerHealthSource source = Source();
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Failure(clock, "second"));
        using ServerHealthHistoryStore store = new(clock);

        Task<ServerHealthSnapshot> first = store.ProbeAsync(source, CancellationToken.None);
        Assert.IsTrue(SpinWait.SpinUntil(() => clock.Delays.Count == 1, TimeSpan.FromSeconds(1)));
        Task<ServerHealthSnapshot> second = store.ProbeAsync(source, CancellationToken.None);
        clock.CompleteDelay();
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, source.ProbeCount);
        Assert.AreSame(first.Result, second.Result);
        Assert.AreEqual(1, first.Result.Measurements.Count);
        Assert.IsTrue(first.Result.Measurements[0].IsConfirmedOutage);
    }

    [TestMethod]
    public async Task FirstFailure_PreservesPreviousAggregateWhileRechecking()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Success(clock, 40, 4));
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Failure(clock, "second"));
        using ServerHealthHistoryStore store = new(clock);
        ServerHealthSnapshot previous = await store.ProbeAsync(source, CancellationToken.None);
        List<ServerHealthSnapshot> observed = [];
        store.SnapshotChanged += (_, args) => observed.Add(args.Snapshot);

        await store.ProbeAsync(source, CancellationToken.None);

        ServerHealthSnapshot rechecking = observed.Single(snapshot => snapshot.IsRechecking);
        Assert.AreEqual(previous.Aggregate, rechecking.Aggregate);
        Assert.AreEqual(1, rechecking.Measurements.Count);
        Assert.AreEqual("first", rechecking.PendingError);
    }

    [TestMethod]
    public async Task HistorySurvivesViewLifetimeAndExpiresAfterTenMinutes()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Success(clock, 40, 4));
        using ServerHealthHistoryStore store = new(clock);
        ServerHealthSnapshot recorded = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(1, store.GetSnapshot(recorded.Key).Measurements.Count);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.AreEqual(1, store.GetSnapshot(recorded.Key).Measurements.Count);
        clock.Advance(TimeSpan.FromMinutes(1).Add(TimeSpan.FromMilliseconds(1)));
        Assert.AreEqual(0, store.GetSnapshot(recorded.Key).Measurements.Count);
    }

    [TestMethod]
    public async Task FourRecordedBatches_RetainGraphHistoryButScoreNewestThree()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Failure(clock, "first"));
        source.Enqueue(Failure(clock, "retry"));
        source.Enqueue(Success(clock, 30, 4));
        source.Enqueue(Success(clock, 31, 4));
        source.Enqueue(Success(clock, 32, 4));
        using ServerHealthHistoryStore store = new(clock);

        await store.ProbeAsync(source, CancellationToken.None);
        await store.ProbeAsync(source, CancellationToken.None);
        await store.ProbeAsync(source, CancellationToken.None);
        ServerHealthSnapshot result = await store.ProbeAsync(source, CancellationToken.None);

        Assert.AreEqual(4, result.Measurements.Count);
        Assert.AreEqual(3, result.Aggregate!.MeasurementCount);
        Assert.AreEqual(0, result.Aggregate.PacketLossPercent);
    }

    [TestMethod]
    public async Task ConsumerCancellation_DoesNotCreateLoss()
    {
        FakeServerHealthClock clock = new();
        TaskCompletionSource<ServerHealthProbeMeasurement> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource source = Source();
        source.Enqueue(_ => completion.Task);
        using ServerHealthHistoryStore store = new(clock);
        using CancellationTokenSource cancellation = new();
        Task<ServerHealthSnapshot> pending = store.ProbeAsync(source, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => pending);
        TaskCompletionSource<ServerHealthSnapshot> recorded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.Aggregate is not null)
            {
                recorded.TrySetResult(args.Snapshot);
            }
        };
        completion.SetResult(Success(clock, 40, 4));
        ServerHealthSnapshot result = await recorded.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual(1, source.ProbeCount);
        Assert.AreEqual(1, result.Measurements.Count);
        Assert.IsFalse(result.Measurements[0].IsConfirmedOutage);
    }

    [TestMethod]
    public async Task SecondConsumerWithSameKey_SeesExistingHistory()
    {
        FakeServerHealthClock clock = new();
        QueueServerHealthSource source = Source();
        source.Enqueue(Success(clock, 40, 4));
        using ServerHealthHistoryStore store = new(clock);
        ServerHealthSnapshot first = await store.ProbeAsync(source, CancellationToken.None);
        ServerHealthSnapshot second = store.GetSnapshot(ServerHealthHistoryKey.Create(
            source.HealthServerId,
            source.HealthProbeAddress!));

        CollectionAssert.AreEqual(first.Measurements.ToArray(), second.Measurements.ToArray());
        Assert.AreEqual(first.Aggregate, second.Aggregate);
    }

    private static QueueServerHealthSource Source() => new()
    {
        HealthServerId = "server-1",
        HealthProbeAddress = "10.0.0.1",
        HealthServerLoad = 0.25,
    };

    private static ServerHealthProbeMeasurement Success(
        FakeServerHealthClock clock,
        double latency,
        int successful) =>
        new(latency, successful, 4, clock.UtcNow, true, null, 0.25);

    private static ServerHealthProbeMeasurement Failure(
        FakeServerHealthClock clock,
        string error) =>
        new(null, 0, 4, clock.UtcNow, false, error, 0.25);
}
