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

using NSubstitute;
using ProtonVPN.Client.Logic.Connection.RequestCreators;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Enums;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.EntityMapping.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Dns;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Client.Logic.Connection.Tests;

[TestClass]
public class MainSettingsRequestCreatorTest
{
    [TestMethod]
    public void CreateForGuestHole_ShouldUseSafeDefaultsRegardlessOfUserSettings()
    {
        // Arrange: deliberately enable ordinary user settings that must not leak into Guest Hole.
        ISettings settings = Substitute.For<ISettings>();
        IEntityMapper entityMapper = Substitute.For<IEntityMapper>();

        settings.IsSplitTunnelingEnabled.Returns(true);
        settings.IsPortForwardingEnabled.Returns(true);
        settings.IsNetShieldEnabled.Returns(true);
        settings.NatType.Returns(NatType.Moderate);
        settings.IsIpv6LeakProtectionEnabled.Returns(false);
        settings.IsIpv6Enabled.Returns(true);
        settings.IsLocalAreaNetworkAccessEnabled.Returns(true);
        settings.IsLocalDnsEnabled.Returns(true);
        settings.IsVpnAcceleratorEnabled.Returns(false);
        settings.OpenVpnAdapter.Returns(OpenVpnAdapter.Tun);
        settings.WireGuardConnectionTimeout.Returns(TimeSpan.FromSeconds(1));
        settings.IsShareCrashReportsEnabled.Returns(true);

        MainSettingsRequestCreator creator = new(settings, entityMapper);

        // Act
        MainSettingsIpcEntity guestHoleSettings = creator.CreateForGuestHole();

        // Assert
        Assert.AreEqual(KillSwitchModeIpcEntity.Off, guestHoleSettings.KillSwitchMode);
        Assert.AreEqual(SplitTunnelModeIpcEntity.Disabled, guestHoleSettings.SplitTunnel.Mode);
        Assert.AreEqual(0, guestHoleSettings.SplitTunnel.AppPaths.Length);
        Assert.AreEqual(0, guestHoleSettings.SplitTunnel.Ips.Length);
        Assert.IsFalse(guestHoleSettings.PortForwarding);
        Assert.AreEqual(0, guestHoleSettings.NetShieldMode);
        Assert.IsFalse(guestHoleSettings.ModerateNat);
        Assert.IsTrue(guestHoleSettings.Ipv6LeakProtection);
        Assert.IsFalse(guestHoleSettings.IsIpv6Enabled);
        Assert.IsFalse(guestHoleSettings.IsLocalAreaNetworkAccessEnabled);
        Assert.IsTrue(guestHoleSettings.SplitTcp);
        Assert.AreEqual(OpenVpnAdapterIpcEntity.Tap, guestHoleSettings.OpenVpnAdapter);
        Assert.AreEqual(DefaultSettings.ProlongedWireGuardConnectionTimeout, guestHoleSettings.WireGuardConnectionTimeout);
        Assert.AreEqual(DnsBlockModeIpcEntity.Nrpt, guestHoleSettings.DnsBlockMode);
        Assert.AreEqual(DefaultSettings.ShouldDisableWeakHostSetting, guestHoleSettings.ShouldDisableWeakHostSetting);
        Assert.IsTrue(guestHoleSettings.IsShareCrashReportsEnabled);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CreateForGuestHole_ShouldPreserveAppPortForwardingPreference(bool isEnabled)
    {
        ISettings settings = Substitute.For<ISettings>();
        IEntityMapper entityMapper = Substitute.For<IEntityMapper>();
        settings.IsPortForwardingForAppsEnabled.Returns(isEnabled);

        MainSettingsRequestCreator creator = new(settings, entityMapper);

        MainSettingsIpcEntity guestHoleSettings = creator.CreateForGuestHole();

        Assert.AreEqual(isEnabled, guestHoleSettings.PortForwardingForApps);
        Assert.IsFalse(guestHoleSettings.PortForwarding);
    }

    [TestMethod]
    public void CreateForGuestHole_ShouldPreserveEnabledKillSwitchMode()
    {
        ISettings settings = Substitute.For<ISettings>();
        IEntityMapper entityMapper = Substitute.For<IEntityMapper>();

        settings.IsKillSwitchEnabled.Returns(true);
        settings.KillSwitchMode.Returns(KillSwitchMode.Advanced);
        entityMapper
            .Map<KillSwitchMode, KillSwitchModeIpcEntity>(KillSwitchMode.Advanced)
            .Returns(KillSwitchModeIpcEntity.Hard);

        MainSettingsRequestCreator creator = new(settings, entityMapper);

        MainSettingsIpcEntity guestHoleSettings = creator.CreateForGuestHole();

        Assert.AreEqual(KillSwitchModeIpcEntity.Hard, guestHoleSettings.KillSwitchMode);
    }
}
