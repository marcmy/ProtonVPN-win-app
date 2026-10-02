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
using ProtonVPN.Common.Core.Helpers;

namespace ProtonVPN.Common.Core.Tests.Helpers;

[TestClass]
public class SplitTunnelFolderScannerTest
{
    private string _root = null!;
    [TestInitialize] public void Initialize() => _root = Directory.CreateTempSubdirectory("proton-folder-rule-").FullName;
    [TestCleanup] public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void Scan_RecursiveExecutablesOnly_RefreshesNewVersions()
    {
        string nested = Directory.CreateDirectory(Path.Combine(_root, "EA", "version1")).FullName;
        string exe = Path.Combine(nested, "EALauncher.exe");
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(nested, "script.py"), "");
        FolderScanResult first = SplitTunnelFolderScanner.Scan($" \"{_root}\" ");
        Assert.IsNull(first.Error);
        CollectionAssert.AreEqual(new[] { exe }, first.AppPaths);

        string renamed = Path.Combine(_root, "EA", "version2");
        Directory.Move(nested, renamed);
        CollectionAssert.AreEqual(new[] { Path.Combine(renamed, "EALauncher.exe") }, SplitTunnelFolderScanner.Scan(_root).AppPaths);
        Directory.Delete(renamed, recursive: true);
        Assert.AreEqual(0, SplitTunnelFolderScanner.Scan(_root).AppPaths.Length);
    }

    [TestMethod]
    public void Normalize_RejectsBroadRootsRemoteAndUnsafePatterns()
    {
        Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize(Path.GetPathRoot(_root), out _));
        Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize(@"\\server\share\tools", out _));
        Assert.IsTrue(SplitTunnelFolderScanner.TryNormalize(Path.Combine(_root, "*"), out _));
        Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize(Path.Combine(Path.GetPathRoot(_root)!, "*", "Tools"), out _));
        Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize(Path.Combine(_root, "**"), out _));
        Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize(Path.Combine(_root, "*", "bad:name"), out _));
        Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize(Path.Combine(_root, "*", "..", "Tools"), out _));
        Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize("relative", out _));
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0) { Assert.IsFalse(SplitTunnelFolderScanner.TryNormalize(Path.Combine(windows, "System32"), out _)); }
        Assert.IsTrue(SplitTunnelFolderScanner.TryNormalize($"\"{_root}\\..\\{Path.GetFileName(_root)}\"", out string normalized));
        Assert.AreEqual(_root, normalized);
    }

    [TestMethod]
    public void Pattern_MatchesOneLevelCaseInsensitivelyThenScansRecursively()
    {
        string folder = Directory.CreateDirectory(Path.Combine(_root, "Version1", "Tools", "nested")).FullName;
        string exe = Path.Combine(folder, "app.exe");
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(_root, "outside.exe"), "");
        string unrelated = Directory.CreateDirectory(Path.Combine(_root, "Other", "Version2", "Tools")).FullName;
        File.WriteAllText(Path.Combine(unrelated, "not-matched.exe"), "");
        string pattern = Path.Combine(_root, "version?", "tools");
        FolderScanResult scan = SplitTunnelFolderScanner.Scan($"\"{pattern}\"");
        Assert.IsNull(scan.Error);
        CollectionAssert.AreEqual(new[] { exe }, scan.AppPaths);
        Assert.AreEqual(_root, SplitTunnelFolderScanner.GetWatchRoot(pattern));
        Assert.IsNull(SplitTunnelFolderScanner.Scan(Path.Combine(_root, "future*", "Tools")).Error);
        Assert.AreEqual(0, SplitTunnelFolderScanner.Scan(Path.Combine(_root, "future*", "Tools")).AppPaths.Length);
        string renamed = Path.Combine(_root, "Version2");
        Directory.Move(Path.Combine(_root, "Version1"), renamed);
        CollectionAssert.AreEqual(new[] { Path.Combine(renamed, "Tools", "nested", "app.exe") }, SplitTunnelFolderScanner.Scan(pattern).AppPaths);
    }

    [TestMethod]
    public void Pattern_LargeNonExecutableLibraryIsCoveredAcrossMatchedRoots()
    {
        string first = Directory.CreateDirectory(Path.Combine(_root, "v1")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(_root, "v2")).FullName;
        File.WriteAllText(Path.Combine(first, "app.exe"), "");
        for (int i = 0; i < 10001; i++)
        {
            File.WriteAllText(Path.Combine(second, $"{i}.txt"), "");
        }
        FolderScanResult scan = SplitTunnelFolderScanner.Scan(Path.Combine(_root, "v*"));
        Assert.IsNull(scan.Error);
        CollectionAssert.AreEqual(new[] { Path.Combine(first, "app.exe") }, scan.AppPaths);
    }

    [TestMethod]
    public async Task Scan_BatchesReportProgressAndCanBeCancelled()
    {
        for (int i = 0; i <= SplitTunnelFolderScanner.BatchSize; i++) { File.WriteAllText(Path.Combine(_root, $"{i}.txt"), ""); }
        using CancellationTokenSource cancellation = new();
        int reports = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await SplitTunnelFolderScanner.ScanAsync(_root, cancellation.Token, progress =>
            {
                Assert.AreEqual((long)SplitTunnelFolderScanner.BatchSize, progress.Entries);
                reports++;
                cancellation.Cancel();
            }));
        Assert.AreEqual(1, reports);
    }

    [TestMethod]
    public void Pattern_ExecutableLimitIsSharedAndFailureHasNoPartialCoverage()
    {
        for (int version = 0; version < 2; version++)
        {
            string folder = Directory.CreateDirectory(Path.Combine(_root, $"v{version}")).FullName;
            for (int i = 0; i <= SplitTunnelFolderScanner.MaximumExecutables / 2; i++)
            {
                File.WriteAllText(Path.Combine(folder, $"{i}.exe"), "");
            }
        }
        FolderScanResult scan = SplitTunnelFolderScanner.Scan(Path.Combine(_root, "v*"));
        Assert.IsNotNull(scan.Error);
        Assert.AreEqual(0, scan.AppPaths.Length);
    }

    [TestMethod]
    public void Containment_RequiresDirectoryBoundary()
    {
        Assert.IsTrue(SplitTunnelFolderScanner.IsWithin(_root, Path.Combine(_root, "nested", "app.exe")));
        Assert.IsFalse(SplitTunnelFolderScanner.IsWithin(_root, _root + "-other\\app.exe"));
    }

    [TestMethod]
    public void Scan_MissingOrOverdeepTreeNeverReturnsPartialCoverage()
    {
        Assert.IsNotNull(SplitTunnelFolderScanner.Scan(Path.Combine(_root, "missing")).Error);
        File.WriteAllText(Path.Combine(_root, "app.exe"), "");
        string nested = _root;
        for (int depth = 0; depth <= SplitTunnelFolderScanner.MaximumDepth; depth++)
        {
            nested = Directory.CreateDirectory(Path.Combine(nested, "d")).FullName;
        }
        FolderScanResult scan = SplitTunnelFolderScanner.Scan(_root);
        Assert.IsNotNull(scan.Error);
        Assert.AreEqual(0, scan.AppPaths.Length);
    }

    [TestMethod]
    public void Scan_SkipsLinkedDescendantsAndRejectsLinkedRoots()
    {
        string outside = Directory.CreateTempSubdirectory("proton-folder-outside-").FullName;
        string link = Path.Combine(_root, "link");
        try
        {
            File.WriteAllText(Path.Combine(outside, "outside.exe"), "");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (UnauthorizedAccessException) { Assert.Inconclusive("Creating symbolic links requires developer mode or privilege."); }
            Assert.AreEqual(0, SplitTunnelFolderScanner.Scan(_root).AppPaths.Length);
            Assert.AreEqual(0, SplitTunnelFolderScanner.Scan(Path.Combine(_root, "*")).AppPaths.Length);
            Assert.IsNotNull(SplitTunnelFolderScanner.Scan(link).Error);
        }
        finally
        {
            if (Directory.Exists(link)) { Directory.Delete(link); }
            Directory.Delete(outside, recursive: true);
        }
    }
}
