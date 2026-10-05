using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

// Views declare interest; loading, filtering and hovering never perform a probe themselves.
public sealed class ServerHealthRefreshScheduler : IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly ServerHealthHistoryStore _store;
    private readonly IServerHealthClock _clock;
    private readonly object _sync = new();
    private readonly Dictionary<ServerHealthHistoryKey, Candidate> _candidates = [];
    private readonly SemaphoreSlim _tick = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer? _timer;
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

    public void OfferCandidates(IEnumerable<IServerHealthSource> sources)
    {
        lock (_sync)
        {
            foreach (IServerHealthSource source in sources.Take(8))
            {
                Candidate? candidate = GetOrAdd(source);
                if (candidate is not null) { candidate.OfferedUntil = _clock.UtcNow.AddMinutes(5); }
            }
        }
    }

    private Candidate? GetOrAdd(IServerHealthSource source)
    {
        if (_disposed || string.IsNullOrWhiteSpace(source.HealthProbeAddress)) { return null; }
        ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress);
        if (_candidates.TryGetValue(key, out Candidate? candidate)) { candidate.Source = source; return candidate; }
        foreach (ServerHealthHistoryKey expired in _candidates.Where(pair => pair.Value.InterestedViews == 0 && pair.Value.OfferedUntil <= _clock.UtcNow).Select(pair => pair.Key).ToArray())
        { _candidates.Remove(expired); }
        if (_candidates.Count >= 64) { return null; } // Bound interest, not the saved cache or server catalogue.
        _candidates[key] = candidate = new(source);
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
                candidates = _candidates.Where(pair => pair.Value.InterestedViews > 0 || pair.Value.OfferedUntil > _clock.UtcNow).ToArray();
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
                    if (!_candidates.TryGetValue(key, out Candidate? candidate) ||
                        (candidate.InterestedViews == 0 && candidate.OfferedUntil <= _clock.UtcNow))
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
        lock (_sync) { _disposed = true; _candidates.Clear(); }
        _timer?.Dispose();
        _lifetime.Cancel();
    }

    private sealed class Candidate(IServerHealthSource source)
    {
        public IServerHealthSource Source { get; set; } = source;
        public int InterestedViews { get; set; }
        public DateTimeOffset OfferedUntil { get; set; }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
