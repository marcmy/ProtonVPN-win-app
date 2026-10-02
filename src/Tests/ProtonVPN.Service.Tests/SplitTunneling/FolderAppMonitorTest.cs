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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Service.SplitTunneling;
using ProtonVPN.Common.Core.Helpers;

namespace ProtonVPN.Service.Tests.SplitTunneling;

[TestClass]
public class FolderAppMonitorTest
{
    [TestMethod]
    public void RecoveryHealthChecksAreInfrequentRatherThanEveryFifteenSeconds()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(5), FolderAppMonitor.RecoveryInterval);
    }

    private static async Task WaitUntilAsync(Func<bool> completed)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!completed()) { await Task.Delay(20, timeout.Token); }
    }

    [TestMethod]
    public async Task TargetedUpdates_EnforceCombinedLimitAcrossCachedRulesAndRecoverWithoutRescanningThem()
    {
        string root = Directory.CreateTempSubdirectory("proton-cached-budget-").FullName;
        string first = Path.Combine(root, "first");
        string second = Path.Combine(root, "second");
        string[] library = new string[SplitTunnelFolderScanner.MaximumExecutables];
        for (int i = 0; i < library.Length; i++) { library[i] = Path.Combine(first, $"{i}.exe"); }
        bool addExtra = false;
        int firstScans = 0;
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>(), (folder, _, _) =>
        {
            if (folder == first) { firstScans++; return Task.FromResult(new FolderScanResult(library, null)); }
            return Task.FromResult(new FolderScanResult(addExtra ? [Path.Combine(second, "extra.exe")] : [], null));
        });
        try
        {
            monitor.ReplaceRules([first, second]);
            await monitor.ReconcileAsync(force: true);
            Assert.AreEqual(library.Length, monitor.AppPaths.Length);
            addExtra = true;
            await monitor.ReconcileAsync(force: true, requestedFolders: [second]);
            Assert.AreEqual(0, monitor.AppPaths.Length);
            StringAssert.Contains(monitor.Status.Error, "Combined folder executable limit");
            addExtra = false;
            await monitor.ReconcileAsync(force: true, requestedFolders: [second]);
            Assert.AreEqual(library.Length, monitor.AppPaths.Length);
            Assert.AreEqual(string.Empty, monitor.Status.Error);
            Assert.AreEqual(1, firstScans);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task HealthyRules_DoNotRescanOnRecoveryTicksAndChangesOnlyRescanAffectedAnchor()
    {
        string root = Directory.CreateTempSubdirectory("proton-targeted-watch-").FullName;
        string first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
        string original = Path.Combine(second, "original.exe");
        File.WriteAllText(original, "");
        ConcurrentDictionary<string, int> counts = new();
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>(), async (folder, token, progress) =>
        {
            counts.AddOrUpdate(folder, 1, (_, count) => count + 1);
            return await SplitTunnelFolderScanner.ScanAsync(folder, token, progress);
        });
        try
        {
            monitor.ReplaceRules([first, second]);
            monitor.Start();
            await WaitUntilAsync(() => !monitor.Status.IsScanning);
            for (int i = 0; i < 3; i++) { monitor.QueueRecovery(); }
            await Task.Delay(500);
            Assert.AreEqual(1, counts[first]);
            Assert.AreEqual(1, counts[second]);
            string added = Path.Combine(first, "new.exe");
            File.WriteAllText(added, "");
            await WaitUntilAsync(() => Array.Exists(monitor.AppPaths, app => app == added) && !monitor.Status.IsScanning);
            Assert.AreEqual(2, counts[first]);
            Assert.AreEqual(1, counts[second]);
            CollectionAssert.AreEquivalent(new[] { original, added }, monitor.AppPaths);
            monitor.RecoverAnchor(first); // The same targeted path used after a watcher error/overflow.
            await WaitUntilAsync(() => counts[first] == 3 && !monitor.Status.IsScanning);
            Assert.AreEqual(1, counts[second]);
            monitor.QueueRecovery();
            await Task.Delay(500);
            Assert.AreEqual(3, counts[first]); // Watcher was recreated after its recovery scan.
            CollectionAssert.AreEquivalent(new[] { original, added }, monitor.AppPaths);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Recovery_RetriesMissingRulesWithoutRescanningHealthyRules()
    {
        string root = Directory.CreateTempSubdirectory("proton-unwatched-recovery-").FullName;
        string healthy = Directory.CreateDirectory(Path.Combine(root, "healthy")).FullName;
        string missing = Path.Combine(root, "missing");
        string original = Path.Combine(healthy, "original.exe");
        File.WriteAllText(original, "");
        ConcurrentDictionary<string, int> counts = new();
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>(), async (folder, token, progress) =>
        {
            counts.AddOrUpdate(folder, 1, (_, count) => count + 1);
            return await SplitTunnelFolderScanner.ScanAsync(folder, token, progress);
        });
        try
        {
            monitor.ReplaceRules([healthy, missing]);
            monitor.Start();
            await WaitUntilAsync(() => !monitor.Status.IsScanning);
            Assert.IsFalse(string.IsNullOrEmpty(monitor.Status.Error));
            Directory.CreateDirectory(missing);
            string added = Path.Combine(missing, "new.exe");
            File.WriteAllText(added, "");
            monitor.QueueRecovery();
            await WaitUntilAsync(() => Array.Exists(monitor.AppPaths, app => app == added) && !monitor.Status.IsScanning);
            Assert.AreEqual(1, counts[healthy]);
            Assert.AreEqual(2, counts[missing]);
            Assert.AreEqual(string.Empty, monitor.Status.Error);
            CollectionAssert.AreEquivalent(new[] { original, added }, monitor.AppPaths);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task StartedMonitor_IgnoresActualNonExecutableChurnButDiscoversNewApp()
    {
        string root = Directory.CreateTempSubdirectory("proton-folder-idle-").FullName;
        int scans = 0;
        TaskCompletionSource initialScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>(), async (folder, token, progress) =>
        {
            Interlocked.Increment(ref scans);
            FolderScanResult result = await SplitTunnelFolderScanner.ScanAsync(folder, token, progress);
            initialScan.TrySetResult();
            return result;
        });
        try
        {
            monitor.ReplaceRules([root]);
            monitor.Start();
            await initialScan.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 50; i++)
            {
                string file = Path.Combine(root, $"cache{i}.tmp");
                File.WriteAllText(file, "");
                File.Move(file, Path.Combine(root, $"cache{i}.log"));
                File.Delete(Path.Combine(root, $"cache{i}.log"));
            }
            // Allow the real watcher/debounce pipeline time to fire if an irrelevant event was queued.
            await Task.Delay(750);
            Assert.AreEqual(1, Volatile.Read(ref scans));
            TaskCompletionSource discovered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string executable = Path.Combine(root, "new.exe");
            monitor.PathsChanged += (_, _) =>
            {
                if (Array.Exists(monitor.AppPaths, path => path == executable)) { discovered.TrySetResult(); }
            };
            File.WriteAllText(executable, "");
            await discovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(new[] { executable }, monitor.AppPaths);
            Assert.AreEqual(2, Volatile.Read(ref scans));
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void WatcherHints_IgnoreFileChurnButKeepExecutablesAndDirectoryMoves()
    {
        string root = Directory.CreateTempSubdirectory("proton-folder-hints-").FullName;
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>());
        try
        {
            string child = Directory.CreateDirectory(Path.Combine(root, "version.1")).FullName;
            string exe = Path.Combine(child, "app.exe");
            File.WriteAllText(exe, "");
            monitor.ReplaceRules([root]);
            monitor.Reconcile(force: true);
            Assert.IsFalse(monitor.IsRelevantChange(new(WatcherChangeTypes.Created, child, "cache.tmp")));
            Assert.IsFalse(monitor.IsRelevantChange(new(WatcherChangeTypes.Deleted, child, "output.log")));
            Assert.IsFalse(monitor.IsRelevantChange(new RenamedEventArgs(WatcherChangeTypes.Renamed, child, "new.tmp", "old.tmp")));
            Assert.IsTrue(monitor.IsRelevantChange(new(WatcherChangeTypes.Created, child, "APP.EXE")));
            Assert.IsTrue(monitor.IsRelevantChange(new RenamedEventArgs(WatcherChangeTypes.Renamed, child, "app.old", "app.exe")));
            Assert.IsTrue(monitor.IsRelevantChange(new RenamedEventArgs(WatcherChangeTypes.Renamed, child, "app.exe", "download.tmp")));
            Assert.IsTrue(monitor.IsRelevantChange(new(WatcherChangeTypes.Created, root, "version.1")));
            Directory.Delete(child, recursive: true);
            Assert.IsTrue(monitor.IsRelevantChange(new(WatcherChangeTypes.Deleted, root, "version.1")));
            Assert.IsTrue(monitor.IsRelevantChange(new RenamedEventArgs(WatcherChangeTypes.Renamed, root, "version.2", "version.1")));
            Assert.IsFalse(monitor.IsRelevantChange(new(WatcherChangeTypes.Deleted, root, "version.1-other")));
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task BatchedRescan_KeepsSnapshotUntilCompleteAndStopCancelsIt()
    {
        string root = Directory.CreateTempSubdirectory("proton-library-scan-").FullName;
        int scans = 0;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>(), async (folder, token, progress) =>
        {
            if (++scans > 1)
            {
                progress(new(512, 1));
                if (scans == 2) { await release.Task.WaitAsync(token); }
                else { await Task.Delay(Timeout.Infinite, token); }
            }
            return await SplitTunnelFolderScanner.ScanAsync(folder, token, progress);
        });
        try
        {
            string original = Path.Combine(root, "original.exe");
            File.WriteAllText(original, "");
            monitor.ReplaceRules([root]);
            await monitor.ReconcileAsync(force: true);
            for (int i = 0; i < 1024; i++) { File.WriteAllText(Path.Combine(root, $"{i}.txt"), ""); }
            string added = Path.Combine(root, "added.exe");
            File.WriteAllText(added, "");
            Task rescan = monitor.ReconcileAsync(force: true);
            Assert.IsTrue(monitor.Status.IsScanning);
            Assert.IsTrue(monitor.Status.Entries >= 512);
            CollectionAssert.AreEqual(new[] { original }, monitor.AppPaths);
            release.SetResult();
            await rescan;
            Assert.IsFalse(monitor.Status.IsScanning);
            Assert.AreEqual(2, monitor.AppPaths.Length);
            Assert.AreEqual(2, monitor.Status.Executables);

            Task cancelled = monitor.ReconcileAsync(force: true);
            monitor.Stop();
            await cancelled;
            Assert.IsFalse(monitor.Status.IsActive);
            Assert.IsFalse(monitor.Status.IsScanning);
            Assert.AreEqual(0, monitor.AppPaths.Length);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task Replacement_CancelsOldScanAndMoreThanTwentyRulesRemainSupported()
    {
        string root = Directory.CreateTempSubdirectory("proton-many-folders-").FullName;
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>(), async (folder, token, progress) =>
        {
            if (folder == root) { progress(new(512, 0)); await Task.Delay(Timeout.Infinite, token); }
            return await SplitTunnelFolderScanner.ScanAsync(folder, token, progress);
        });
        try
        {
            for (int i = 0; i < 1024; i++) { File.WriteAllText(Path.Combine(root, $"{i}.txt"), ""); }
            monitor.ReplaceRules([root]);
            Task obsolete = monitor.ReconcileAsync(force: true);
            string[] rules = new string[25];
            for (int i = 0; i < rules.Length; i++)
            {
                rules[i] = Directory.CreateDirectory(Path.Combine(root, $"tools{i}")).FullName;
                File.WriteAllText(Path.Combine(rules[i], "app.exe"), "");
            }
            monitor.ReplaceRules(rules);
            await obsolete;
            await monitor.ReconcileAsync(force: true);
            Assert.AreEqual(25, monitor.AppPaths.Length);
            Assert.AreEqual(25, monitor.Status.RulePaths.Length);
            Assert.AreEqual(string.Empty, monitor.Status.Error);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PatternMonitor_DiscoversNewMatchingRootsWithoutApply()
    {
        string root = Directory.CreateTempSubdirectory("proton-pattern-watch-").FullName;
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>());
        try
        {
            monitor.ReplaceRules([Path.Combine(root, "version*", "Tools")]);
            Assert.AreEqual(0, monitor.AppPaths.Length);
            monitor.Start();
            string folder = Path.Combine(root, "version1", "Tools", "nested");
            string exe = Path.Combine(folder, "app.exe");
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.PathsChanged += (_, _) =>
            {
                if (Array.Exists(monitor.AppPaths, app => app == exe)) { changed.TrySetResult(); }
            };
            Directory.CreateDirectory(folder);
            File.WriteAllText(exe, "");
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(new[] { exe }, monitor.AppPaths);
            string renamed = Path.Combine(root, "version2");
            Directory.Move(Path.Combine(root, "version1"), renamed);
            monitor.Reconcile(force: true);
            exe = Path.Combine(renamed, "Tools", "nested", "app.exe");
            folder = Path.GetDirectoryName(exe)!;
            CollectionAssert.AreEqual(new[] { exe }, monitor.AppPaths);
            monitor.ReplaceRules([Path.Combine(root, "version*", "Tools"), folder]);
            monitor.ReplaceRules([folder]);
            monitor.Reconcile(force: true);
            CollectionAssert.AreEqual(new[] { exe }, monitor.AppPaths);
            Directory.Delete(renamed, recursive: true);
            monitor.Reconcile(force: true);
            Assert.AreEqual(0, monitor.AppPaths.Length);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task StartedMonitor_DiscoversNestedExecutableWithoutUiOrApply()
    {
        string root = Directory.CreateTempSubdirectory("proton-folder-watch-").FullName;
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>());
        try
        {
            monitor.ReplaceRules([root]);
            monitor.Start();
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string nested = Directory.CreateDirectory(Path.Combine(root, "version")).FullName;
            string path = Path.Combine(nested, "new.exe");
            monitor.PathsChanged += (_, _) =>
            {
                if (Array.Exists(monitor.AppPaths, app => app == path)) { changed.TrySetResult(); }
            };
            File.WriteAllText(path, "");
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CollectionAssert.AreEqual(new[] { path }, monitor.AppPaths);
        }
        finally { monitor.Stop(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Reconcile_OverlappingOwnersTreeRenameAndMissedEvents()
    {
        string root = Directory.CreateTempSubdirectory("proton-folder-monitor-").FullName;
        try
        {
            string child = Directory.CreateDirectory(Path.Combine(root, "child")).FullName;
            string exe = Path.Combine(child, "app.exe");
            File.WriteAllText(exe, "");
            using FolderAppMonitor monitor = new(Substitute.For<ILogger>());
            monitor.ReplaceRules([root, child]);
            monitor.Reconcile(force: true);
            CollectionAssert.AreEqual(new[] { exe }, monitor.AppPaths);

            monitor.ReplaceRules([root]); // Removing one owner must not remove the overlapping app.
            monitor.Reconcile(force: true);
            CollectionAssert.AreEqual(new[] { exe }, monitor.AppPaths);
            string renamed = Path.Combine(root, "renamed");
            Directory.Move(child, renamed);
            // A full reconciliation is also the recovery path for watcher overflow/missed events.
            monitor.Reconcile(force: true);
            CollectionAssert.AreEqual(new[] { Path.Combine(renamed, "app.exe") }, monitor.AppPaths);
            Directory.Delete(renamed, recursive: true);
            monitor.Reconcile(force: true);
            Assert.AreEqual(0, monitor.AppPaths.Length);

            monitor.Stop();
            File.WriteAllText(Path.Combine(root, "later.exe"), "");
            monitor.Reconcile();
            Assert.AreEqual(0, monitor.AppPaths.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void Reconcile_UnavailableRootIsNotKeptAsStaleCoverage()
    {
        string root = Directory.CreateTempSubdirectory("proton-folder-missing-").FullName;
        using FolderAppMonitor monitor = new(Substitute.For<ILogger>());
        try
        {
            string exe = Path.Combine(root, "app.exe");
            File.WriteAllText(exe, "");
            monitor.ReplaceRules([root]);
            monitor.Reconcile(force: true);
            Assert.AreEqual(1, monitor.AppPaths.Length);
            Directory.Delete(root, recursive: true);
            monitor.Reconcile(force: true);
            Assert.AreEqual(0, monitor.AppPaths.Length);
            Directory.CreateDirectory(root);
            File.WriteAllText(exe, "");
            monitor.Reconcile(force: true);
            Assert.AreEqual(1, monitor.AppPaths.Length);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }
}
