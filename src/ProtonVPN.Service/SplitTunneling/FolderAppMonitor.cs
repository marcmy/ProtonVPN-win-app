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
using ProtonVPN.Common.Core.Helpers;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;

namespace ProtonVPN.Service.SplitTunneling;

/// <summary>Service-owned reconciliation: notifications are hints, never incremental truth.</summary>
public sealed class FolderAppMonitor : IFolderAppMonitor, IDisposable
{
    private readonly ILogger _logger;
    private readonly object _sync = new();
    private readonly object _scanSync = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private readonly Timer _periodic;
    private string[] _folders = [];
    private string[] _appPaths = [];
    private string _lastErrors = string.Empty;
    private int _generation;
    private bool _started;
    private bool _disposed;
    private bool _queued;
    private bool _dirty;

    public event EventHandler? PathsChanged;
    public string[] AppPaths { get { lock (_sync) { return (string[])_appPaths.Clone(); } } }

    public FolderAppMonitor(ILogger logger)
    {
        _logger = logger;
        _debounce = new(_ => RunQueuedReconcile(), null, Timeout.Infinite, Timeout.Infinite);
        _periodic = new(_ => QueueReconcile(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void ReplaceRules(string[] folders)
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _generation++;
            // Do not silently truncate saved rules if old/custom clients exceed the supported limit.
            _folders = (folders ?? []).Select(SplitTunnelAppPathResolver.Normalize)
                .Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            RefreshWatchers();
        }
        Reconcile(force: true, publish: false);
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            if (_folders.Length == 0) { Stop(); return; }
            _started = true;
            RefreshWatchers();
            _periodic.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
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
            _folders = [];
            _appPaths = [];
            _lastErrors = string.Empty;
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

    private void RunQueuedReconcile()
    {
        lock (_sync) { _dirty = false; }
        try { Reconcile(); }
        finally
        {
            lock (_sync)
            {
                _queued = false;
                if (_dirty && _started && !_disposed) { QueueReconcile(); }
            }
        }
    }

    internal void Reconcile(bool force = false, bool publish = true)
    {
        bool changed;
        string? warning = null;
        lock (_scanSync)
        {
            string[] folders;
            int generation;
            lock (_sync)
            {
                if (_disposed || (!force && !_started)) { return; }
                folders = _folders;
                generation = _generation;
            }
            List<string> errors = [];
            HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
            if (folders.Length > SplitTunnelFolderScanner.MaximumFolders)
            {
                errors.Add($"Folder-rule limit exceeded ({SplitTunnelFolderScanner.MaximumFolders}); no folder app rules applied.");
            }
            else
            {
                foreach (string folder in folders)
                {
                    FolderScanResult scan = SplitTunnelFolderScanner.Scan(folder);
                    if (scan.Error != null) { errors.Add($"{folder}: {scan.Error}"); }
                    paths.UnionWith(scan.AppPaths);
                    if (paths.Count > SplitTunnelFolderScanner.MaximumEntries)
                    {
                        errors.Add("Combined folder executable limit exceeded; no folder app rules applied.");
                        paths.Clear();
                        break;
                    }
                }
            }
            string[] effective = paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            string errorText = string.Join("; ", errors);
            lock (_sync)
            {
                if (_disposed || generation != _generation || (!force && !_started)) { return; }
                changed = !_appPaths.SequenceEqual(effective, StringComparer.OrdinalIgnoreCase);
                _appPaths = effective;
                if (errorText != _lastErrors)
                {
                    warning = errorText.Length == 0 ? null : errorText;
                    _lastErrors = errorText;
                }
                RefreshWatchers();
            }
        }
        if (warning != null) { _logger.Warn<SplitTunnelLog>($"Folder app rules need attention: {warning}"); }
        // Never acquire SplitTunnel's state lock while holding either monitor lock.
        if (changed && publish)
        {
            try { PathsChanged?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.Error<SplitTunnelLog>("Failed to apply reconciled folder app rules.", ex); }
        }
    }

    private void RefreshWatchers()
    {
        if (!_started || _folders.Length > SplitTunnelFolderScanner.MaximumFolders) { DisposeWatchers(); return; }
        foreach (string key in _watchers.Keys.Except(_folders, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            _watchers[key].Dispose();
            _watchers.Remove(key);
        }
        foreach (string folder in _folders)
        {
            if (_watchers.ContainsKey(folder) || !SplitTunnelFolderScanner.TryNormalize(folder, out string normalized)) { continue; }
            // Only establish watchers after a successful no-link scan; periodic reconciliation
            // also recovers inaccessible, deleted, renamed and later recreated roots.
            if (SplitTunnelFolderScanner.Scan(normalized).Error != null) { continue; }
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new(normalized) { IncludeSubdirectories = true,
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

    private void OnChanged(object sender, FileSystemEventArgs args) => QueueReconcile();
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
        }
    }
}
