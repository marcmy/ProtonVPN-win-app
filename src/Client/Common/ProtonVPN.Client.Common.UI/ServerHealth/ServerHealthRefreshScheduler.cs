using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

// Whole active-tab coverage, bounded concurrency. Ping thresholds never start a pass.
public sealed class ServerHealthRefreshScheduler : IDisposable
{
    public const int MaximumConcurrentFirstChecks = 32;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private readonly ServerHealthHistoryStore _store;
    private readonly object _sync = new();
    private readonly Dictionary<ServerHealthHistoryKey, Candidate> _candidates = [];
    private readonly SemaphoreSlim _pass = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer? _timer;
    private Discovery? _activeDiscovery;
    private long _sequence;
    private bool _disposed;

    public event EventHandler? ProgressChanged;
    public bool IsMeasuring { get { lock (_sync) { return _activeDiscovery?.IsRunning == true; } } }
    public string ProgressText
    {
        get
        {
            lock (_sync)
            {
                return _activeDiscovery?.IsRunning == true
                    ? $"Measuring pings… {_activeDiscovery.CompletedSources:N0}/{_activeDiscovery.TotalSources:N0}"
                    : string.Empty;
            }
        }
    }

    public ServerHealthRefreshScheduler(ServerHealthHistoryStore store, IServerHealthClock? clock = null, bool startTimer = true)
    {
        _store = store;
        if (startTimer) { _timer = new(_ => RunTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)); }
    }

    public IDisposable Track(IServerHealthSource source)
    {
        lock (_sync)
        {
            if (_disposed || string.IsNullOrWhiteSpace(source.HealthProbeAddress)) { return new Lease(() => { }); }
            ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress);
            if (!_candidates.TryGetValue(key, out Candidate? candidate))
            {
                foreach (ServerHealthHistoryKey inactive in _candidates.Where(pair => pair.Value.InterestedViews == 0).Select(pair => pair.Key).ToArray())
                { _candidates.Remove(inactive); }
                // This limit only bounds visible-row refinement, never initial tab coverage.
                if (_candidates.Count >= 64) { _candidates.Remove(_candidates.MinBy(pair => pair.Value.Sequence).Key); }
                _candidates[key] = candidate = new(source);
            }
            candidate.Source = source;
            candidate.Sequence = ++_sequence;
            candidate.InterestedViews++;
            return new Lease(() => { lock (_sync) { candidate.InterestedViews--; } });
        }
    }

    // Supply the full unfiltered catalogue for the active feature tab, ordered by usefulness.
    public Discovery StartDiscovery(IEnumerable<IServerHealthSource> sources)
    {
        lock (_sync)
        {
            _activeDiscovery?.Dispose();
            IServerHealthSource[] catalogue = _disposed ? [] : sources
                .Where(source => !string.IsNullOrWhiteSpace(source.HealthProbeAddress))
                .DistinctBy(source => ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)).ToArray();
            Discovery discovery = new(catalogue, _lifetime.Token);
            _activeDiscovery = discovery;
            discovery.Completion = _disposed ? Task.CompletedTask : Task.Run(() => RunDiscoveryAsync(discovery));
            return discovery;
        }
    }

    private async Task RunDiscoveryAsync(Discovery discovery)
    {
        bool entered = false;
        CancellationToken token = discovery.Token;
        try
        {
            await _pass.WaitAsync(token);
            entered = true;
            IServerHealthSource[] due = discovery.Sources.Where(source => _store.NeedsRefresh(source, RefreshInterval))
                // New endpoints first; keep caller ranking stable within each group.
                .OrderBy(source => _store.GetSnapshot(ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)).LatestMeasurement is not null)
                .ToArray();
            discovery.BeginPass(discovery.TotalSources - due.Length);
            RaiseProgress();
            await Parallel.ForEachAsync(due, new ParallelOptions
            { MaxDegreeOfParallelism = MaximumConcurrentFirstChecks, CancellationToken = token }, async (source, cancellation) =>
            {
                try
                {
                    if (_store.NeedsRefresh(source, RefreshInterval))
                    { await _store.ProbeAsync(source, cancellation, retryFailure: false, quickFirstResponse: true); }
                }
                finally { discovery.CompleteSource(); RaiseProgress(); }
            });
        }
        catch (OperationCanceledException) { }
        catch { } // Optional telemetry must not interrupt the VPN or UI.
        finally
        {
            discovery.EndPass();
            RaiseProgress();
            if (entered) { _pass.Release(); }
        }
    }

    private void RaiseProgress()
    {
        if (!_disposed) { ProgressChanged?.Invoke(this, EventArgs.Empty); }
    }

    private async void RunTick()
    {
        try { await TickAsync(); }
        catch (OperationCanceledException) { }
        catch { }
    }

    public async Task TickAsync()
    {
        Task? refresh = null;
        IServerHealthSource[] visible;
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed) { return; }
            // Loaded/unloaded callbacks can trail navigation. A closed scope must
            // not keep refining its rows, and old-tab rows cannot leak into a new tab.
            if (_activeDiscovery is { IsDisposed: true }) { return; }
            Discovery? scope = _activeDiscovery is { IsDisposed: false } ? _activeDiscovery : null;
            if (scope is not null)
            {
                if (!scope.Completion.IsCompleted) { return; }
                if (scope.Sources.Any(source => _store.NeedsRefresh(source, RefreshInterval)))
                { refresh = scope.Completion = Task.Run(() => RunDiscoveryAsync(scope)); }
            }
            visible = _candidates.Values.Where(candidate => candidate.InterestedViews > 0
                    && (scope is null || scope.Contains(candidate.Source)))
                .Select(candidate => candidate.Source).ToArray();
            token = scope?.Token ?? _lifetime.Token;
        }
        if (refresh is not null) { await refresh; return; }
        if (!await _pass.WaitAsync(0, token)) { return; }
        try
        {
            await Parallel.ForEachAsync(visible, new ParallelOptions
            { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (source, cancellation) =>
            {
                lock (_sync)
                {
                    ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!);
                    if (!_candidates.TryGetValue(key, out Candidate? candidate) || candidate.InterestedViews == 0) { return; }
                }
                if (_store.NeedsRefresh(source, TimeSpan.FromMinutes(1))) { await _store.ProbeAsync(source, cancellation); }
            });
        }
        finally { _pass.Release(); }
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; _activeDiscovery?.Dispose(); _candidates.Clear(); }
        _timer?.Dispose();
        _lifetime.Cancel();
        ProgressChanged = null;
    }

    private sealed class Candidate(IServerHealthSource source)
    {
        public IServerHealthSource Source { get; set; } = source;
        public int InterestedViews { get; set; }
        public long Sequence { get; set; }
    }

    public sealed class Discovery : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly HashSet<ServerHealthHistoryKey> _keys;
        private readonly CancellationToken _token;
        private IServerHealthSource[] _sources;
        private int _disposed;
        private int _running = 1;
        private int _completed;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool IsRunning => !IsDisposed && Volatile.Read(ref _running) != 0;
        public int TotalSources { get; }
        public int CompletedSources => Volatile.Read(ref _completed);
        internal IServerHealthSource[] Sources => _sources;
        internal CancellationToken Token => _token;
        internal bool Contains(IServerHealthSource source) =>
            _keys.Contains(ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!));
        public Task Completion { get; internal set; } = Task.CompletedTask;
        internal Discovery(IServerHealthSource[] sources, CancellationToken lifetime)
        {
            _sources = sources;
            _keys = sources.Select(source => ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)).ToHashSet();
            TotalSources = sources.Length;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _token = _cancellation.Token;
        }
        internal void BeginPass(int completed) { Volatile.Write(ref _completed, completed); Volatile.Write(ref _running, 1); }
        internal void CompleteSource() => Interlocked.Increment(ref _completed);
        internal void EndPass() => Volatile.Write(ref _running, 0);
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            _cancellation.Cancel();
            _sources = [];
            _ = Completion.ContinueWith(_ => _cancellation.Dispose(), TaskScheduler.Default);
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
