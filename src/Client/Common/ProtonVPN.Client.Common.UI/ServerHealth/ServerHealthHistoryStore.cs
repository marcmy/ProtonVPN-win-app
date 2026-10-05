using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Client.Common.UI.ServerHealth;

public sealed class ServerHealthSnapshotChangedEventArgs : EventArgs
{
    public ServerHealthSnapshot Snapshot { get; }

    public ServerHealthSnapshotChangedEventArgs(ServerHealthSnapshot snapshot)
    {
        Snapshot = snapshot;
    }
}

public sealed class ServerHealthHistoryStore : IDisposable
{
    private const string NO_REPLY_ERROR = "No ICMP replies were received.";

    private static readonly TimeSpan _retention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _defaultMinimumProbeInterval = TimeSpan.FromSeconds(30);

    private readonly IServerHealthClock _clock;
    private readonly SemaphoreSlim _probeSlots;
    private readonly TimeSpan _minimumProbeInterval;
    private readonly ConcurrentDictionary<ServerHealthHistoryKey, Entry> _entries = new();
    private readonly object _inFlightLock = new();
    private readonly Dictionary<ServerHealthHistoryKey, Task<ServerHealthSnapshot>> _inFlight = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly string? _cachePath;
    private readonly Timer? _saveTimer;
    private readonly object _saveLock = new();
    private long _networkGeneration;

    private volatile bool _isDisposed;
    private int _resourcesDisposed;

    public event EventHandler<ServerHealthSnapshotChangedEventArgs>? SnapshotChanged;

    public ServerHealthHistoryStore(
        IServerHealthClock? clock = null,
        int maximumConcurrentProbes = 8,
        TimeSpan? minimumProbeInterval = null,
        string? cachePath = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentProbes, 1);
        _clock = clock ?? new SystemServerHealthClock();
        _probeSlots = new(maximumConcurrentProbes, maximumConcurrentProbes);
        _minimumProbeInterval = minimumProbeInterval ?? _defaultMinimumProbeInterval;
        if (_minimumProbeInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumProbeInterval));
        }
        _cachePath = cachePath;
        if (cachePath is not null)
        {
            foreach (SavedServerHealth saved in ServerHealthDiskCache.Load(cachePath, _clock.UtcNow))
            {
                _entries[ServerHealthHistoryKey.Create(saved.Key.ServerId, saved.Key.ProbeAddress)] = new(saved.Measurement.CheckedAt)
                { SavedMeasurement = saved.Measurement, RequiresRefresh = true };
            }
            _saveTimer = new(_ => SaveCache(), null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    public ServerHealthSnapshot GetSnapshot(ServerHealthHistoryKey key)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return ServerHealthSnapshot.Empty(key);
        }

        lock (entry.SyncRoot)
        {
            Prune(entry);
            if (entry.Measurements.Count == 0 &&
                !entry.IsChecking &&
                !entry.IsRechecking &&
                _clock.UtcNow - entry.LastRecordedAt > ServerHealthDiskCache.Retention)
            {
                _entries.TryRemove(key, out _);
                return ServerHealthSnapshot.Empty(key);
            }

            return CreateSnapshot(key, entry);
        }
    }

    public bool NeedsRefresh(IServerHealthSource source, TimeSpan interval)
    {
        if (string.IsNullOrWhiteSpace(source.HealthProbeAddress)) { return false; }
        ServerHealthSnapshot snapshot = GetSnapshot(ServerHealthHistoryKey.Create(source.HealthServerId, source.HealthProbeAddress));
        TimeSpan effectiveInterval = snapshot.LatestMeasurement?.IsCompleteFailure == true ? TimeSpan.FromMinutes(10) : interval;
        bool invalidated = false;
        if (_entries.TryGetValue(snapshot.Key, out Entry? entry)) { lock (entry.SyncRoot) { invalidated = entry.RequiresRefresh; } }
        return invalidated || snapshot.LatestMeasurement is null || _clock.UtcNow - snapshot.LatestMeasurement.CheckedAt >= effectiveInterval;
    }

    public void InvalidateFreshness()
    {
        Interlocked.Increment(ref _networkGeneration);
        foreach (Entry entry in _entries.Values) { lock (entry.SyncRoot) { entry.RequiresRefresh = true; } }
    }

    private void SaveCache(bool final = false)
    {
        if (_cachePath is null) { return; }
        lock (_saveLock)
        {
            if (_isDisposed && !final) { return; }
            List<SavedServerHealth> saved = [];
            foreach ((ServerHealthHistoryKey key, Entry entry) in _entries)
            {
                lock (entry.SyncRoot)
                {
                    if (entry.SavedMeasurement is not null && _clock.UtcNow - entry.SavedMeasurement.CheckedAt <= ServerHealthDiskCache.Retention)
                    { saved.Add(new(key, entry.SavedMeasurement)); }
                }
            }
            ServerHealthDiskCache.Save(_cachePath, saved);
        }
    }

    public async Task<ServerHealthSnapshot> ProbeAsync(
        IServerHealthSource source,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(source);
        string? probeAddress = source.HealthProbeAddress;
        if (string.IsNullOrWhiteSpace(probeAddress))
        {
            throw new ArgumentException("A health probe address is required.", nameof(source));
        }

        ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(
            source.HealthServerId,
            probeAddress);
        Task<ServerHealthSnapshot> pending;
        lock (_inFlightLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (!_inFlight.TryGetValue(key, out pending!))
            {
                if (TryGetRecentSnapshot(key, out ServerHealthSnapshot recentSnapshot))
                {
                    return recentSnapshot;
                }

                TaskCompletionSource<ServerHealthSnapshot> completion =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = completion.Task;
                _inFlight.Add(key, pending);
                _ = RunProbeAndReleaseAsync(key, source, completion);
            }
        }

        return await pending.WaitAsync(cancellationToken);
    }

    private bool TryGetRecentSnapshot(
        ServerHealthHistoryKey key,
        out ServerHealthSnapshot snapshot)
    {
        snapshot = null!;
        if (!_entries.TryGetValue(key, out Entry? entry))
        {
            return false;
        }

        lock (entry.SyncRoot)
        {
            Prune(entry);
            if (entry.RequiresRefresh || entry.SavedMeasurement is null ||
                _clock.UtcNow - entry.LastRecordedAt >= _minimumProbeInterval)
            {
                return false;
            }

            snapshot = CreateSnapshot(key, entry);
            return true;
        }
    }

    private async Task RunProbeAndReleaseAsync(
        ServerHealthHistoryKey key,
        IServerHealthSource source,
        TaskCompletionSource<ServerHealthSnapshot> completion)
    {
        try
        {
            completion.TrySetResult(
                await ProbeCoreAsync(key, source, _lifetimeCancellation.Token));
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
        finally
        {
            lock (_inFlightLock)
            {
                _inFlight.Remove(key);
            }
        }
    }

    private async Task<ServerHealthSnapshot> ProbeCoreAsync(
        ServerHealthHistoryKey key,
        IServerHealthSource source,
        CancellationToken cancellationToken)
    {
        Entry entry = _entries.GetOrAdd(key, _ => new Entry(_clock.UtcNow));
        long networkGeneration = Interlocked.Read(ref _networkGeneration);
        SetTransientState(key, entry, checking: true, rechecking: false, error: null);
        try
        {
            ServerHealthProbeMeasurement first = await ProbeOnceAsync(source, cancellationToken);
            if (!first.IsCompleteFailure)
            {
                return Record(key, entry, first with { ServerLoad = source.HealthServerLoad }, networkGeneration);
            }

            SetTransientState(
                key,
                entry,
                checking: false,
                rechecking: true,
                first.Error ?? NO_REPLY_ERROR);
            await _clock.DelayAsync(_retryDelay, cancellationToken);
            ServerHealthProbeMeasurement retry = await ProbeOnceAsync(source, cancellationToken);
            if (!retry.IsCompleteFailure)
            {
                return Record(key, entry, retry with
                {
                    ServerLoad = source.HealthServerLoad,
                    WasRetried = true,
                }, networkGeneration);
            }

            return Record(key, entry, new ServerHealthProbeMeasurement(
                null,
                0,
                4,
                _clock.UtcNow,
                first.UsedPhysicalRoute || retry.UsedPhysicalRoute,
                retry.Error ?? first.Error ?? NO_REPLY_ERROR,
                source.HealthServerLoad,
                WasRetried: true,
                IsConfirmedOutage: true), networkGeneration);
        }
        catch (OperationCanceledException)
        {
            SetTransientState(key, entry, checking: false, rechecking: false, error: null);
            throw;
        }
    }

    private async Task<ServerHealthProbeMeasurement> ProbeOnceAsync(
        IServerHealthSource source,
        CancellationToken cancellationToken)
    {
        await _probeSlots.WaitAsync(cancellationToken);
        try
        {
            ServerHealthProbeMeasurement result = await InvokeProbeAsync(source, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _probeSlots.Release();
        }
    }

    private async Task<ServerHealthProbeMeasurement> InvokeProbeAsync(
        IServerHealthSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await source.ProbeHealthAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(
                null,
                0,
                4,
                _clock.UtcNow,
                false,
                exception.Message,
                source.HealthServerLoad);
        }
    }

    private ServerHealthSnapshot Record(
        ServerHealthHistoryKey key,
        Entry entry,
        ServerHealthProbeMeasurement measurement,
        long networkGeneration)
    {
        ServerHealthSnapshot snapshot;
        lock (entry.SyncRoot)
        {
            entry.Measurements.Add(measurement);
            entry.LastRecordedAt = measurement.CheckedAt;
            entry.SavedMeasurement = measurement;
            entry.RequiresRefresh = networkGeneration != Interlocked.Read(ref _networkGeneration);
            entry.IsChecking = false;
            entry.IsRechecking = false;
            entry.PendingError = null;
            Prune(entry);
            snapshot = CreateSnapshot(key, entry);
        }

        try { _saveTimer?.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
        if (_entries.Count > ServerHealthDiskCache.MaximumEntries)
        {
            foreach (ServerHealthHistoryKey old in _entries.Where(pair => !pair.Value.IsChecking && !pair.Value.IsRechecking)
                         .OrderBy(pair => pair.Value.LastRecordedAt).Take(_entries.Count - ServerHealthDiskCache.MaximumEntries).Select(pair => pair.Key))
            { _entries.TryRemove(old, out _); }
        }

        RaiseSnapshotChanged(snapshot);
        return snapshot;
    }

    private void SetTransientState(
        ServerHealthHistoryKey key,
        Entry entry,
        bool checking,
        bool rechecking,
        string? error)
    {
        ServerHealthSnapshot snapshot;
        lock (entry.SyncRoot)
        {
            Prune(entry);
            entry.IsChecking = checking;
            entry.IsRechecking = rechecking;
            entry.PendingError = error;
            snapshot = CreateSnapshot(key, entry);
        }

        RaiseSnapshotChanged(snapshot);
    }

    private void Prune(Entry entry)
    {
        DateTimeOffset cutoff = _clock.UtcNow - _retention;
        entry.Measurements.RemoveAll(measurement => measurement.CheckedAt < cutoff);
    }

    private ServerHealthSnapshot CreateSnapshot(
        ServerHealthHistoryKey key,
        Entry entry)
    {
        ServerHealthProbeMeasurement[] measurements = entry.Measurements.ToArray();
        ServerHealthProbeMeasurement? latest = measurements.Length == 0 ? entry.SavedMeasurement : measurements[^1];
        ServerHealthAggregate? aggregate = measurements.Length > 0 ? ServerHealthCalculator.Aggregate(measurements)
            : latest is null ? null : ServerHealthCalculator.Aggregate([latest]);
        return new(
            key,
            measurements,
            aggregate,
            latest,
            entry.IsChecking,
            entry.IsRechecking,
            entry.PendingError,
            IsCached: measurements.Length == 0 && latest is not null,
            IsStale: entry.RequiresRefresh || (latest is not null && _clock.UtcNow - latest.CheckedAt >= TimeSpan.FromMinutes(5)));
    }

    private void RaiseSnapshotChanged(ServerHealthSnapshot snapshot)
    {
        if (!_isDisposed)
        {
            SnapshotChanged?.Invoke(this, new(snapshot));
        }
    }

    public void Dispose()
    {
        Task[] pending;
        lock (_inFlightLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            pending = _inFlight.Values.Cast<Task>().ToArray();
        }

        SnapshotChanged = null;
        _lifetimeCancellation.Cancel();
        _saveTimer?.Dispose();
        SaveCache(final: true);
        _entries.Clear();

        if (pending.Length == 0)
        {
            DisposeResources();
            return;
        }

        _ = DisposeResourcesWhenCompleteAsync(pending);
    }

    private async Task DisposeResourcesWhenCompleteAsync(Task[] pending)
    {
        try
        {
            await Task.WhenAll(pending);
        }
        catch
        {
            // Completion is enough; cancellation or faults are already observed by callers.
        }
        finally
        {
            DisposeResources();
        }
    }

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Dispose();
        _probeSlots.Dispose();
    }

    private sealed class Entry
    {
        public object SyncRoot { get; } = new();
        public List<ServerHealthProbeMeasurement> Measurements { get; } = [];
        public DateTimeOffset LastRecordedAt { get; set; }
        public bool IsChecking { get; set; }
        public bool IsRechecking { get; set; }
        public string? PendingError { get; set; }
        public ServerHealthProbeMeasurement? SavedMeasurement { get; set; }
        public bool RequiresRefresh { get; set; }

        public Entry(DateTimeOffset createdAt)
        {
            LastRecordedAt = createdAt;
        }
    }
}
