using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

// Filtering is cache-only. Opening a list can request a small, immediate discovery pass.
public sealed class ServerHealthRefreshScheduler : IDisposable
{
    public const int MaximumInitialProbes = 24;
    public static readonly TimeSpan InitialPassDuration = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly ServerHealthHistoryStore _store;
    private readonly IServerHealthClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<ServerHealthHistoryKey, Candidate> _candidates = [];
    private readonly Queue<DateTimeOffset> _initialAdmissions = new();
    private readonly SemaphoreSlim _tick = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer? _timer;
    private Discovery? _activeDiscovery;
    private long _sequence;
    private bool _disposed;

    public ServerHealthRefreshScheduler(ServerHealthHistoryStore store, IServerHealthClock? clock = null, bool startTimer = true)
    {
        _store = store;
        _clock = clock ?? new SystemServerHealthClock();
        if (startTimer) { _timer = new(_ => RunTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)); }
    }

    public IDisposable Track(IServerHealthSource source)
    {
        lock (_sync)
        {
            Candidate? candidate = GetOrAdd(source);
            if (candidate is null) { return new Lease(() => { }); }
            candidate.InterestedViews++;
            return new Lease(() => { lock (_sync) { candidate.InterestedViews--; } });
        }
    }

    // Supply candidates in priority order, before filtering. Keep the lease while the list is open.
    public Discovery StartDiscovery(IEnumerable<IServerHealthSource> sources)
    {
        Discovery discovery;
        lock (_sync)
        {
            List<IServerHealthSource> unknown = [];
            List<IDisposable> interests = [];
            HashSet<ServerHealthHistoryKey> seen = [];
            int savedInterests = 0;
            if (!_disposed)
            {
                foreach (IServerHealthSource source in sources)
                {
                    if (string.IsNullOrWhiteSpace(source.HealthProbeAddress)) { continue; }
                    ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress);
                    if (!seen.Add(key)) { continue; }
                    if (_store.GetSnapshot(key).LatestMeasurement is null)
                    {
                        unknown.Add(source);
                        interests.Add(Track(source));
                        if (unknown.Count == MaximumInitialProbes) { break; }
                    }
                    else if (savedInterests++ < 8) { interests.Add(Track(source)); }
                }
            }
            discovery = new(interests);
            _activeDiscovery = discovery;
            // Never block the UI. The store publishes completed measurements individually.
            discovery.Completion = _disposed ? Task.CompletedTask : Task.Run(() => RunDiscoveryAsync(discovery, unknown));
        }
        return discovery;
    }

    private async Task RunDiscoveryAsync(Discovery discovery, List<IServerHealthSource> sources)
    {
        bool entered = false;
        try
        {
            await _tick.WaitAsync(_lifetime.Token);
            entered = true;
            DateTimeOffset deadline = _clock.UtcNow + InitialPassDuration;
            foreach (IServerHealthSource source in sources)
            {
                lock (_sync)
                {
                    if (_disposed || discovery.IsDisposed || _activeDiscovery != discovery || _clock.UtcNow >= deadline) { break; }
                    string? address = source.HealthProbeAddress;
                    if (string.IsNullOrWhiteSpace(address) || _store.GetSnapshot(ServerHealthHistoryKey.Create(source.HealthServerId, address)).LatestMeasurement is not null) { continue; }
                    // Rapid tab changes must not multiply the first-pass budget.
                    while (_initialAdmissions.TryPeek(out DateTimeOffset admission) && _clock.UtcNow - admission >= TimeSpan.FromMinutes(1))
                    { _initialAdmissions.Dequeue(); }
                    if (_initialAdmissions.Count >= MaximumInitialProbes * 2) { break; }
                    _initialAdmissions.Enqueue(_clock.UtcNow);
                }
                // Do not hold up discovery with the normal five-second no-reply retry.
                await _store.ProbeAsync(source, _lifetime.Token, retryFailure: false);
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(250), _lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { } // Optional telemetry must not interrupt the VPN or UI.
        finally { if (entered) { _tick.Release(); } }
    }

    private Candidate? GetOrAdd(IServerHealthSource source)
    {
        if (_disposed || string.IsNullOrWhiteSpace(source.HealthProbeAddress)) { return null; }
        ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress);
        if (_candidates.TryGetValue(key, out Candidate? candidate))
        { candidate.Source = source; candidate.Sequence = ++_sequence; return candidate; }
        foreach (ServerHealthHistoryKey expired in _candidates.Where(pair => pair.Value.InterestedViews == 0).Select(pair => pair.Key).ToArray())
        { _candidates.Remove(expired); }
        if (_candidates.Count >= 64)
        {
            // Newly viewed rows must not be refused because the first 64 filled the budget.
            _candidates.Remove(_candidates.MinBy(pair => pair.Value.Sequence).Key);
        }
        _candidates[key] = candidate = new(source) { Sequence = ++_sequence };
        return candidate;
    }

    private async void RunTick()
    {
        try { await TickAsync(); }
        catch (OperationCanceledException) { }
        catch { } // Optional telemetry failures must not interrupt the VPN or UI.
    }

    public async Task TickAsync()
    {
        if (!await _tick.WaitAsync(0)) { return; }
        try
        {
            KeyValuePair<ServerHealthHistoryKey, Candidate>[] candidates;
            lock (_sync)
            {
                if (_disposed) { return; }
                candidates = _candidates.Where(pair => pair.Value.InterestedViews > 0).ToArray();
            }
            ServerHealthHistoryKey[] due = candidates.Where(pair => _store.NeedsRefresh(pair.Value.Source, RefreshInterval))
                .OrderBy(pair => _store.GetSnapshot(pair.Key).LatestMeasurement?.CheckedAt ?? DateTimeOffset.MinValue)
                .ThenBy(pair => pair.Key.ServerId, StringComparer.Ordinal).Take(2).Select(pair => pair.Key).ToArray();
            foreach (ServerHealthHistoryKey key in due)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                IServerHealthSource source;
                lock (_sync)
                {
                    if (!_candidates.TryGetValue(key, out Candidate? candidate) || candidate.InterestedViews == 0)
                    {
                        continue;
                    }
                    source = candidate.Source;
                }
                if (!_store.NeedsRefresh(source, RefreshInterval)) { continue; }
                await _store.ProbeAsync(source, _lifetime.Token);
                await _clock.DelayAsync(TimeSpan.FromSeconds(1), _lifetime.Token);
            }
        }
        finally { _tick.Release(); }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _candidates.Clear(); _activeDiscovery?.Dispose(); }
        _timer?.Dispose();
        _lifetime.Cancel();
    }

    private sealed class Candidate(IServerHealthSource source)
    {
        public IServerHealthSource Source { get; set; } = source;
        public int InterestedViews { get; set; }
        public long Sequence { get; set; }
    }

    public sealed class Discovery : IDisposable
    {
        private List<IDisposable>? _interests;
        internal bool IsDisposed => Volatile.Read(ref _interests) is null;
        public Task Completion { get; internal set; } = Task.CompletedTask;
        internal Discovery(List<IDisposable> interests) => _interests = interests;
        public void Dispose()
        {
            List<IDisposable>? interests = Interlocked.Exchange(ref _interests, null);
            if (interests is not null) { foreach (IDisposable interest in interests) { interest.Dispose(); } }
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
