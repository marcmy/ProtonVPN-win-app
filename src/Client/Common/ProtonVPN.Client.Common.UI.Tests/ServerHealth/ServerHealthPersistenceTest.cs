using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthPersistenceTest
{
    private string _directory = null!;
    private string CachePath => Path.Combine(_directory, "ping-cache.json");
    private readonly FakeServerHealthClock _clock = new();

    [TestInitialize]
    public void Initialize() => _directory = Directory.CreateTempSubdirectory("proton-ping-tests-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    [TestMethod]
    public async Task Restart_RestoresSavedPingAndTimestampWithoutInventingGraphHistory()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(Success());
        using (ServerHealthHistoryStore original = new(_clock, cachePath: CachePath))
        {
            await original.ProbeAsync(source, CancellationToken.None);
        }
        _clock.Advance(TimeSpan.FromDays(1));
        using ServerHealthHistoryStore restored = new(_clock, cachePath: CachePath);
        ServerHealthSnapshot snapshot = restored.GetSnapshot(Key(source));
        Assert.AreEqual(42d, snapshot.Aggregate!.AverageLatencyMilliseconds);
        Assert.AreEqual(DateTimeOffset.UnixEpoch, snapshot.LatestMeasurement!.CheckedAt);
        Assert.AreEqual(0, snapshot.Measurements.Count);
        Assert.IsTrue(snapshot.IsCached);
        Assert.IsTrue(snapshot.IsStale);
        Assert.AreEqual("Saved measurement", ServerHealthPresentation.FromSnapshot(snapshot).ConfidenceText);
        StringAssert.Contains(ServerHealthPresentation.FromSnapshot(snapshot).LastCheckedText, "Saved");
        StringAssert.Contains(ServerHealthPresentation.FromSnapshot(snapshot).LastCheckedText, "refresh due");
        Assert.AreEqual(1, source.ProbeCount);
        Assert.IsNull(restored.GetSnapshot(ServerHealthHistoryKey.Create(source.HealthServerId, "192.0.2.99")).Aggregate);
    }

    [TestMethod]
    public void UnmeasuredSnapshot_DoesNotPretendAProbeIsInProgress()
    {
        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(ServerHealthSnapshot.Empty(Key(Source())));
        Assert.AreEqual("Not measured", presentation.GradeText);
        Assert.AreEqual("—", presentation.LatencyText);
        Assert.AreEqual("Not measured", presentation.LastCheckedText);
    }

    [TestMethod]
    public async Task NetworkChange_MarksValuesStaleWithoutStartingNetworkWork()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(Success());
        using ServerHealthHistoryStore store = new(_clock);
        await store.ProbeAsync(source, CancellationToken.None);
        Assert.IsFalse(store.NeedsRefresh(source, TimeSpan.FromMinutes(5)));
        store.InvalidateFreshness();
        Assert.IsTrue(store.NeedsRefresh(source, TimeSpan.FromMinutes(5)));
        Assert.IsTrue(store.GetSnapshot(Key(source)).IsStale);
        Assert.AreEqual(42d, new ServerPingFilterSession(store).GetAverageLatencyMilliseconds(source));
        Assert.AreEqual(1, source.ProbeCount);
    }

    [TestMethod]
    public async Task NetworkChangeDuringProbe_DoesNotMarkOldNetworkResultFresh()
    {
        QueueServerHealthSource source = Source();
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Enqueue(_ => completion.Task);
        using ServerHealthHistoryStore store = new(_clock);
        Task<ServerHealthSnapshot> pending = store.ProbeAsync(source, CancellationToken.None);
        store.InvalidateFreshness();
        completion.SetResult(Success());
        Assert.IsTrue((await pending).IsStale);
        Assert.IsTrue(store.NeedsRefresh(source, TimeSpan.FromMinutes(5)));
    }

    [TestMethod]
    public async Task ThirtyDayExpiry_DropsSavedPingAsWellAsRollingHistory()
    {
        QueueServerHealthSource source = Source();
        source.Enqueue(Success());
        using ServerHealthHistoryStore store = new(_clock);
        await store.ProbeAsync(source, CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(31));
        Assert.IsNull(store.GetSnapshot(Key(source)).LatestMeasurement);
    }

    [TestMethod]
    public void CorruptAndOversizedCache_AreIgnored()
    {
        foreach (string content in new[] { "not json" })
        {
            File.WriteAllText(CachePath, content);
            using ServerHealthHistoryStore store = new(_clock, cachePath: CachePath);
            Assert.IsNull(store.GetSnapshot(Key(Source())).LatestMeasurement);
        }
        using (FileStream oversized = File.Create(CachePath)) { oversized.SetLength(64 * 1024 * 1024 + 1); }
        using ServerHealthHistoryStore oversizedStore = new(_clock, cachePath: CachePath);
        Assert.IsNull(oversizedStore.GetSnapshot(Key(Source())).LatestMeasurement);
    }

    [TestMethod]
    public void InvalidOrExpiredMeasurements_AreNotRestored()
    {
        SavedServerHealth valid = new(Key(Source()), Success());
        SavedServerHealth[] entries =
        [
            valid with { Measurement = Success() with { CheckedAt = _clock.UtcNow.AddDays(1) } },
            valid with { Measurement = Success() with { CheckedAt = _clock.UtcNow.AddDays(-31) } },
            valid with { Measurement = Success() with { TotalSamples = 0 } },
            valid with { Measurement = Success() with { SuccessfulSamples = 5 } },
            valid with { Measurement = Success() with { AverageLatencyMilliseconds = -1 } },
            valid with { Key = new("server", "not an endpoint") },
            valid with { Key = new("", "192.0.2.1") },
        ];
        File.WriteAllText(CachePath, JsonSerializer.Serialize(entries));
        using ServerHealthHistoryStore store = new(_clock, cachePath: CachePath);
        Assert.IsNull(store.GetSnapshot(valid.Key).LatestMeasurement);
    }

    [TestMethod]
    public void DuplicateEndpoint_RestoresNewestMeasurementRegardlessOfFileOrder()
    {
        SavedServerHealth older = new(Key(Source()), Success() with { AverageLatencyMilliseconds = 99, CheckedAt = _clock.UtcNow.AddMinutes(-10) });
        SavedServerHealth newer = new(new("server-persisted", "192.0.2.1"), Success());
        File.WriteAllText(CachePath, JsonSerializer.Serialize(new[] { newer, older }));
        using ServerHealthHistoryStore store = new(_clock, cachePath: CachePath);
        Assert.AreEqual(42d, store.GetSnapshot(Key(Source())).LatestMeasurement!.AverageLatencyMilliseconds);
    }

    [TestMethod]
    public async Task CacheCapacity_EvictsOldestAndPersistsOnlyBoundedEndpoints()
    {
        using (ServerHealthHistoryStore store = new(_clock, cachePath: CachePath, maximumCachedEntries: 64))
        {
            for (int i = 0; i < 65; i++)
            {
                QueueServerHealthSource source = new() { HealthServerId = $"bounded-{i}", HealthProbeAddress = "192.0.2.1" };
                source.Enqueue(Success());
                await store.ProbeAsync(source, CancellationToken.None);
                _clock.Advance(TimeSpan.FromSeconds(1));
            }
            Assert.IsNull(store.GetSnapshot(ServerHealthHistoryKey.Create("bounded-0", "192.0.2.1")).LatestMeasurement);
        }
        Assert.AreEqual(64, JsonSerializer.Deserialize<List<SavedServerHealth>>(File.ReadAllText(CachePath))!.Count);
        using ServerHealthHistoryStore restored = new(_clock, cachePath: CachePath);
        Assert.IsNotNull(restored.GetSnapshot(ServerHealthHistoryKey.Create("bounded-64", "192.0.2.1")).LatestMeasurement);
    }

    [TestMethod]
    public async Task DefaultCache_RetainsAndRestoresAnEntireCatalogueBeyondOldFourThousandLimit()
    {
        using (ServerHealthHistoryStore store = new(_clock, cachePath: CachePath))
        {
            for (int i = 0; i < 5000; i++)
            {
                QueueServerHealthSource source = new() { HealthServerId = $"catalogue-{i}", HealthProbeAddress = "192.0.2.1" };
                source.Enqueue(Success());
                await store.ProbeAsync(source, CancellationToken.None);
            }
            Assert.IsNotNull(store.GetSnapshot(ServerHealthHistoryKey.Create("catalogue-0", "192.0.2.1")).LatestMeasurement);
        }
        Assert.AreEqual(5000, JsonSerializer.Deserialize<List<SavedServerHealth>>(File.ReadAllText(CachePath))!.Count);
        using ServerHealthHistoryStore restored = new(_clock, cachePath: CachePath);
        Assert.IsNotNull(restored.GetSnapshot(ServerHealthHistoryKey.Create("catalogue-0", "192.0.2.1")).LatestMeasurement);
        Assert.IsNotNull(restored.GetSnapshot(ServerHealthHistoryKey.Create("catalogue-4999", "192.0.2.1")).LatestMeasurement);
    }

    [TestMethod]
    public async Task DisposeDuringUncooperativeProbe_DoesNotSaveLateResult()
    {
        QueueServerHealthSource source = Source();
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Enqueue(_ => completion.Task);
        ServerHealthHistoryStore store = new(_clock, cachePath: CachePath);
        Task<ServerHealthSnapshot> pending = store.ProbeAsync(source, CancellationToken.None);
        store.Dispose();
        completion.SetResult(Success());
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => pending);
        using ServerHealthHistoryStore restored = new(_clock, cachePath: CachePath);
        Assert.IsNull(restored.GetSnapshot(Key(source)).LatestMeasurement);
    }

    private ServerHealthProbeMeasurement Success() => new(42, 4, 4, _clock.UtcNow, true, null, 0.25);
    private static ServerHealthHistoryKey Key(QueueServerHealthSource source) => ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!);
    private static QueueServerHealthSource Source() => new() { HealthServerId = "server-persisted", HealthProbeAddress = "192.0.2.1", HealthServerLoad = 0.25 };
}
