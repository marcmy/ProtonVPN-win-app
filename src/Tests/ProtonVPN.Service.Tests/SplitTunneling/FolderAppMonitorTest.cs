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
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Service.SplitTunneling;

namespace ProtonVPN.Service.Tests.SplitTunneling;

[TestClass]
public class FolderAppMonitorTest
{
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
            CollectionAssert.AreEqual(new[] { exe }, monitor.AppPaths);

            monitor.ReplaceRules([root]); // Removing one owner must not remove the overlapping app.
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
