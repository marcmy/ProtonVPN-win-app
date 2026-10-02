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
