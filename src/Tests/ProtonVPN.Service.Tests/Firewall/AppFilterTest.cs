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
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN. If not, see <https://www.gnu.org/licenses/>.
 */


#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.NetworkFilter;
using ProtonVPN.Service.Firewall;
using Action = ProtonVPN.NetworkFilter.Action;

namespace ProtonVPN.Service.Tests.Firewall;

[TestClass]
public class AppFilterTest
{
    private static readonly Tuple<Layer, Action>[] Filters = [Tuple.Create(Layer.AppAuthConnectV4, Action.HardPermit)];

    [TestMethod]
    public void Replace_PublishesNewPolicyAndRemovesOldFilters()
    {
        TestAppFilter subject = new();
        subject.Add(["old.exe"], Filters);
        subject.Replace(["new.exe", "NEW.exe"], Filters);
        CollectionAssert.AreEquivalent(new[] { "new.exe" }, subject.ActivePaths);
        Assert.AreEqual(1, subject.Commits);
    }

    [TestMethod]
    public void Replace_UnexpectedFailureRetainsOldPolicyAndCanRetry()
    {
        TestAppFilter subject = new();
        subject.Add(["old.exe"], Filters);
        subject.FailPath = "bad.exe";
        Assert.ThrowsExactly<InvalidOperationException>(() => subject.Replace(["new.exe", "bad.exe"], Filters));
        CollectionAssert.AreEquivalent(new[] { "old.exe" }, subject.ActivePaths);
        Assert.AreEqual(1, subject.Aborts);
        subject.FailPath = null;
        subject.Replace(["new.exe"], Filters);
        CollectionAssert.AreEquivalent(new[] { "new.exe" }, subject.ActivePaths);
        subject.Replace([], Filters);
        Assert.AreEqual(0, subject.ActivePaths.Length);
    }

    [TestMethod]
    public void Replace_NonLocalPathIsSkippedWithoutDiscardingHealthyPaths()
    {
        TestAppFilter subject = new() { NonLocalPath = @"\\server\share\app.exe" };
        subject.Replace(["healthy.exe", subject.NonLocalPath, "also-healthy.exe"], Filters);
        CollectionAssert.AreEquivalent(new[] { "healthy.exe", "also-healthy.exe" }, subject.ActivePaths);
        Assert.AreEqual(1, subject.Commits);
        Assert.AreEqual(0, subject.Aborts);
    }

    private sealed class TestAppFilter() : AppFilter(Substitute.For<ILogger>(), null!, null!)
    {
        private Dictionary<Guid, string> _active = [];
        private Dictionary<Guid, string>? _staged;
        public string? FailPath { get; set; }
        public string? NonLocalPath { get; set; }
        public int Commits { get; private set; }
        public int Aborts { get; private set; }
        public string[] ActivePaths => _active.Values.ToArray();
        protected override void StartTransaction() => _staged = new(_active);
        protected override void CommitTransaction() { _active = _staged!; _staged = null; Commits++; }
        protected override void AbortTransaction() { _staged = null; Aborts++; }
        protected override List<Guid> CreateFilters(string path, Tuple<Layer, Action>[] filters)
        {
            if (path == FailPath) { throw new InvalidOperationException("Simulated filter failure"); }
            if (path == NonLocalPath) { throw new NonLocalAppPathException(0x800700A1); }
            Guid id = Guid.NewGuid();
            (_staged ?? _active)[id] = path;
            return [id];
        }
        protected override void RemoveGuids(List<Guid> guids)
        {
            foreach (Guid id in guids) { (_staged ?? _active).Remove(id); }
        }
    }
}
