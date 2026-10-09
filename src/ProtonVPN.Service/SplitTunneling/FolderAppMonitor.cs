/*
 * Copyright (c) 2026 Proton AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.Common.Core.Helpers;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;

namespace ProtonVPN.Service.SplitTunneling;

/// <summary>Service-owned reconciliation: notifications are hints, never incremental truth.</summary>
public sealed class FolderAppMonitor : IFolderAppMonitor, IDisposable
{
    // A cheap health check, not a full-tree scan for healthy watched rules.
    internal static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PreparedLifetime = TimeSpan.FromMinutes(10);
    private const string CombinedLimitError = "Combined folder executable limit exceeded; no folder app rules applied.";
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Action<FolderScanProgress>, Task<FolderScanResult>> _scan;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _scanSync = new(1);
    private CancellationTokenSource _scanCancellation = new();
    private FolderScanStatusIpcEntity _status = new();
    private readonly FolderScanStatusIpcEntity _preparationStatus = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Dictionary<string, (FolderScanResult Scan, DateTimeOffset Expires)> _prepared = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, FolderScanResult> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _ruleAnchors = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _appAncestors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private readonly Timer _periodic;
    private string[] _folders = [];
    private string[] _scanningFolders = [];
    private string[] _appPaths = [];
    private string _lastErrors = string.Empty;
    private int _generation;
    private long _scanRevision;
    private bool _started;
    private bool _disposed;
    private bool _queued;
    private bool _dirty;
    private bool _watcherLimitWarned;

    public event EventHandler? PathsChanged;
    public string[] AppPaths { get { lock (_sync) { return (string[])_appPaths.Clone(); } } }
    public FolderScanStatusIpcEntity Status
    {
        get
        {
            lock (_sync)
            {
                return new() { IsActive = _started, IsScanning = _status.IsScanning, Entries = _status.Entries,
                    Executables = _status.Executables, Error = _status.Error, RulePaths = (string[])_folders.Clone(),
                    IsPreparing = _preparationStatus.IsPreparing, PreparationEntries = _preparationStatus.Entries,
                    PreparationExecutables = _preparationStatus.Executables };
            }
        }
    }

    public FolderAppMonitor(ILogger logger)
        : this(logger, (folder, token, progress) => SplitTunnelFolderScanner.ScanInBackgroundAsync(folder, token, progress)) { }

    internal FolderAppMonitor(ILogger logger, Func<string, CancellationToken, Action<FolderScanProgress>, Task<FolderScanResult>> scan)
    {
        _logger = logger;
        _scan = scan;
        _debounce = new(_ => RunQueuedReconcile(), null, Timeout.Infinite, Timeout.Infinite);
        _periodic = new(_ => QueueRecovery(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void ReplaceRules(string[] folders)
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            string[] interrupted = _pendingFolders.Concat(_scanningFolders).ToArray();
            _generation++;
            _scanCancellation.Cancel();
            _scanCancellation.Dispose();
            _scanCancellation = new();
            _queued = false;
            _dirty = false;
            _pendingFolders.Clear();
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            // Saved folder count is unrestricted; executable and traversal budgets remain separate.
            string[] replacement = (folders ?? []).Select(SplitTunnelAppPathResolver.Normalize)
                .Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            RemoveExpiredPreparations();
            _snapshots = _snapshots.Where(pair => replacement.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (string folder in replacement)
            {
                if (_prepared.Remove(folder, out var prepared) && !_snapshots.ContainsKey(folder))
                { _snapshots[folder] = prepared.Scan; }
            }
            _folders = replacement;
            _scanningFolders = [];
            _ruleAnchors = _folders.ToDictionary(folder => folder, SplitTunnelFolderScanner.GetWatchRoot, StringComparer.OrdinalIgnoreCase);
            (string[] paths, string error) = GetSnapshotPaths(_snapshots);
            SetEffectivePaths(paths);
            _status = new() { IsScanning = _folders.Any(folder => !_snapshots.ContainsKey(folder)),
                Executables = paths.Length, Error = error };
            RefreshWatchers();
            QueueReconcile(_folders.Where(folder => !_snapshots.ContainsKey(folder)
                || interrupted.Contains(folder, StringComparer.OrdinalIgnoreCase)));
        }
    }

    public async Task<FolderScanStatusIpcEntity> PrepareRuleAsync(string folder, CancellationToken cancellationToken)
    {
        if (!SplitTunnelFolderScanner.TryNormalize(folder, out string normalized))
        {
            return new() { Error = "Choose a specific local folder or folder pattern." };
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        await _scanSync.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_disposed) { return new() { Error = "Folder discovery service is stopping." }; }
                _prepared.Remove(normalized);
                _preparationStatus.IsPreparing = true;
                _preparationStatus.Entries = 0;
                _preparationStatus.Executables = 0;
            }
            FolderScanResult scan = await _scan(normalized, linked.Token, progress =>
            {
                lock (_sync)
                {
                    _preparationStatus.Entries = progress.Entries;
                    _preparationStatus.Executables = progress.Executables;
                }
            }).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_sync)
            {
                RemoveExpiredPreparations();
                if (scan.Error == null)
                {
                    // Bound only the temporary discovery cache, not the number of saved folder rules.
                    while (_prepared.Count > 0 && (_prepared.Count >= 128 ||
                        _prepared.Values.Sum(value => value.Scan.AppPaths.Length) + scan.AppPaths.Length > SplitTunnelFolderScanner.MaximumExecutables))
                    {
                        _prepared.Remove(_prepared.MinBy(pair => pair.Value.Expires).Key);
                    }
                    _prepared[normalized] = (scan, DateTimeOffset.UtcNow + PreparedLifetime);
                }
                return new() { Entries = _preparationStatus.Entries, Executables = scan.AppPaths.Length,
                    Error = scan.Error ?? string.Empty, RulePaths = [normalized] };
            }
        }
        finally
        {
            lock (_sync) { _preparationStatus.IsPreparing = false; }
            _scanSync.Release();
        }
    }

    private void RemoveExpiredPreparations()
    {
        foreach (string folder in _prepared.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
        { _prepared.Remove(folder); }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            if (_folders.Length == 0) { Stop(); return; }
            _started = true;
            RefreshWatchers();
            _periodic.Change(RecoveryInterval, RecoveryInterval);
        }
        // New watchers queue their own handoff scan. Healthy unchanged rules need no Apply-time rescan.
        lock (_sync) { QueueReconcile(_folders.Where(folder => !_snapshots.ContainsKey(folder))); }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _started = false;
            _queued = false;
            _dirty = false;
            _pendingFolders.Clear();
            _generation++;
            _scanCancellation.Cancel();
            _folders = [];
            _scanningFolders = [];
            _appPaths = [];
            _snapshots.Clear();
            _ruleAnchors.Clear();
            _appAncestors.Clear();
            _lastErrors = string.Empty;
            _watcherLimitWarned = false;
            _status = new();
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            _periodic.Change(Timeout.Infinite, Timeout.Infinite);
            DisposeWatchers();
        }
    }

    private void QueueReconcile(IEnumerable<string>? folders = null)
    {
        lock (_sync)
        {
            if (!_disposed && _started)
            {
                _pendingFolders.UnionWith(folders ?? _folders);
                if (_pendingFolders.Count == 0) { return; }
                _dirty = true;
                // A continuous update stream must not indefinitely postpone discovery.
                if (!_queued) { _queued = true; _debounce.Change(250, Timeout.Infinite); }
            }
        }
    }

    private async void RunQueuedReconcile()
    {
        int queuedGeneration;
        string[] folders;
        lock (_sync)
        {
            _dirty = false;
            queuedGeneration = _generation;
            folders = _pendingFolders.ToArray();
            _pendingFolders.Clear();
        }
        try { await ReconcileAsync(requestedFolders: folders, queuedGeneration: queuedGeneration).ConfigureAwait(false); }
        catch (Exception ex) { _logger.Error<SplitTunnelLog>("Folder reconciliation failed.", ex); }
        finally
        {
            lock (_sync)
            {
                if (queuedGeneration == _generation)
                {
                    _queued = false;
                    if (_dirty && _started && !_disposed) { QueueReconcile([]); }
                }
            }
        }
    }

    internal void Reconcile(bool force = false, bool publish = true) => ReconcileAsync(force, publish).GetAwaiter().GetResult();

    internal async Task ReconcileAsync(bool force = false, bool publish = true,
        string[]? requestedFolders = null, int? queuedGeneration = null)
    {
        bool changed = false;
        string? warning = null;
        int generation = 0;
        long revision = 0;
        string errorText = string.Empty;
        await _scanSync.WaitAsync().ConfigureAwait(false);
        try
        {
            string[] folders;
            Dictionary<string, FolderScanResult> snapshots;
            CancellationToken token;
            lock (_sync)
            {
                if (_disposed || (!force && !_started) || (queuedGeneration.HasValue && queuedGeneration != _generation)) { return; }
                folders = requestedFolders == null ? _folders
                    : _folders.Where(folder => requestedFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (folders.Length == 0) { return; }
                snapshots = new(_snapshots, StringComparer.OrdinalIgnoreCase);
                generation = _generation;
                _scanningFolders = folders;
                revision = ++_scanRevision;
                token = _scanCancellation.Token;
                _status.IsScanning = true;
                _status.Entries = 0;
                _status.Executables = 0;
            }
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            long entries = 0;
            {
                foreach (string folder in folders)
                {
                    token.ThrowIfCancellationRequested();
                    long rootEntries = 0;
                    FolderScanResult scan = await _scan(folder, token, progress =>
                    {
                        rootEntries = progress.Entries;
                        lock (_sync)
                        {
                            if (generation == _generation)
                            {
                                _status.Entries = entries + progress.Entries;
                                _status.Executables = paths.Count + progress.Executables;
                            }
                        }
                    }).ConfigureAwait(false);
                    entries += rootEntries;
                    snapshots[folder] = scan;
                    paths.Clear();
                    foreach (FolderScanResult snapshot in snapshots.Values)
                    {
                        paths.UnionWith(snapshot.AppPaths);
                        if (paths.Count > SplitTunnelFolderScanner.MaximumExecutables) { break; }
                    }
                    if (paths.Count > SplitTunnelFolderScanner.MaximumExecutables)
                    {
                        // Bound both work and cached coverage; don't scan every remaining library after overflow.
                        snapshots[folder] = new([], CombinedLimitError);
                        break;
                    }
                }
            }
            (string[] effective, string snapshotError) = GetSnapshotPaths(snapshots);
            errorText = snapshotError;
            lock (_sync)
            {
                if (_disposed || generation != _generation || (!force && !_started)) { return; }
                changed = !_appPaths.SequenceEqual(effective, StringComparer.OrdinalIgnoreCase);
                SetEffectivePaths(effective);
                _snapshots = snapshots;
                _status.Entries = entries;
                _status.Executables = effective.Length;
                if (errorText != _lastErrors)
                {
                    warning = errorText.Length == 0 ? null : errorText;
                    _lastErrors = errorText;
                }
                RefreshWatchers();
            }
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            lock (_sync) { if (generation == _generation) { _scanningFolders = []; } }
            _scanSync.Release();
        }
        if (warning != null) { _logger.Warn<SplitTunnelLog>($"Folder app rules need attention: {warning}"); }
        // Never acquire SplitTunnel's state lock while holding either monitor lock.
        if (changed && publish)
        {
            try { PathsChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex)
            {
                errorText = "Discovery completed, but applying folder app rules failed. Check service logs.";
                _logger.Error<SplitTunnelLog>(errorText, ex);
            }
        }
        lock (_sync)
        {
            if (generation == _generation && revision == _scanRevision) { _status.IsScanning = false; _status.Error = errorText; }
        }
    }

    private static (string[] Paths, string Error) GetSnapshotPaths(Dictionary<string, FolderScanResult> snapshots)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];
        foreach ((string folder, FolderScanResult scan) in snapshots)
        {
            if (scan.Error != null) { errors.Add($"{folder}: {scan.Error}"); }
            paths.UnionWith(scan.AppPaths);
            if (scan.Error == CombinedLimitError || paths.Count > SplitTunnelFolderScanner.MaximumExecutables)
            {
                if (scan.Error != CombinedLimitError) { errors.Add(CombinedLimitError); }
                paths.Clear();
                break;
            }
        }
        return (paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(), string.Join("; ", errors));
    }

    private void SetEffectivePaths(string[] paths)
    {
        _appPaths = paths;
        _appAncestors.Clear();
        foreach (string app in paths)
        {
            for (string? parent = Path.GetDirectoryName(app); parent != null && _appAncestors.Add(parent); parent = Path.GetDirectoryName(parent)) { }
        }
    }

    private void RefreshWatchers()
    {
        if (!_started) { DisposeWatchers(); return; }
        string[] anchors = _ruleAnchors.Values.Where(anchor => anchor.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string key in _watchers.Keys.Where(key => !anchors.Contains(key, StringComparer.OrdinalIgnoreCase)
                     || SplitTunnelFolderScanner.ValidateWatchRoot(key) != null).ToArray())
        {
            _watchers[key].Dispose();
            _watchers.Remove(key);
        }
        // A native watcher budget is not a saved-rule limit: all remaining rules still reconcile periodically.
        if (anchors.Length > 64 && !_watcherLimitWarned)
        {
            _watcherLimitWarned = true;
            _logger.Warn<SplitTunnelLog>("More than 64 distinct folder anchors; extra anchors use periodic discovery rather than dedicated watchers.");
        }
        foreach (string folder in anchors)
        {
            if (_watchers.Count >= 64) { break; }
            if (_watchers.ContainsKey(folder) || !SplitTunnelFolderScanner.TryNormalize(folder, out string normalized)) { continue; }
            // Only establish watchers after validating the fixed no-link anchor; periodic reconciliation
            // also recovers inaccessible, deleted, renamed and later recreated roots.
            if (SplitTunnelFolderScanner.ValidateWatchRoot(normalized) != null) { continue; }
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new(SplitTunnelFolderScanner.GetWatchRoot(normalized)) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnChanged;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watchers[folder] = watcher;
                // A new/recreated watcher must precede its recovery scan, closing the scan/watch gap.
                QueueReconcile(RulesForAnchor(folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                watcher?.Dispose();
                _logger.Warn<SplitTunnelLog>($"Folder watcher unavailable; periodic reconciliation remains active: {folder}", ex);
            }
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (IsRelevantChange(args) && sender is FileSystemWatcher watcher)
        {
            lock (_sync) { QueueReconcile(RulesForAnchor(watcher.Path)); }
        }
    }

    private string[] RulesForAnchor(string anchor) => _ruleAnchors.Where(rule =>
        string.Equals(rule.Value, anchor, StringComparison.OrdinalIgnoreCase)).Select(rule => rule.Key).ToArray();

    internal void QueueRecovery()
    {
        lock (_sync)
        {
            if (!_started || _disposed) { return; }
            // Preserve recovery candidates before recreating watchers: their scan/watch handoff needs a rescan.
            string[] unwatched = _folders.Where(folder => !_watchers.ContainsKey(_ruleAnchors[folder])).ToArray();
            RefreshWatchers();
            QueueReconcile(_folders.Where(folder => unwatched.Contains(folder, StringComparer.OrdinalIgnoreCase)
                || !_watchers.ContainsKey(_ruleAnchors[folder])
                || !_snapshots.TryGetValue(folder, out FolderScanResult? snapshot) || snapshot.Error != null));
        }
    }

    internal bool IsRelevantChange(FileSystemEventArgs args)
    {
        // Ignore logs, downloads, caches and other non-executable file churn. A moved-in populated
        // directory still needs discovery; deletion/rename of a known app's ancestor needs cleanup.
        if (Path.GetExtension(args.FullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(args.FullPath)) { return true; }
        string oldPath = args is RenamedEventArgs renamed ? renamed.OldFullPath : args.FullPath;
        if (Path.GetExtension(oldPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)) { return true; }
        if (args.ChangeType == WatcherChangeTypes.Created) { return false; }
        lock (_sync)
        {
            return _appAncestors.Contains(Path.TrimEndingDirectorySeparator(oldPath));
        }
    }
    private void OnError(object sender, ErrorEventArgs args)
    {
        _logger.Warn<SplitTunnelLog>("Folder watcher error; reconciling affected rules and recreating its watcher.", args.GetException());
        if (sender is FileSystemWatcher watcher) { RecoverAnchor(watcher.Path); }
    }

    internal void RecoverAnchor(string anchor)
    {
        lock (_sync)
        {
            if (_watchers.Remove(anchor, out FileSystemWatcher? watcher))
            {
                watcher.Dispose();
            }
            RefreshWatchers();
            QueueReconcile(RulesForAnchor(anchor));
        }
    }

    private void DisposeWatchers()
    {
        foreach (FileSystemWatcher watcher in _watchers.Values) { watcher.Dispose(); }
        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            Stop();
            _disposed = true;
            _lifetimeCancellation.Cancel();
            _prepared.Clear();
            _debounce.Dispose();
            _periodic.Dispose();
            _scanCancellation.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }
}
