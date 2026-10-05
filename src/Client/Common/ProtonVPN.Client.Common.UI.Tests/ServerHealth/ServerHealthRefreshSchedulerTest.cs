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
        _store = new(_clock, maximumConcurrentProbes: 1);
        _scheduler = new(_store, _clock, startTimer: false);
    }

    [TestCleanup]
    public void Cleanup() { _scheduler.Dispose(); _store.Dispose(); }

    [TestMethod]
    public async Task OpeningCatalogue_StartsImmediatelyButStopsAtTwentyFourUncachedEndpoints()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 1000).Select(Source).ToArray();
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await discovery.Completion;
        Assert.AreEqual(24, sources.Sum(source => source.ProbeCount));
        Assert.AreEqual(0, sources.Skip(24).Sum(source => source.ProbeCount));
        Assert.AreEqual(24, _clock.Delays.Count(delay => delay == TimeSpan.FromMilliseconds(250)));
    }

    [TestMethod]
    public async Task InitialPass_SkipsCachedEndpointsBeforeApplyingItsLimit()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 60).Select(Source).ToArray();
        foreach (QueueServerHealthSource source in sources.Take(30)) { await _store.ProbeAsync(source, CancellationToken.None); }
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await discovery.Completion;
        Assert.AreEqual(30, sources.Take(30).Sum(source => source.ProbeCount));
        Assert.AreEqual(24, sources.Skip(30).Sum(source => source.ProbeCount));
        Assert.AreEqual(0, sources.Skip(54).Sum(source => source.ProbeCount));
    }

    [TestMethod]
    public async Task InitialPass_PreservesCallerPriorityAndDeduplicatesEndpoints()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 30).Select(Source).Reverse().ToArray();
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources.SelectMany(source => new[] { source, source }));
        await discovery.Completion;
        Assert.IsTrue(sources.Take(24).All(source => source.ProbeCount == 1));
        Assert.IsTrue(sources.Skip(24).All(source => source.ProbeCount == 0));
    }

    [TestMethod]
    public async Task InitialPass_StopsAdmittingAfterTwentySeconds()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 50).Select(index =>
        {
            QueueServerHealthSource source = new() { HealthServerId = $"slow-{index}", HealthProbeAddress = "192.0.2.1" };
            source.Enqueue(_ => { _clock.Advance(TimeSpan.FromSeconds(5)); return Task.FromResult(Success()); });
            return source;
        }).ToArray();
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await discovery.Completion;
        Assert.AreEqual(4, sources.Sum(source => source.ProbeCount));
    }

    [TestMethod]
    public async Task InitialPass_NoRepliesDoNotRetryOrDeclareConfirmedOutage()
    {
        QueueServerHealthSource failed = new() { HealthServerId = "failed", HealthProbeAddress = "192.0.2.1" };
        failed.Enqueue(new ServerHealthProbeMeasurement(null, 0, 4, _clock.UtcNow, true, "No reply", 0));
        QueueServerHealthSource next = Source(2);
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery([failed, next]);
        await discovery.Completion;
        Assert.AreEqual(1, failed.ProbeCount);
        Assert.AreEqual(1, next.ProbeCount);
        Assert.IsFalse(_store.GetSnapshot(ServerHealthHistoryKey.Create(failed.HealthServerId, failed.HealthProbeAddress)).LatestMeasurement!.IsConfirmedOutage);
        Assert.IsFalse(_clock.Delays.Contains(TimeSpan.FromSeconds(5)));
        await _scheduler.TickAsync();
        Assert.AreEqual(1, failed.ProbeCount); // Normal failure cooldown still applies.
    }

    [TestMethod]
    public async Task ClosingList_StopsQueuedInitialProbesAndPeriodicRefresh()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource pending = new() { HealthServerId = "pending", HealthProbeAddress = "192.0.2.1" };
        pending.Enqueue(_ => { entered.SetResult(); return completion.Task; });
        QueueServerHealthSource next = Source(2);
        ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery([pending, next]);
        await entered.Task;
        discovery.Dispose();
        discovery.Dispose();
        completion.SetResult(Success());
        await discovery.Completion;
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _scheduler.TickAsync();
        Assert.AreEqual(1, pending.ProbeCount);
        Assert.AreEqual(0, next.ProbeCount);
    }

    [TestMethod]
    public async Task NewList_SupersedesOldInitialPassWithoutParallelProbes()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource pending = new() { HealthServerId = "pending", HealthProbeAddress = "192.0.2.1" };
        pending.Enqueue(_ => { entered.SetResult(); return completion.Task; });
        QueueServerHealthSource oldNext = Source(2);
        using ServerHealthRefreshScheduler.Discovery old = _scheduler.StartDiscovery([pending, oldNext]);
        await entered.Task;
        QueueServerHealthSource newNext = Source(3);
        using ServerHealthRefreshScheduler.Discovery current = _scheduler.StartDiscovery([newNext]);
        Assert.AreEqual(0, newNext.ProbeCount);
        await _scheduler.TickAsync();
        Assert.AreEqual(0, oldNext.ProbeCount);
        completion.SetResult(Success());
        await Task.WhenAll(old.Completion, current.Completion);
        Assert.AreEqual(0, oldNext.ProbeCount);
        Assert.AreEqual(1, newNext.ProbeCount);
    }

    [TestMethod]
    public async Task ReopeningList_ProgressesBeyondItsFirstSelectionInsteadOfRepeatingIt()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 100).Select(Source).ToArray();
        using (ServerHealthRefreshScheduler.Discovery first = _scheduler.StartDiscovery(sources)) { await first.Completion; }
        using (ServerHealthRefreshScheduler.Discovery second = _scheduler.StartDiscovery(sources)) { await second.Completion; }
        Assert.IsTrue(sources.Take(48).All(source => source.ProbeCount == 1));
        Assert.IsTrue(sources.Skip(48).All(source => source.ProbeCount == 0));
        using (ServerHealthRefreshScheduler.Discovery third = _scheduler.StartDiscovery(sources)) { await third.Completion; }
        Assert.AreEqual(48, sources.Sum(source => source.ProbeCount)); // Global cap across rapid list switches.
        _clock.Advance(TimeSpan.FromMinutes(1));
        using (ServerHealthRefreshScheduler.Discovery later = _scheduler.StartDiscovery(sources)) { await later.Completion; }
        Assert.AreEqual(72, sources.Sum(source => source.ProbeCount));
    }

    [TestMethod]
    public async Task Filter_ShowsCachedAndFreshMatchesIncrementallyWithoutRestartingPass()
    {
        ServerPingFilterSession filter = new(_store);
        filter.SelectedOption = filter.Options.Single(option => option.MaxLatencyMilliseconds == 50);
        QueueServerHealthSource saved = Source(1);
        await _store.ProbeAsync(saved, CancellationToken.None);
        QueueServerHealthSource fresh = Source(2);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource pending = new() { HealthServerId = "pending", HealthProbeAddress = "192.0.2.1" };
        pending.Enqueue(_ => { entered.SetResult(); return completion.Task; });
        Assert.IsTrue(filter.Matches(saved));
        Assert.IsFalse(filter.Matches(fresh));
        using ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery([saved, fresh, pending]);
        await entered.Task;
        Assert.IsTrue(filter.Matches(saved));
        Assert.IsTrue(filter.Matches(fresh));
        Assert.IsFalse(filter.Matches(pending));
        foreach (ServerPingFilterOption option in filter.Options) { filter.SelectedOption = option; }
        Assert.AreEqual(1, saved.ProbeCount);
        Assert.AreEqual(1, fresh.ProbeCount);
        Assert.AreEqual(1, pending.ProbeCount);
        completion.SetResult(Success());
        await discovery.Completion;
        filter.SelectedOption = filter.Options.Single(option => option.MaxLatencyMilliseconds == 50);
        Assert.IsTrue(filter.Matches(pending));
    }

    [TestMethod]
    public async Task UnloadedView_DoesNotProbeUntilAnotherViewDeclaresInterest()
    {
        QueueServerHealthSource source = Source(1);
        IDisposable lease = _scheduler.Track(source);
        lease.Dispose();
        lease.Dispose();
        await _scheduler.TickAsync();
        Assert.AreEqual(0, source.ProbeCount);
        using IDisposable next = _scheduler.Track(source);
        await _scheduler.TickAsync();
        Assert.AreEqual(1, source.ProbeCount);
    }

    [TestMethod]
    public async Task ViewsUnloadedDuringTick_DoNotStartQueuedProbes()
    {
        QueueServerHealthSource first = Source(1);
        QueueServerHealthSource second = Source(2);
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        QueueServerHealthSource pending = new() { HealthServerId = "server-0", HealthProbeAddress = "192.0.2.1" };
        pending.Enqueue(_ => completion.Task);
        using IDisposable firstLease = _scheduler.Track(pending);
        using IDisposable unusedFirst = _scheduler.Track(first);
        IDisposable secondLease = _scheduler.Track(second);
        Task tick = _scheduler.TickAsync();
        unusedFirst.Dispose();
        secondLease.Dispose();
        completion.SetResult(Success());
        await tick;
        Assert.AreEqual(0, first.ProbeCount);
        Assert.AreEqual(0, second.ProbeCount);
    }

    [TestMethod]
    public async Task FreshSavedValue_IsNotRefreshedForFiveMinutes()
    {
        QueueServerHealthSource source = Source(1);
        using IDisposable lease = _scheduler.Track(source);
        await _scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(4));
        await _scheduler.TickAsync();
        Assert.AreEqual(1, source.ProbeCount);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _scheduler.TickAsync();
        Assert.AreEqual(2, source.ProbeCount);
    }

    [TestMethod]
    public async Task FailedServer_HasTenMinuteCooldownNotFiveMinutePolling()
    {
        QueueServerHealthSource source = new() { HealthServerId = "failed", HealthProbeAddress = "192.0.2.2" };
        source.Enqueue(new ServerHealthProbeMeasurement(null, 0, 4, _clock.UtcNow, true, "No reply", 0));
        source.Enqueue(new ServerHealthProbeMeasurement(null, 0, 4, _clock.UtcNow, true, "No reply", 0));
        source.Enqueue(_ => Task.FromResult(Success()));
        using IDisposable lease = _scheduler.Track(source);
        await _scheduler.TickAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _scheduler.TickAsync();
        Assert.AreEqual(2, source.ProbeCount);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await _scheduler.TickAsync();
        Assert.AreEqual(3, source.ProbeCount);
    }

    [TestMethod]
    public async Task WarmedList_RetainsOnlySmallRefreshInterestUntilClosed()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 100).Select(Source).ToArray();
        ServerHealthRefreshScheduler.Discovery discovery = _scheduler.StartDiscovery(sources);
        await discovery.Completion;
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _scheduler.TickAsync();
        Assert.AreEqual(26, sources.Sum(source => source.ProbeCount));
        discovery.Dispose();
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _scheduler.TickAsync();
        Assert.AreEqual(26, sources.Sum(source => source.ProbeCount));
        Assert.AreEqual(0, sources.Skip(24).Sum(source => source.ProbeCount));
    }

    [TestMethod]
    public async Task ConcurrentTicks_DoNotMultiplyAdmissionBudget()
    {
        QueueServerHealthSource source = new() { HealthServerId = "pending", HealthProbeAddress = "192.0.2.1" };
        TaskCompletionSource<ServerHealthProbeMeasurement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Enqueue(_ => completion.Task);
        using IDisposable lease = _scheduler.Track(source);
        Task first = _scheduler.TickAsync();
        await _scheduler.TickAsync();
        Assert.AreEqual(1, source.ProbeCount);
        completion.SetResult(Success());
        await first;
    }

    [TestMethod]
    public async Task InterestHasGlobalBound()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 100).Select(Source).ToArray();
        List<IDisposable> leases = sources.Select(_scheduler.Track).ToList();
        for (int i = 0; i < 40; i++) { await _scheduler.TickAsync(); }
        Assert.AreEqual(64, sources.Sum(source => source.ProbeCount));
        Assert.AreEqual(0, sources.Take(36).Sum(source => source.ProbeCount));
        Assert.IsTrue(sources.Skip(36).All(source => source.ProbeCount == 1));
        foreach (IDisposable lease in leases) { lease.Dispose(); }
    }

    private ServerHealthProbeMeasurement Success() => new(42, 4, 4, _clock.UtcNow, true, null, 0.25);
    private QueueServerHealthSource Source(int index)
    {
        QueueServerHealthSource source = new() { HealthServerId = $"server-{index:D3}", HealthProbeAddress = "192.0.2.1", HealthServerLoad = 0.25 };
        for (int i = 0; i < 4; i++) { source.Enqueue(_ => Task.FromResult(Success())); }
        return source;
    }
}
