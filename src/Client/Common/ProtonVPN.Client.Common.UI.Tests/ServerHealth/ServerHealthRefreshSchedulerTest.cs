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
    public async Task OfferingCatalogue_DoesNotProbeAndRefreshesOnlyEightCandidatesTwoPerTick()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 1000).Select(Source).ToArray();
        _scheduler.OfferCandidates(sources);
        Assert.AreEqual(0, sources.Sum(source => source.ProbeCount));
        await _scheduler.TickAsync();
        Assert.AreEqual(2, sources.Sum(source => source.ProbeCount));
        for (int i = 0; i < 5; i++) { await _scheduler.TickAsync(); }
        Assert.AreEqual(8, sources.Sum(source => source.ProbeCount));
        Assert.AreEqual(0, sources.Skip(8).Sum(source => source.ProbeCount));
        Assert.AreEqual(8, _clock.Delays.Count(delay => delay == TimeSpan.FromSeconds(1)));
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
    public async Task ExpiredOffers_StopBackgroundWork()
    {
        QueueServerHealthSource[] sources = Enumerable.Range(1, 8).Select(Source).ToArray();
        _scheduler.OfferCandidates(sources);
        _clock.Advance(TimeSpan.FromMinutes(6));
        await _scheduler.TickAsync();
        Assert.AreEqual(0, sources.Sum(source => source.ProbeCount));
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
        Assert.AreEqual(0, sources.Skip(64).Sum(source => source.ProbeCount));
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
