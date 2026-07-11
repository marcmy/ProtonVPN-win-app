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
    private static readonly TimeSpan _retention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(5);

    private readonly IServerHealthClock _clock;
    private readonly SemaphoreSlim _probeSlots;
    private readonly ConcurrentDictionary<ServerHealthHistoryKey, Entry> _entries = new();
    private readonly object _inFlightLock = new();
    private readonly Dictionary<ServerHealthHistoryKey, Task<ServerHealthSnapshot>> _inFlight = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    private bool _isDisposed;
    private int _resourcesDisposed;

    public event EventHandler<ServerHealthSnapshotChangedEventArgs>? SnapshotChanged;

    public ServerHealthHistoryStore(
        IServerHealthClock? clock = null,
        int maximumConcurrentProbes = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentProbes, 1);
        _clock = clock ?? new SystemServerHealthClock();
        _probeSlots = new(maximumConcurrentProbes, maximumConcurrentProbes);
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
                _clock.UtcNow - entry.LastRecordedAt > _retention)
            {
                _entries.TryRemove(key, out _);
                return ServerHealthSnapshot.Empty(key);
            }

            return CreateSnapshot(key, entry);
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
                TaskCompletionSource<ServerHealthSnapshot> completion =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending = completion.Task;
                _inFlight.Add(key, pending);
                _ = RunProbeAndReleaseAsync(key, source, completion);
            }
        }

        return await pending.WaitAsync(cancellationToken);
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
        SetTransientState(key, entry, checking: true, rechecking: false, error: null);
        await _probeSlots.WaitAsync(cancellationToken);
        try
        {
            ServerHealthProbeMeasurement first = await InvokeProbeAsync(source, cancellationToken);
            if (!first.IsCompleteFailure)
            {
                return Record(key, entry, first with { ServerLoad = source.HealthServerLoad });
            }

            SetTransientState(key, entry, checking: false, rechecking: true, first.Error);
            await _clock.DelayAsync(_retryDelay, cancellationToken);
            ServerHealthProbeMeasurement retry = await InvokeProbeAsync(source, cancellationToken);
            if (!retry.IsCompleteFailure)
            {
                return Record(key, entry, retry with
                {
                    ServerLoad = source.HealthServerLoad,
                    WasRetried = true,
                });
            }

            return Record(key, entry, new ServerHealthProbeMeasurement(
                null,
                0,
                4,
                _clock.UtcNow,
                first.UsedPhysicalRoute || retry.UsedPhysicalRoute,
                retry.Error ?? first.Error,
                source.HealthServerLoad,
                WasRetried: true,
                IsConfirmedOutage: true));
        }
        catch (OperationCanceledException)
        {
            SetTransientState(key, entry, checking: false, rechecking: false, error: null);
            throw;
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
        ServerHealthProbeMeasurement measurement)
    {
        ServerHealthSnapshot snapshot;
        lock (entry.SyncRoot)
        {
            entry.Measurements.Add(measurement);
            entry.LastRecordedAt = measurement.CheckedAt;
            entry.IsChecking = false;
            entry.IsRechecking = false;
            entry.PendingError = null;
            Prune(entry);
            snapshot = CreateSnapshot(key, entry);
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

    private static ServerHealthSnapshot CreateSnapshot(
        ServerHealthHistoryKey key,
        Entry entry)
    {
        ServerHealthProbeMeasurement[] measurements = entry.Measurements
            .OrderBy(measurement => measurement.CheckedAt)
            .ToArray();
        ServerHealthAggregate? aggregate = measurements.Length == 0
            ? null
            : ServerHealthCalculator.Aggregate(measurements);
        return new(
            key,
            measurements,
            aggregate,
            measurements.Length == 0 ? null : measurements[^1],
            entry.IsChecking,
            entry.IsRechecking,
            entry.PendingError);
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
        _entries.Clear();
        _lifetimeCancellation.Cancel();

        if (pending.Length == 0)
        {
            DisposeResources();
            return;
        }

        _ = Task.WhenAll(pending).ContinueWith(
            _ => DisposeResources(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

        public Entry(DateTimeOffset createdAt)
        {
            LastRecordedAt = createdAt;
        }
    }
}
