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
    // Watcher events drive normal discovery. This is only recovery for lost notifications/unwatched roots.
    internal static readonly TimeSpan RecoveryInterval = TimeSpan.FromMinutes(5);
    private readonly ILogger _logger;
    private readonly Func<string, CancellationToken, Action<FolderScanProgress>, Task<FolderScanResult>> _scan;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _scanSync = new(1);
    private CancellationTokenSource _scanCancellation = new();
    private FolderScanStatusIpcEntity _status = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private readonly Timer _periodic;
    private string[] _folders = [];
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
                    Executables = _status.Executables, Error = _status.Error, RulePaths = (string[])_folders.Clone() };
            }
        }
    }

    public FolderAppMonitor(ILogger logger)
        : this(logger, (folder, token, progress) => SplitTunnelFolderScanner.ScanAsync(folder, token, progress)) { }

    internal FolderAppMonitor(ILogger logger, Func<string, CancellationToken, Action<FolderScanProgress>, Task<FolderScanResult>> scan)
    {
        _logger = logger;
        _scan = scan;
        _debounce = new(_ => RunQueuedReconcile(), null, Timeout.Infinite, Timeout.Infinite);
        _periodic = new(_ => QueueReconcile(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void ReplaceRules(string[] folders)
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _generation++;
            _scanCancellation.Cancel();
            _scanCancellation.Dispose();
            _scanCancellation = new();
            _queued = false;
            _dirty = false;
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            // Saved folder count is unrestricted; executable and traversal budgets remain separate.
            string[] replacement = (folders ?? []).Select(SplitTunnelAppPathResolver.Normalize)
                .Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!_folders.SequenceEqual(replacement, StringComparer.OrdinalIgnoreCase)) { _appPaths = []; }
            _folders = replacement;
            _status = new() { IsScanning = _folders.Length > 0 };
            RefreshWatchers();
        }
        QueueReconcile();
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
        // Reconcile the initial scan/watch handoff, including newly appeared roots.
        QueueReconcile();
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _started = false;
            _queued = false;
            _dirty = false;
            _generation++;
            _scanCancellation.Cancel();
            _folders = [];
            _appPaths = [];
            _lastErrors = string.Empty;
            _watcherLimitWarned = false;
            _status = new();
            _debounce.Change(Timeout.Infinite, Timeout.Infinite);
            _periodic.Change(Timeout.Infinite, Timeout.Infinite);
            DisposeWatchers();
        }
    }

    private void QueueReconcile()
    {
        lock (_sync)
        {
            if (!_disposed && _started)
            {
                _dirty = true;
                // A continuous update stream must not indefinitely postpone discovery.
                if (!_queued) { _queued = true; _debounce.Change(250, Timeout.Infinite); }
            }
        }
    }

    private async void RunQueuedReconcile()
    {
        int queuedGeneration;
        lock (_sync) { _dirty = false; queuedGeneration = _generation; }
        try { await ReconcileAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.Error<SplitTunnelLog>("Folder reconciliation failed.", ex); }
        finally
        {
            lock (_sync)
            {
                if (queuedGeneration == _generation)
                {
                    _queued = false;
                    if (_dirty && _started && !_disposed) { QueueReconcile(); }
                }
            }
        }
    }

    internal void Reconcile(bool force = false, bool publish = true) => ReconcileAsync(force, publish).GetAwaiter().GetResult();

    internal async Task ReconcileAsync(bool force = false, bool publish = true)
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
            CancellationToken token;
            lock (_sync)
            {
                if (_disposed || (!force && !_started)) { return; }
                folders = _folders;
                generation = _generation;
                revision = ++_scanRevision;
                token = _scanCancellation.Token;
                _status.IsScanning = true;
                _status.Entries = 0;
                _status.Executables = 0;
            }
            List<string> errors = [];
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
                    if (scan.Error != null) { errors.Add($"{folder}: {scan.Error}"); }
                    paths.UnionWith(scan.AppPaths);
                    if (paths.Count > SplitTunnelFolderScanner.MaximumExecutables)
                    {
                        errors.Add("Combined folder executable limit exceeded; no folder app rules applied.");
                        paths.Clear();
                        break;
                    }
                }
            }
            string[] effective = paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            errorText = string.Join("; ", errors);
            lock (_sync)
            {
                if (_disposed || generation != _generation || (!force && !_started)) { return; }
                changed = !_appPaths.SequenceEqual(effective, StringComparer.OrdinalIgnoreCase);
                _appPaths = effective;
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
        finally { _scanSync.Release(); }
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

    private void RefreshWatchers()
    {
        if (!_started) { DisposeWatchers(); return; }
        string[] anchors = _folders.Select(SplitTunnelFolderScanner.GetWatchRoot).Where(anchor => anchor.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string key in _watchers.Keys.Except(anchors, StringComparer.OrdinalIgnoreCase).ToArray())
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
        if (IsRelevantChange(args)) { QueueReconcile(); }
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
            return _appPaths.Any(app => SplitTunnelFolderScanner.IsWithin(oldPath, app));
        }
    }
    private void OnError(object sender, ErrorEventArgs args)
    {
        _logger.Warn<SplitTunnelLog>("Folder watcher error; reconciling the full tree and recreating its watcher.", args.GetException());
        lock (_sync)
        {
            foreach (string key in _watchers.Where(pair => ReferenceEquals(pair.Value, sender)).Select(pair => pair.Key).ToArray())
            {
                _watchers[key].Dispose();
                _watchers.Remove(key);
            }
        }
        QueueReconcile();
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
            Stop();
            _disposed = true;
            _debounce.Dispose();
            _periodic.Dispose();
            _scanCancellation.Dispose();
        }
    }
}
