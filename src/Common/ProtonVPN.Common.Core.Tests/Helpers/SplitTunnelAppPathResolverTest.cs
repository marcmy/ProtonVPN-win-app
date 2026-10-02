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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Common.Core.Helpers;

namespace ProtonVPN.Common.Core.Tests.Helpers;

[TestClass]
public class SplitTunnelAppPathResolverTest
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateTempSubdirectory("proton-app-pattern-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    [DataRow("  \"C:\\Downloads\\OOSU10.exe\"  ", "C:\\Downloads\\OOSU10.exe")]
    [DataRow("C:\\Downloads\\OOSU10.exe", "C:\\Downloads\\OOSU10.exe")]
    [DataRow("  \"C:\\Program Files\\app.exe\"  ", "C:\\Program Files\\app.exe")]
    [DataRow("\"C:\\app.exe", "\"C:\\app.exe")]
    [DataRow("C:\\app.exe\"", "C:\\app.exe\"")]
    [DataRow(null, "")]
    public void Normalize_StripsOnlyPairedOuterQuotes(string? path, string expected)
    {
        Assert.AreEqual(expected, SplitTunnelAppPathResolver.Normalize(path));
    }

    [TestMethod]
    public void Resolve_ExpandsVersionFoldersAndDeduplicatesCachedPaths()
    {
        string first = CreateFile("1.0", "app.exe");
        string second = CreateFile("2.0", "app.exe");
        CreateFile("2.0", "helper.exe");
        string pattern = Path.Combine(_root, "*", "app.exe");

        string[] paths = SplitTunnelAppPathResolver.Resolve([pattern, first, second.ToUpperInvariant()]);

        CollectionAssert.AreEquivalent(new[] { first, second }, paths);
        Assert.IsFalse(Array.Exists(paths, path => path.Contains('*')));
    }

    [TestMethod]
    public void Resolve_RefreshesAfterNewVersionAppearsWithoutChangingSavedRule()
    {
        string first = CreateFile("1.0", "app.exe");
        string pattern = Path.Combine(_root, "*", "app.exe");
        string[] saved = [pattern];
        CollectionAssert.AreEqual(new[] { first }, SplitTunnelAppPathResolver.Resolve(saved));

        Directory.Delete(Path.Combine(_root, "1.0"), recursive: true);
        string second = CreateFile("2.0", "app.exe");

        CollectionAssert.AreEqual(new[] { second }, SplitTunnelAppPathResolver.Resolve(saved));
        Assert.AreEqual(pattern, saved[0]);
    }

    [TestMethod]
    public void ResolvePattern_TraversesOnlySpecifiedLevels()
    {
        string first = CreateFile("vendor1", "version1", "app.exe");
        string second = CreateFile("vendor2", "version2", "app.exe");
        CreateFile("vendor1", "version1", "nested", "app.exe");
        CreateFile("other", "version1", "app.exe");

        CollectionAssert.AreEquivalent(new[] { first, second },
            SplitTunnelAppPathResolver.ResolvePattern(Path.Combine(_root, "vendor*", "version*", "app.exe")));
    }

    [TestMethod]
    public void Resolve_UnmatchedOrInvalidPatternsNeverReachNativeFilters()
    {
        string missing = Path.Combine(_root, "missing*", "app.exe");
        string notExe = Path.Combine(_root, "*", "app.dll");
        string traversal = Path.Combine(_root, "*", "..", "app.exe");

        Assert.AreEqual(0, SplitTunnelAppPathResolver.Resolve([missing, notExe, traversal, "relative*\\app.exe", "?"]).Length);
        Assert.AreEqual(0, SplitTunnelAppPathResolver.Resolve(null).Length);
    }

    [TestMethod]
    public void Resolve_PreservesExplicitPathsAndPackageIdentifiers()
    {
        string[] paths = ["C:\\not-installed\\app.exe", "Microsoft.App_abc123", @"\\?\C:\tools\app.exe"];
        CollectionAssert.AreEqual(paths, SplitTunnelAppPathResolver.Resolve(paths));
        CollectionAssert.AreEqual(new[] { paths[0] }, SplitTunnelAppPathResolver.Resolve([$"  \"{paths[0]}\"  ", paths[0]]));
    }

    [TestMethod]
    public void Resolve_AcceptsQuotedWildcardRule()
    {
        string file = CreateFile("version1", "app.exe");
        string pattern = Path.Combine(_root, "version*", "app.exe");
        CollectionAssert.AreEqual(new[] { file }, SplitTunnelAppPathResolver.Resolve([$"\"{pattern}\""]));
    }

    private string CreateFile(params string[] segments)
    {
        string path = Path.Combine(_root, Path.Combine(segments));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
