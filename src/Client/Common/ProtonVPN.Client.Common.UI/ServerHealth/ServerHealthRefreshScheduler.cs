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
    private bool _priorityRequestPending;

    public event EventHandler? ProgressChanged;
    public bool IsMeasuring { get { lock (_sync) { return _activeDiscovery?.IsRunning == true; } } }
    public string ProgressText
    {
        get
        {
            lock (_sync)
            {
                return _activeDiscovery?.IsRunning == true
                    ? $"Ping checked {_activeDiscovery.CompletedSources:N0} of {_activeDiscovery.TotalSources:N0}"
                    : string.Empty;
            }
        }
    }

    public ServerHealthRefreshScheduler(ServerHealthHistoryStore store, IServerHealthClock? clock = null, bool startTimer = true)
    {
        _store = store;
        if (startTimer) { _timer = new(_ => RunTick(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)); }
    }

    public IDisposable Track(IServerHealthSource source, bool prioritize = false)
    {
        Candidate candidate;
        lock (_sync)
        {
            if (_disposed || string.IsNullOrWhiteSpace(source.HealthProbeAddress)) { return new Lease(() => { }); }
            ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress);
            if (!_candidates.TryGetValue(key, out candidate!))
            {
                foreach (ServerHealthHistoryKey inactive in _candidates.Where(pair => pair.Value.InterestedViews == 0).Select(pair => pair.Key).ToArray())
                { _candidates.Remove(inactive); }
                // This limit only bounds visible-row refinement, never initial tab coverage.
                if (_candidates.Count >= 64)
                {
                    var oldest = _candidates.OrderBy(pair => pair.Value.PriorityInterests > 0)
                        .ThenBy(pair => pair.Value.Sequence).First();
                    oldest.Value.StopInterestWork();
                    _candidates.Remove(oldest.Key);
                }
                _candidates[key] = candidate = new(source);
            }
            candidate.Source = source;
            candidate.Sequence = ++_sequence;
            candidate.AddInterest(prioritize);
            _priorityRequestPending |= prioritize;
        }
        if (prioritize) { RunTick(); }
        return new Lease(() =>
        {
            lock (_sync)
            {
                candidate.RemoveInterest(prioritize);
            }
        });
    }

    // Supply the full unfiltered catalogue for the active feature tab, ordered by usefulness.
    public Discovery StartDiscovery(
        IEnumerable<IServerHealthSource> sources, IEnumerable<IServerHealthSource>? visiblePriority = null)
    {
        lock (_sync)
        {
            _activeDiscovery?.Dispose();
            IServerHealthSource[] catalogue = _disposed ? [] : sources
                .Where(source => !string.IsNullOrWhiteSpace(source.HealthProbeAddress))
                .DistinctBy(source => ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)).ToArray();
            HashSet<ServerHealthHistoryKey> priority = (visiblePriority ?? [])
                .Where(source => !string.IsNullOrWhiteSpace(source.HealthProbeAddress))
                .Select(source => ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)).ToHashSet();
            Discovery discovery = new(catalogue, priority, _lifetime.Token);
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
            Dictionary<ServerHealthHistoryKey, int> visible;
            lock (_sync)
            {
                visible = _candidates.Where(pair => pair.Value.InterestedViews > 0)
                    .ToDictionary(pair => pair.Key, pair => pair.Value.PriorityInterests > 0 ? 0 : 1);
            }
            var due = discovery.Sources.Where(source => _store.NeedsRefresh(source, RefreshInterval))
                .Select(source => (Source: source, Key: ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)))
                .OrderBy(item => visible.TryGetValue(item.Key, out int priority) ? priority : discovery.Priority.Contains(item.Key) ? 1 : 2)
                .ThenBy(item => _store.GetSnapshot(item.Key).LatestMeasurement is not null)
                .GroupBy(item => item.Key.ProbeAddress)
                .Select(group => (Representative: group.First().Source, Keys: group.Select(item => item.Key).ToArray()))
                .ToArray();
            discovery.BeginPass(discovery.TotalSources - due.Sum(batch => batch.Keys.Length));
            RaiseProgress();
            await Parallel.ForEachAsync(due, new ParallelOptions
            { MaxDegreeOfParallelism = MaximumConcurrentFirstChecks, CancellationToken = token }, async (batch, cancellation) =>
            {
                try
                {
                    _store.RegisterAliases(batch.Keys);
                    if (_store.NeedsRefresh(batch.Representative, RefreshInterval))
                    { await _store.ProbeAsync(batch.Representative, cancellation, quickFirstResponse: true); }
                }
                finally { discovery.CompleteSources(batch.Keys.Length); RaiseProgress(); }
            });
        }
        catch (OperationCanceledException) { }
        catch { } // Optional telemetry must not interrupt the VPN or UI.
        finally
        {
            discovery.EndPass();
            RaiseProgress();
            if (entered) { _pass.Release(); }
            // A row selected during discovery can start refinement as soon as coverage completes.
            bool hasSelectedRow;
            lock (_sync)
            {
                hasSelectedRow = _candidates.Values.Any(candidate => candidate.PriorityInterests > 0);
            }
            if (entered && !token.IsCancellationRequested && hasSelectedRow)
            {
                _ = discovery.Completion.ContinueWith(_ => RunTick(), CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
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
            token = scope?.Token ?? _lifetime.Token;
        }
        if (refresh is not null) { await refresh; return; }
        if (!await _pass.WaitAsync(0, token)) { return; }
        try
        {
            Candidate[] visible;
            lock (_sync)
            {
                if (_disposed || token.IsCancellationRequested)
                {
                    return;
                }
                _priorityRequestPending = false;
                Discovery? scope = _activeDiscovery;
                visible = _candidates.Values.Where(candidate => candidate.InterestedViews > 0
                        && (scope is null || scope.Contains(candidate.Source)))
                    .OrderByDescending(candidate => candidate.PriorityInterests)
                    .ToArray();
            }
            await Parallel.ForEachAsync(visible, new ParallelOptions
            { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (candidate, cancellation) =>
            {
                IServerHealthSource source;
                bool prioritized;
                CancellationTokenSource interest;
                lock (_sync)
                {
                    if (candidate.InterestTokenSource is not { } activeInterest) { return; }
                    source = candidate.Source;
                    prioritized = candidate.PriorityInterests > 0;
                    interest = CancellationTokenSource.CreateLinkedTokenSource(activeInterest.Token, cancellation);
                }
                using (interest)
                {
                    try
                    {
                        if (_store.NeedsRefinement(source, TimeSpan.FromMinutes(1), prioritized))
                        { await _store.ProbeAsync(source, interest.Token); }
                    }
                    catch (OperationCanceledException) when (interest.IsCancellationRequested && !cancellation.IsCancellationRequested)
                    {
                        // The row disappeared while its tab remained active.
                    }
                }
            });
        }
        finally
        {
            _pass.Release();
            bool needsSelectedCheck;
            lock (_sync)
            {
                needsSelectedCheck = !_disposed && _priorityRequestPending;
            }
            if (needsSelectedCheck) { RunTick(); }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _activeDiscovery?.Dispose();
            foreach (Candidate candidate in _candidates.Values) { candidate.StopInterestWork(); }
            _candidates.Clear();
        }
        _timer?.Dispose();
        _lifetime.Cancel();
        ProgressChanged = null;
    }

    private sealed class Candidate(IServerHealthSource source)
    {
        public IServerHealthSource Source { get; set; } = source;
        public int InterestedViews { get; private set; }
        public int PriorityInterests { get; private set; }
        public long Sequence { get; set; }
        public CancellationTokenSource? InterestTokenSource { get; private set; }

        public void AddInterest(bool prioritize)
        {
            if (InterestedViews++ == 0) { InterestTokenSource = new(); }
            if (prioritize) { PriorityInterests++; }
        }

        public void RemoveInterest(bool prioritize)
        {
            if (prioritize) { PriorityInterests--; }
            if (--InterestedViews == 0) { StopInterestWork(); }
        }

        public void StopInterestWork()
        {
            if (InterestTokenSource is { } source)
            {
                InterestTokenSource = null;
                _ = FinishInterestAsync(source);
            }
        }

        private static async Task FinishInterestAsync(CancellationTokenSource source)
        {
            using (source) { await source.CancelAsync(); }
        }
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
        internal HashSet<ServerHealthHistoryKey> Priority { get; }
        internal bool Contains(IServerHealthSource source) =>
            _keys.Contains(ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!));
        public Task Completion { get; internal set; } = Task.CompletedTask;
        internal Discovery(IServerHealthSource[] sources, HashSet<ServerHealthHistoryKey> priority, CancellationToken lifetime)
        {
            _sources = sources;
            Priority = priority;
            _keys = sources.Select(source => ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress!)).ToHashSet();
            TotalSources = sources.Length;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _token = _cancellation.Token;
        }
        internal void BeginPass(int completed) { Volatile.Write(ref _completed, completed); Volatile.Write(ref _running, 1); }
        internal void CompleteSources(int count) => Interlocked.Add(ref _completed, count);
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
