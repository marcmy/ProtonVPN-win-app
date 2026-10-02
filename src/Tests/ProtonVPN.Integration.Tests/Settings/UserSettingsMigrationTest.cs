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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Client.Settings;
using ProtonVPN.Client.Settings.Contracts.Models;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.Client.Settings.Repositories.Contracts;
using ProtonVPN.Common.Core.Dns;

namespace ProtonVPN.Integration.Tests.Settings;

[TestClass]
public class UserSettingsMigrationTest
{
    [TestMethod]
    public void FolderSettings_OldSettingsDefaultEmptyAndJsonRoundTripsBothModeEntries()
    {
        IUserSettingsCache userCache = Substitute.For<IUserSettingsCache>();
        UserSettings settings = new(Substitute.For<IGlobalSettingsCache>(), userCache);
        Assert.AreEqual(0, settings.SplitTunnelingStandardFoldersList.Count);
        Assert.AreEqual(0, settings.SplitTunnelingInverseFoldersList.Count);

        ProtonVPN.Serialization.Json.JsonSerializer serializer = new([]);
        List<SplitTunnelingFolder> folders = [new(@"C:\EA", true), new(@"C:\tools", false)];
        string json = serializer.SerializeToString(folders);
        List<SplitTunnelingFolder>? restored = serializer.DeserializeFromString<List<SplitTunnelingFolder>>(json);
        CollectionAssert.AreEqual(folders, restored);
        SplitTunnelSettingsIpcEntity? legacy = serializer.DeserializeFromString<SplitTunnelSettingsIpcEntity>("{\"Mode\":1,\"AppPaths\":[],\"Ips\":[]}");
        Assert.IsNotNull(legacy);
        Assert.AreEqual(0, legacy.FolderPaths.Length);
        SplitTunnelSettingsIpcEntity? ipc = serializer.DeserializeFromString<SplitTunnelSettingsIpcEntity>(
            serializer.SerializeToString(new SplitTunnelSettingsIpcEntity { FolderPaths = [@"C:\EA"] }));
        CollectionAssert.AreEqual(new[] { @"C:\EA" }, ipc!.FolderPaths);
    }

    [TestMethod]
    [DataRow(DnsBlockMode.Callout, true)]
    [DataRow(DnsBlockMode.Nrpt, false)]
    public void IsLocalDnsEnabled_WhenBooleanIsMissing_MigratesLegacyDnsMode(
        DnsBlockMode legacyMode,
        bool expectedValue)
    {
        IUserSettingsCache userCache = Substitute.For<IUserSettingsCache>();
        userCache.GetValueType<bool>(SettingEncryption.Unencrypted, Arg.Any<string>())
            .Returns((bool?)null);
        userCache.GetValueType<DnsBlockMode>(SettingEncryption.Unencrypted, Arg.Any<string>())
            .Returns((DnsBlockMode?)legacyMode);

        UserSettings settings = new(Substitute.For<IGlobalSettingsCache>(), userCache);

        Assert.AreEqual(expectedValue, settings.IsLocalDnsEnabled);
        userCache.Received(1).SetValueType<bool>(expectedValue, SettingEncryption.Unencrypted, Arg.Any<string>());
    }

    [TestMethod]
    public void IsLocalDnsEnabled_WhenBooleanExists_PreservesItInsteadOfUsingLegacyMode()
    {
        IUserSettingsCache userCache = Substitute.For<IUserSettingsCache>();
        userCache.GetValueType<bool>(SettingEncryption.Unencrypted, Arg.Any<string>())
            .Returns((bool?)true);
        userCache.GetValueType<DnsBlockMode>(SettingEncryption.Unencrypted, Arg.Any<string>())
            .Returns((DnsBlockMode?)DnsBlockMode.Nrpt);

        UserSettings settings = new(Substitute.For<IGlobalSettingsCache>(), userCache);

        Assert.IsTrue(settings.IsLocalDnsEnabled);
        userCache.DidNotReceive().SetValueType<bool>(Arg.Any<bool?>(), SettingEncryption.Unencrypted, Arg.Any<string>());
    }
}
