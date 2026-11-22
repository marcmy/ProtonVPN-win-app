using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed class ServerHealthSnapshotChangedEventArgs(ServerHealthSnapshot snapshot) : EventArgs
{
    public ServerHealthSnapshot Snapshot { get; } = snapshot;
}

public sealed class ServerHealthHistoryStore : IDisposable
{
    private static readonly TimeSpan _retention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _failureCooldown = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _staleAge = TimeSpan.FromMinutes(5);

    private readonly IServerHealthClock _clock;
    private readonly SemaphoreSlim _probeSlots;
    private readonly TimeSpan _minimumProbeInterval;
    private readonly int _maximumCachedEntries;
    private readonly ConcurrentDictionary<string, EndpointHistory> _endpoints = new();
    private readonly object _jobsLock = new();
    private readonly Dictionary<ProbeKey, ProbeJob> _inFlight = new();
    private readonly HashSet<ProbeJob> _activeJobs = [];
    private readonly string? _cachePath;
    private readonly Timer? _saveTimer;
    private readonly object _saveLock = new();
    private long _networkGeneration;
    private volatile bool _isDisposed;

    public event EventHandler<ServerHealthSnapshotChangedEventArgs>? SnapshotChanged;

    public ServerHealthHistoryStore(
        IServerHealthClock? clock = null,
        int maximumConcurrentProbes = 8,
        TimeSpan? minimumProbeInterval = null,
        string? cachePath = null,
        int maximumCachedEntries = ServerHealthDiskCache.MaximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentProbes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCachedEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCachedEntries, ServerHealthDiskCache.MaximumEntries);
        _clock = clock ?? new SystemServerHealthClock();
        _probeSlots = new(maximumConcurrentProbes, maximumConcurrentProbes);
        _minimumProbeInterval = minimumProbeInterval ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThan(_minimumProbeInterval, TimeSpan.Zero);
        _maximumCachedEntries = maximumCachedEntries;
        _cachePath = cachePath;
        if (cachePath is not null)
        {
            foreach (SavedServerHealth item in ServerHealthDiskCache.Load(cachePath, _clock.UtcNow)
                         .Where(item => !item.Measurement.IsCompleteFailure && item.Measurement.UsedPhysicalRoute)
                         .DistinctBy(item => ServerHealthHistoryKey.Create(item.Key.ServerId, item.Key.ProbeAddress).ProbeAddress)
                         .Take(_maximumCachedEntries))
            {
                ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(item.Key.ServerId, item.Key.ProbeAddress);
                _endpoints[key.ProbeAddress] = new(key, _networkGeneration)
                {
                    SavedLatency = ToSavedLatency(item.Measurement),
                };
            }
            _saveTimer = new(_ => SaveCache(), null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    public ServerHealthSnapshot GetSnapshot(ServerHealthHistoryKey key)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_endpoints.TryGetValue(key.ProbeAddress, out EndpointHistory? history))
        {
            return ServerHealthSnapshot.Empty(key);
        }
        lock (history)
        {
            ResetForGeneration(history, Interlocked.Read(ref _networkGeneration));
            history.Aliases.Add(key);
            return CreateSnapshot(key, history);
        }
    }

    public bool NeedsRefresh(IServerHealthSource source, TimeSpan interval)
    {
        if (!TryGetKey(source, out ServerHealthHistoryKey key))
        {
            return false;
        }
        ServerHealthSnapshot snapshot = GetSnapshot(key);
        ServerHealthProbeMeasurement? latest = snapshot.IsCached ? null : snapshot.LatestMeasurement;
        TimeSpan effectiveInterval = latest?.IsCompleteFailure == true ? _failureCooldown : interval;
        return latest is null || _clock.UtcNow - latest.CheckedAt >= effectiveInterval;
    }

    internal void RegisterAliases(IReadOnlyList<ServerHealthHistoryKey> keys)
    {
        if (keys.Count < 2 || _isDisposed)
        {
            return;
        }
        ServerHealthHistoryKey primary = keys[0];
        long generation = Interlocked.Read(ref _networkGeneration);
        EndpointHistory history = _endpoints.GetOrAdd(primary.ProbeAddress, _ => new(primary, generation));
        bool publish;
        lock (history)
        {
            ResetForGeneration(history, generation);
            int previousCount = history.Aliases.Count;
            history.Aliases.UnionWith(keys);
            publish = history.Aliases.Count != previousCount && history.Measurements.Count > 0;
        }
        if (publish)
        {
            Publish(history);
        }
    }

    internal bool NeedsRefinement(IServerHealthSource source, TimeSpan interval, bool prioritized)
    {
        if (!TryGetKey(source, out ServerHealthHistoryKey key))
        {
            return false;
        }
        ServerHealthSnapshot snapshot = GetSnapshot(key);
        ServerHealthProbeMeasurement? latest = snapshot.Measurements.LastOrDefault(check => !check.IsDiscovery);
        if (!prioritized && snapshot.Measurements.LastOrDefault() is { IsCompleteFailure: true } failed)
        {
            return _clock.UtcNow - failed.CheckedAt >= _failureCooldown;
        }
        TimeSpan effectiveInterval = latest?.IsCompleteFailure == true && !prioritized ? _failureCooldown : interval;
        return latest is null || _clock.UtcNow - latest.CheckedAt >= effectiveInterval;
    }

    public void InvalidateFreshness()
    {
        long generation = Interlocked.Increment(ref _networkGeneration);
        lock (_jobsLock)
        {
            foreach (ProbeJob job in _activeJobs)
            {
                if (job.Generation < generation)
                {
                    job.Cancel();
                }
            }
        }
        foreach (EndpointHistory history in _endpoints.Values)
        {
            lock (history)
            {
                ResetForGeneration(history, generation);
            }
            Publish(history);
        }
    }

    public async Task<ServerHealthSnapshot> ProbeAsync(
        IServerHealthSource source,
        CancellationToken cancellationToken,
        bool quickFirstResponse = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetKey(source, out ServerHealthHistoryKey key))
        {
            throw new ArgumentException("A health probe address is required.", nameof(source));
        }

        ProbeJob job;
        EndpointHistory history;
        ProbeKey probeKey;
        bool start = false;
        lock (_jobsLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            long generation = Interlocked.Read(ref _networkGeneration);
            probeKey = new(key.ProbeAddress, quickFirstResponse, generation);
            history = _endpoints.GetOrAdd(key.ProbeAddress, _ => new(key, generation));
            lock (history)
            {
                ResetForGeneration(history, generation);
                history.Aliases.Add(key);
                ServerHealthProbeMeasurement? latest = history.Measurements.LastOrDefault(check =>
                    quickFirstResponse || !check.IsDiscovery);
                TimeSpan interval = !quickFirstResponse && _minimumProbeInterval > TimeSpan.Zero &&
                    _minimumProbeInterval < ServerHealthCalculator.MinimumFixedCheckSpacing
                    ? ServerHealthCalculator.MinimumFixedCheckSpacing
                    : _minimumProbeInterval;
                if (latest is not null && _clock.UtcNow - latest.CheckedAt < interval)
                {
                    return CreateSnapshot(key, history);
                }
            }
            if (!_inFlight.TryGetValue(probeKey, out job!) || job.Cancellation.IsCancellationRequested)
            {
                job = new(generation);
                _inFlight[probeKey] = job;
                _activeJobs.Add(job);
                start = true;
            }
            job.Subscribers++;
        }

        if (start)
        {
            _ = RunAsync(probeKey, job, history, source);
        }

        try
        {
            ServerHealthSnapshot snapshot = await job.Completion.Task.WaitAsync(cancellationToken);
            return snapshot.Key == key ? snapshot : snapshot with { Key = key };
        }
        finally
        {
            lock (_jobsLock)
            {
                if (--job.Subscribers == 0 && _activeJobs.Contains(job))
                {
                    job.Cancel();
                }
            }
        }
    }

    private async Task RunAsync(
        ProbeKey probeKey, ProbeJob job, EndpointHistory history, IServerHealthSource source)
    {
        ServerHealthSnapshot? result = null;
        Exception? error = null;
        CancellationToken token = job.Cancellation.Token;
        bool isChecking = false;
        long versionAtStart = 0;
        try
        {
            await _probeSlots.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                lock (history)
                {
                    if (history.Generation != job.Generation)
                    {
                        throw new OperationCanceledException(token);
                    }
                    history.ActiveChecks++;
                    versionAtStart = history.RecordVersion;
                    isChecking = true;
                }
                Publish(history);
                ServerHealthProbeMeasurement measurement;
                try
                {
                    measurement = await source.ProbeHealthAsync(token, probeKey.IsDiscovery);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    measurement = new(null, 0, 0, _clock.UtcNow, false, exception.Message, source.HealthServerLoad);
                }
                token.ThrowIfCancellationRequested();
                lock (history)
                {
                    if (!_isDisposed && !token.IsCancellationRequested &&
                        job.Generation == Interlocked.Read(ref _networkGeneration))
                    {
                        Record(history, measurement with
                        {
                            IsDiscovery = probeKey.IsDiscovery,
                            ServerLoad = source.HealthServerLoad,
                        }, versionAtStart);
                    }
                }
            }
            finally
            {
                _probeSlots.Release();
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            lock (history)
            {
                ResetForGeneration(history, Interlocked.Read(ref _networkGeneration));
                if (isChecking && history.Generation == job.Generation)
                {
                    history.ActiveChecks--;
                }
                result = CreateSnapshot(history.Key, history);
            }
            lock (_jobsLock)
            {
                // A new subscriber can replace an abandoned job while its native send unwinds.
                if (_inFlight.TryGetValue(probeKey, out ProbeJob? current) && ReferenceEquals(current, job))
                {
                    _inFlight.Remove(probeKey);
                }
                _activeJobs.Remove(job);
            }
            try { await job.CancellationCompletion; }
            catch (Exception exception) { error ??= exception; }
            finally { job.Cancellation.Dispose(); }
        }

        try
        {
            if (isChecking)
            {
                Publish(history);
                ScheduleSave();
            }
        }
        finally
        {
            if (error is null || job.Generation != Interlocked.Read(ref _networkGeneration))
            {
                job.Completion.TrySetResult(result);
            }
            else if (error is OperationCanceledException canceled)
            {
                job.Completion.TrySetCanceled(canceled.CancellationToken);
            }
            else
            {
                job.Completion.TrySetException(error);
            }
        }
    }

    private static bool TryGetKey(IServerHealthSource source, out ServerHealthHistoryKey key)
    {
        key = default;
        string? address = source.HealthProbeAddress;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }
        key = ServerHealthHistoryKey.Create(source.HealthServerId, address);
        return true;
    }

    private static void ResetForGeneration(EndpointHistory history, long generation)
    {
        if (history.Generation >= generation)
        {
            return;
        }
        history.Measurements.Clear();
        history.Generation = generation;
        history.ActiveChecks = 0;
        history.RouteContext = null;
        history.HasObservedReply = false;
    }

    private static void Record(EndpointHistory history, ServerHealthProbeMeasurement measurement, long versionAtStart)
    {
        bool arrivedLate = versionAtStart != history.RecordVersion &&
            history.Measurements.LastOrDefault()?.CheckedAt > measurement.CheckedAt;
        if (arrivedLate && !string.Equals(history.RouteContext, measurement.RouteContext, StringComparison.Ordinal))
        {
            return;
        }
        if (measurement.RouteContext is string route && history.RouteContext != route)
        {
            if (history.RouteContext is not null)
            {
                history.Measurements.Clear();
                history.HasObservedReply = false;
            }
            history.RouteContext = route;
        }
        int insertionIndex = arrivedLate
            ? history.Measurements.FindLastIndex(check => check.CheckedAt <= measurement.CheckedAt) + 1
            : history.Measurements.Count;
        history.Measurements.Insert(insertionIndex, measurement);
        history.HasObservedReply |= measurement.HasVerifiedEchoReply;
        history.RecordVersion++;
        if (measurement.UsedPhysicalRoute && measurement.AverageLatencyMilliseconds is not null &&
            (!arrivedLate || history.SavedLatency is null))
        {
            history.SavedLatency = ToSavedLatency(measurement);
        }
    }

    private ServerHealthSnapshot CreateSnapshot(ServerHealthHistoryKey key, EndpointHistory history)
    {
        DateTimeOffset now = _clock.UtcNow;
        history.Measurements.RemoveAll(check => check.CheckedAt < now - _retention);
        if (history.SavedLatency?.CheckedAt < now - ServerHealthDiskCache.Retention)
        {
            history.SavedLatency = null;
        }
        ServerHealthProbeMeasurement[] measurements = history.Measurements.ToArray();
        ServerHealthProbeMeasurement? latest = measurements.LastOrDefault() ?? history.SavedLatency;
        bool saved = measurements.Length == 0 && latest is not null;
        ServerHealthAggregate? aggregate = measurements.Length > 0 ? ServerHealthCalculator.Aggregate(measurements, history.HasObservedReply)
            : latest is null ? null : ServerHealthCalculator.Aggregate([latest]);
        return new(key, measurements, aggregate, latest, history.ActiveChecks > 0, false, null,
            IsCached: saved,
            IsStale: saved || (latest is not null && now - latest.CheckedAt >= _staleAge),
            SavedLatencyMilliseconds: history.SavedLatency?.AverageLatencyMilliseconds,
            FreshUntil: saved ? null : latest?.CheckedAt + _staleAge);
    }

    private void Publish(EndpointHistory history)
    {
        EventHandler<ServerHealthSnapshotChangedEventArgs>? handler = SnapshotChanged;
        if (_isDisposed || handler is null)
        {
            return;
        }
        ServerHealthHistoryKey[] aliases;
        ServerHealthSnapshot snapshot;
        lock (history)
        {
            ResetForGeneration(history, Interlocked.Read(ref _networkGeneration));
            // Read the newest snapshot at publication time if another mode completed meanwhile.
            snapshot = CreateSnapshot(history.Key, history);
            aliases = history.Aliases.ToArray();
        }
        foreach (ServerHealthHistoryKey alias in aliases)
        {
            handler(this, new(snapshot.Key == alias ? snapshot : snapshot with { Key = alias }));
        }
    }

    private static ServerHealthProbeMeasurement ToSavedLatency(ServerHealthProbeMeasurement measurement) =>
        measurement with { Observations = null, RouteContext = null, IsConfirmedOutage = false };

    private void ScheduleSave()
    {
        try { _saveTimer?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
        if (_endpoints.Count <= _maximumCachedEntries)
        {
            return;
        }
        foreach ((string address, EndpointHistory history) in _endpoints
                     .OrderBy(pair => pair.Value.SavedLatency?.CheckedAt).Take(_endpoints.Count - _maximumCachedEntries))
        {
            lock (_jobsLock)
            {
                if (!_inFlight.Keys.Any(job => job.Address == address))
                {
                    _endpoints.TryRemove(new KeyValuePair<string, EndpointHistory>(address, history));
                }
            }
        }
    }

    private void SaveCache(bool final = false)
    {
        if (_cachePath is null)
        {
            return;
        }
        lock (_saveLock)
        {
            if (_isDisposed && !final)
            {
                return;
            }
            List<SavedServerHealth> saved = [];
            foreach (EndpointHistory history in _endpoints.Values)
            {
                lock (history)
                {
                    if (history.SavedLatency is { } measurement && _clock.UtcNow - measurement.CheckedAt <= ServerHealthDiskCache.Retention)
                    {
                        saved.Add(new(history.Key, measurement));
                    }
                }
            }
            ServerHealthDiskCache.Save(_cachePath, saved);
        }
    }

    public void Dispose()
    {
        Task[] pending;
        lock (_jobsLock)
        {
            if (_isDisposed)
            {
                return;
            }
            _isDisposed = true;
            SnapshotChanged = null;
            pending = _activeJobs.Select(job => (Task)job.Completion.Task).ToArray();
            foreach (ProbeJob job in _activeJobs)
            {
                job.Cancel();
            }
        }
        _saveTimer?.Dispose();
        SaveCache(final: true);
        _ = DisposeSlotsAsync(pending);
    }

    private async Task DisposeSlotsAsync(Task[] pending)
    {
        try { await Task.WhenAll(pending); }
        catch (Exception) { }
        finally { _probeSlots.Dispose(); }
    }

    private readonly record struct ProbeKey(string Address, bool IsDiscovery, long Generation);

    private sealed class ProbeJob(long generation)
    {
        public long Generation { get; } = generation;
        public TaskCompletionSource<ServerHealthSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public Task CancellationCompletion { get; private set; } = Task.CompletedTask;
        public int Subscribers { get; set; }

        public void Cancel()
        {
            if (!Cancellation.IsCancellationRequested)
            {
                CancellationCompletion = Cancellation.CancelAsync();
            }
        }
    }

    private sealed class EndpointHistory(ServerHealthHistoryKey key, long generation)
    {
        public ServerHealthHistoryKey Key { get; } = key;
        public HashSet<ServerHealthHistoryKey> Aliases { get; } = [key];
        public List<ServerHealthProbeMeasurement> Measurements { get; } = [];
        public long Generation { get; set; } = generation;
        public long RecordVersion { get; set; }
        public string? RouteContext { get; set; }
        public bool HasObservedReply { get; set; }
        public ServerHealthProbeMeasurement? SavedLatency { get; set; }
        public int ActiveChecks { get; set; }
    }
}
