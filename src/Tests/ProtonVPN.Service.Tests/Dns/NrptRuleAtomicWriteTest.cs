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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using ProtonVPN.OperatingSystems.NRPT;

namespace ProtonVPN.Service.Tests.Dns;

[TestClass]
public class NrptRuleAtomicWriteTest
{
    private const string NAME_SERVERS = "10.2.0.1";

    // The NRPT lives in the SYSTEM hive, which supports registry transactions. Its scratch key requires
    // elevation, which CI runners have.
    private const string SYSTEM_HIVE_SCRATCH_ROOT = @"SYSTEM\ProtonVPN.Tests";
    private const string USER_HIVE_SCRATCH_ROOT = @"Software\ProtonVPN.Tests";

    private RegistryKey _baseKey;
    private string _scratchRoot;
    private string _rulesPath;

    [TestCleanup]
    public void TestCleanup()
    {
        if (_baseKey is not null)
        {
            _baseKey.DeleteSubKeyTree($@"{_scratchRoot}\{nameof(NrptRuleAtomicWriteTest)}", throwOnMissingSubKey: false);
            using (RegistryKey scratchRootKey = _baseKey.OpenSubKey(_scratchRoot))
            {
                if (scratchRootKey is not null && scratchRootKey.SubKeyCount == 0 && scratchRootKey.ValueCount == 0)
                {
                    _baseKey.DeleteSubKey(_scratchRoot, throwOnMissingSubKey: false);
                }
            }
            _baseKey.Dispose();
        }
    }

    [TestMethod]
    public void CreateRule_ShouldNotExposePartialRuleBeforeAllValuesAreWritten()
    {
        UseSystemHiveScratchKey();
        bool wasRuleVisibleBeforeCommit = true;
        string committedRulePath = null;

        bool isCreated = StaticNrptInvoker.CreateRule(_baseKey, _rulesPath, NAME_SERVERS,
            onException: (message, ex) => Assert.Fail($"{message}: {ex}"),
            onError: Assert.Fail,
            beforeCommit: rulePath =>
            {
                committedRulePath = rulePath;
                using RegistryKey outsideTransaction = _baseKey.OpenSubKey(rulePath);
                wasRuleVisibleBeforeCommit = outsideTransaction is not null;
            });

        Assert.IsTrue(isCreated);
        Assert.IsNotNull(committedRulePath, "The rule must be written through a registry transaction.");
        Assert.IsFalse(wasRuleVisibleBeforeCommit,
            "The DNS Client service must not be able to read the NRPT rule before all of its values are written.");
        AssertCompleteRule(committedRulePath);
    }

    [TestMethod]
    public void CreateRule_WhenCommitIsAbandoned_ShouldLeaveNoPartialRule()
    {
        UseSystemHiveScratchKey();
        string abandonedRulePath = null;

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            StaticNrptInvoker.CreateRule(_baseKey, _rulesPath, NAME_SERVERS,
                onException: (_, ex) => throw ex,
                onError: Assert.Fail,
                beforeCommit: rulePath =>
                {
                    abandonedRulePath = rulePath;
                    throw new InvalidOperationException("Simulated failure before commit");
                }));

        Assert.IsNotNull(abandonedRulePath);
        using RegistryKey ruleKey = _baseKey.OpenSubKey(abandonedRulePath);
        Assert.IsNull(ruleKey, "An uncommitted NRPT rule must be rolled back.");
    }

    // Whether a user hive supports registry transactions depends on the machine (CI runners do, many
    // desktops do not), so this covers the fallback where it is unsupported and the transaction elsewhere.
    [TestMethod]
    public void CreateRule_WithOrWithoutHiveTransactionSupport_ShouldWriteCompleteRule()
    {
        UseScratchKey(RegistryHive.CurrentUser, USER_HIVE_SCRATCH_ROOT);

        bool isCreated = StaticNrptInvoker.CreateRule(_baseKey, _rulesPath, NAME_SERVERS,
            onException: (message, ex) => Assert.Fail($"{message}: {ex}"),
            onError: Assert.Fail,
            beforeCommit: null);

        Assert.IsTrue(isCreated);
        using RegistryKey rulesKey = _baseKey.OpenSubKey(_rulesPath);
        string[] ruleNames = rulesKey.GetSubKeyNames();
        Assert.HasCount(1, ruleNames);
        AssertCompleteRule($@"{_rulesPath}\{ruleNames[0]}");
    }

    private void UseSystemHiveScratchKey()
    {
        try
        {
            UseScratchKey(RegistryHive.LocalMachine, SYSTEM_HIVE_SCRATCH_ROOT);
        }
        catch (UnauthorizedAccessException)
        {
            _baseKey.Dispose();
            _baseKey = null;
            Assert.Inconclusive("Writing a scratch key to the SYSTEM hive requires an elevated test run.");
        }
    }

    private void UseScratchKey(RegistryHive hive, string scratchRoot)
    {
        _baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        _scratchRoot = scratchRoot;
        _rulesPath = $@"{scratchRoot}\{nameof(NrptRuleAtomicWriteTest)}\{Guid.NewGuid():N}";
        _baseKey.CreateSubKey(_rulesPath).Dispose();
    }

    private void AssertCompleteRule(string rulePath)
    {
        using RegistryKey ruleKey = _baseKey.OpenSubKey(rulePath);
        Assert.IsNotNull(ruleKey);
        Assert.AreEqual("Proton VPN", ruleKey.GetValue("DisplayName"));
        Assert.AreEqual(NAME_SERVERS, ruleKey.GetValue("GenericDNSServers"));
        Assert.AreEqual(8, ruleKey.GetValue("ConfigOptions"));
        Assert.AreEqual(2, ruleKey.GetValue("Version"));
        CollectionAssert.AreEqual(new[] { "." }, (string[])ruleKey.GetValue("Name"));
    }
}
