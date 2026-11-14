using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Tests.ServerHealth;

[TestClass]
public class ServerHealthRefreshSchedulerTest
{
    private readonly FakeServerHealthClock _clock = new();
    private ServerHealthHistoryStore _store = null!;
    private ServerHealthRefreshScheduler _scheduler = null!;

    [TestInitialize]
    public void Initialize()
    {
        _store = new(_clock, maximumConcurrentProbes: 32);
        _scheduler = new(_store, _clock, startTimer: false);
    }

    [TestCleanup]
    public void Cleanup() { _scheduler.Dispose(); _store.Dispose(); }

    [TestMethod]
    public async Task OpeningTab_MeasuresItsEntireCatalogueWithQuickRequestsNotSmallSelection()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 1000).Select(Source).ToArray();
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await discovery.Completion;
        Assert.IsTrue(sources.All(source => source.ProbeCount == 1));
        Assert.IsTrue(sources.All(source => source.QuickRequests.SequenceEqual([true])));
        Assert.AreEqual(1000, discovery.CompletedSources);
        Assert.IsFalse(_scheduler.IsMeasuring);
        Assert.AreEqual(0, _clock.Delays.Count); // No per-server pacing or slow failure retry.
    }

    [TestMethod]
    public async Task WholeTabPass_HasBoundedConcurrencyAndProgress()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0, active = 0, maximum = 0;
        QueueServerHealthSource[] sources = Enumerable.Range(1, 96).Select(index =>
        {
            QueueServerHealthSource source = new() { HealthServerId = $"pending-{index}", HealthProbeAddress = "192.0.2.1" };
            source.Enqueue(async _ =>
            {
                int running = Interlocked.Increment(ref active);
                int observed;
                do { observed = Volatile.Read(ref maximum); }
                while (running > observed && Interlocked.CompareExchange(ref maximum, running, observed) != observed);
                if (Interlocked.Increment(ref entered) == 32) { started.SetResult(); }
                await release.Task;
                Interlocked.Decrement(ref active);
                return Success();
            });
            return source;
        }).ToArray();
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(32, sources.Sum(source => source.ProbeCount));
        Assert.IsTrue(_scheduler.IsMeasuring);
        Assert.AreEqual(96, discovery.TotalSources);
        Assert.AreEqual(0, discovery.CompletedSources);
        release.SetResult();
        await discovery.Completion;
        Assert.AreEqual(32, maximum);
        Assert.AreEqual(96, discovery.CompletedSources);
    }

    [TestMethod]
    public async Task FreshCachedEndpoints_AppearImmediatelyAndAreNotRepeatedWhenChangingLists()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 100).Select(Source).ToArray();
        foreach (QueueServerHealthSource source in sources.Take(60)) { await _store.ProbeAsync(source, CancellationToken.None); }
        using (ServerHealthRefreshScheduler.Discovery first = _scheduler.StartDiscovery(sources)) { await first.Completion; }
        using (ServerHealthRefreshScheduler.Discovery reopened = _scheduler.StartDiscovery(sources)) { await reopened.Completion; }
        Assert.IsTrue(sources.All(source => source.ProbeCount == 1));
        Assert.IsTrue(sources.Take(60).All(source => source.QuickRequests.SequenceEqual([false])));
        Assert.IsTrue(sources.Skip(60).All(source => source.QuickRequests.SequenceEqual([true])));
    }

    [TestMethod]
    public async Task FeatureScope_DoesNotProbeServersOutsideSelectedTab()
    {
        QueueServerHealthSource[] all = Enumerable.Range(1, 500).Select(Source).ToArray();
        QueueServerHealthSource[] p2p = all.Where((_, index) => index % 4 == 0).ToArray();
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(p2p);
        await discovery.Completion;
        Assert.IsTrue(p2p.All(source => source.ProbeCount == 1));
        Assert.IsTrue(all.Except(p2p).All(source => source.ProbeCount == 0));
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _scheduler.TickAsync();
        Assert.IsTrue(p2p.All(source => source.ProbeCount == 2));
        Assert.IsTrue(all.Except(p2p).All(source => source.ProbeCount == 0));
    }

    [TestMethod]
    public async Task LateLoadedRows_FromOtherTabsCannotEscapeFeatureScope()
    {
        QueueServerHealthSource active = Source(1);
        QueueServerHealthSource previousTab = Source(2);
        using IDisposable previousInterest = _scheduler.Track(previousTab);
        using IDisposable currentInterest = _scheduler.Track(active);
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery([active]);
        await discovery.Completion;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _scheduler.TickAsync();
        Assert.AreEqual(2, active.ProbeCount);
        Assert.AreEqual(0, previousTab.ProbeCount);
        Assert.IsFalse(active.QuickRequests.Last());
    }

    [TestMethod]
    public async Task ClosedTab_DoesNotRefineRowsWaitingForUnloadCallbacks()
    {
        QueueServerHealthSource source = Source(1);
        using IDisposable interest = _scheduler.Track(source);
        ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery([source]);
        await discovery.Completion;
        discovery.Dispose();
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _scheduler.TickAsync();
        Assert.AreEqual(1, source.ProbeCount);
    }

    [TestMethod]
    public async Task SwitchingTabs_CancelsQueuedOldWorkAndMeasuresAllNewTabServers()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        QueueServerHealthSource[] oldSources = Enumerable.Range(1, 100).Select(index =>
        {
            QueueServerHealthSource source = new() { HealthServerId = $"old-{index}", HealthProbeAddress = "192.0.2.1" };
            source.Enqueue(async _ => { if (Interlocked.Increment(ref entered) == 32) { started.SetResult(); } await release.Task; return Success(); });
            return source;
        }).ToArray();
        using ServerHealthRefreshScheduler.Discovery old = _scheduler.StartDiscovery(oldSources);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        QueueServerHealthSource[] newSources = Enumerable.Range(1, 50).Select(Source).ToArray();
        using ServerHealthRefreshScheduler.Discovery current = _scheduler.StartDiscovery(newSources);
        await old.Completion;
        release.SetResult();
        await current.Completion;
        Assert.AreEqual(32, oldSources.Sum(source => source.ProbeCount));
        Assert.IsTrue(newSources.All(source => source.ProbeCount == 1));
    }

    [TestMethod]
    public async Task ClosingTab_CancelsQueuedChecksAndStopsPeriodicPasses()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        QueueServerHealthSource[] sources = Enumerable.Range(1, 100).Select(index =>
        {
            QueueServerHealthSource source = new() { HealthServerId = $"closing-{index}", HealthProbeAddress = "192.0.2.1" };
            source.Enqueue(async _ => { if (Interlocked.Increment(ref entered) == 32) { started.SetResult(); } await release.Task; return Success(); });
            return source;
        }).ToArray();
        ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        discovery.Dispose();
        discovery.Dispose();
        await discovery.Completion;
        release.SetResult();
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _scheduler.TickAsync();
        Assert.AreEqual(32, sources.Sum(source => source.ProbeCount));
        Assert.IsFalse(_scheduler.IsMeasuring);
    }

    [TestMethod]
    public async Task DuplicateLogicalEndpoints_AreMeasuredOnceWithinPass()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 50).Select(Source).ToArray();
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources.SelectMany(source => new[] { source, source }));
        await discovery.Completion;
        Assert.IsTrue(sources.All(source => source.ProbeCount == 1));
    }

    [TestMethod]
    public async Task Filter_ShowsPendingRowsAndUpdatesFromFirstReplyWithoutRestartingPass()
    {
        ServerPingFilterSession filter = new(_store);
        filter.SelectedOption = filter.Options.Single(option => option.MaxLatencyMilliseconds == 50);
        QueueServerHealthSource saved = Source(1);
        await _store.ProbeAsync(saved, CancellationToken.None);
        QueueServerHealthSource high = new() { HealthServerId = "high", HealthProbeAddress = "192.0.2.1" };
        high.Enqueue(new ServerHealthProbeMeasurement(180, 1, 1, _clock.UtcNow, true, null, 0));
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource pending = new() { HealthServerId = "pending", HealthProbeAddress = "192.0.2.1" };
        pending.Enqueue(_ => { started.SetResult(); return completion.Task; });
        Assert.IsTrue(filter.Matches(saved));
        Assert.IsTrue(filter.Matches(pending)); // Not hidden just because the first pass has not reached it.
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery([saved, high, pending]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (ServerPingFilterOption option in filter.Options) { filter.SelectedOption = option; }
        completion.SetResult(new ServerHealthProbeMeasurement(35, 1, 1, _clock.UtcNow, true, null, 0));
        await discovery.Completion;
        filter.SelectedOption = filter.Options.Single(option => option.MaxLatencyMilliseconds == 50);
        Assert.IsTrue(filter.Matches(saved));
        Assert.IsTrue(filter.Matches(pending));
        Assert.IsFalse(filter.Matches(high));
        Assert.AreEqual(1, saved.ProbeCount);
        Assert.AreEqual(1, pending.ProbeCount);
    }

    [TestMethod]
    public async Task QuickFailure_DoesNotRetryOrDeclareOutageAndRetainsTenMinuteCooldown()
    {
        QueueServerHealthSource failed = new() { HealthServerId = "failed", HealthProbeAddress = "192.0.2.1" };
        failed.Enqueue(new ServerHealthProbeMeasurement(null, 0, 2, _clock.UtcNow, true, "No reply", 0));
        failed.Enqueue(_ => Task.FromResult(Success()));
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery([failed]);
        await discovery.Completion;
        Assert.AreEqual(1, failed.ProbeCount);
        Assert.IsFalse(_store.GetSnapshot(ServerHealthHistoryKey.Create(failed.HealthServerId, failed.HealthProbeAddress)).LatestMeasurement!.IsConfirmedOutage);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _scheduler.TickAsync();
        Assert.AreEqual(1, failed.ProbeCount);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _scheduler.TickAsync();
        Assert.AreEqual(2, failed.ProbeCount);
        Assert.AreEqual(0, _clock.Delays.Count);
    }

    [TestMethod]
    public async Task VisibleRows_RefineNormallyAfterOneMinuteAndStopWhenUnloaded()
    {
        QueueServerHealthSource source = Source(1);
        using (IDisposable interest = _scheduler.Track(source))
        {
            await _scheduler.TickAsync();
            _clock.Advance(TimeSpan.FromSeconds(30));
            await _scheduler.TickAsync();
            Assert.AreEqual(1, source.ProbeCount);
            _clock.Advance(TimeSpan.FromSeconds(30));
            await _scheduler.TickAsync();
            Assert.AreEqual(2, source.ProbeCount);
            Assert.IsTrue(source.QuickRequests.All(quick => !quick));
        }
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _scheduler.TickAsync();
        Assert.AreEqual(2, source.ProbeCount);
    }

    [TestMethod]
    public async Task NormalRefinement_RetainsFailureRetry()
    {
        QueueServerHealthSource source = new() { HealthServerId = "retry", HealthProbeAddress = "192.0.2.1" };
        source.Enqueue(new ServerHealthProbeMeasurement(null, 0, 4, _clock.UtcNow, true, "No reply", 0));
        source.Enqueue(_ => Task.FromResult(Success()));
        using IDisposable interest = _scheduler.Track(source);
        await _scheduler.TickAsync();
        Assert.AreEqual(2, source.ProbeCount);
        CollectionAssert.AreEqual(new[] { false, false }, source.QuickRequests.ToArray());
        Assert.IsTrue(_clock.Delays.Contains(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task VisibleRefinementBound_DoesNotLimitInitialCatalogueCoverage()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 100).Select(Source).ToArray();
        List<IDisposable> interests = sources.Select(_scheduler.Track).ToList();
        await _scheduler.TickAsync();
        Assert.AreEqual(64, sources.Sum(source => source.ProbeCount));
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await discovery.Completion;
        Assert.AreEqual(100, sources.Sum(source => source.ProbeCount));
        foreach (IDisposable interest in interests) { interest.Dispose(); }
    }

    private ServerHealthProbeMeasurement Success() => new(42, 4, 4, _clock.UtcNow, true, null, 0.25);
    private QueueServerHealthSource Source(int index)
    {
        QueueServerHealthSource source = new() { HealthServerId = $"server-{index:D3}", HealthProbeAddress = "192.0.2.1", HealthServerLoad = 0.25 };
        for (int i = 0; i < 4; i++) { source.Enqueue(_ => Task.FromResult(Success())); }
        return source;
    }
}
