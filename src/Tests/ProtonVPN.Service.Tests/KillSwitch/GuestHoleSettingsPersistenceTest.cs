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
using ProtonVPN.Common.Core.Dns;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Dns;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.Settings;

namespace ProtonVPN.Service.Tests.KillSwitch;

[TestClass]
public class GuestHoleSettingsPersistenceTest
{
    private const string REMOTE_IP = "198.51.100.10";

    [TestMethod]
    [DataRow(KillSwitchModeIpcEntity.Soft)]
    [DataRow(KillSwitchModeIpcEntity.Hard)]
    public void GuestHoleSnapshot_WhenDisconnectKeepsKillSwitchEnabled_ShouldPersistUntilNormalConnection(
        KillSwitchModeIpcEntity killSwitchMode)
    {
        ISettingsFileStorage storage = Substitute.For<ISettingsFileStorage>();
        IpFilter ipFilter = new(Substitute.For<ILogger>());
        ServiceSettings serviceSettings = new(storage, ipFilter);

        MainSettingsIpcEntity normalSettings = CreateSettings(
            killSwitchMode,
            isLocalAreaNetworkAccessEnabled: true,
            DnsBlockModeIpcEntity.Callout);
        MainSettingsIpcEntity guestHoleSettings = CreateSettings(
            killSwitchMode,
            isLocalAreaNetworkAccessEnabled: false,
            DnsBlockModeIpcEntity.Nrpt);

        serviceSettings.Apply(normalSettings);

        IFirewall firewall = Substitute.For<IFirewall>();
        firewall.LeakProtectionEnabled.Returns(true);
        firewall.IsLocalAreaNetworkAccessEnabled.Returns(true);
        INetworkInterfaceProvider networkInterfaceProvider = Substitute.For<INetworkInterfaceProvider>();
        Service.KillSwitch.KillSwitch killSwitch = new(firewall, serviceSettings, networkInterfaceProvider);
        killSwitch.Start();
        serviceSettings.SettingsChanged += (_, settings) => killSwitch.OnServiceSettingsChanged(settings);

        VpnState connectedState = CreateVpnState(VpnStatus.Connected, VpnError.None);
        killSwitch.OnVpnConnected(connectedState);
        firewall.ClearReceivedCalls();

        serviceSettings.Apply(guestHoleSettings);
        killSwitch.OnVpnDisconnected(CreateVpnState(VpnStatus.Disconnected, VpnError.NoneKeepEnabledKillSwitch));

        Assert.IsFalse(serviceSettings.IsLocalAreaNetworkAccessEnabled);
        Assert.AreEqual(DnsBlockMode.Nrpt, serviceSettings.DnsBlockMode);
        storage.Received().Set(guestHoleSettings);
        firewall.Received(2).EnableLeakProtection(Arg.Is<FirewallParams>(parameters =>
            !parameters.IsLocalAreaNetworkAccessEnabled &&
            parameters.DnsBlockMode == DnsBlockMode.Nrpt));

        firewall.ClearReceivedCalls();
        firewall.IsLocalAreaNetworkAccessEnabled.Returns(false);
        serviceSettings.Apply(normalSettings);
        killSwitch.OnVpnConnected(connectedState);

        Assert.IsTrue(serviceSettings.IsLocalAreaNetworkAccessEnabled);
        Assert.AreEqual(DnsBlockMode.Callout, serviceSettings.DnsBlockMode);
        firewall.Received(1).EnableLeakProtection(Arg.Is<FirewallParams>(parameters =>
            parameters.IsLocalAreaNetworkAccessEnabled &&
            parameters.DnsBlockMode == DnsBlockMode.Callout));
    }

    private static MainSettingsIpcEntity CreateSettings(
        KillSwitchModeIpcEntity killSwitchMode,
        bool isLocalAreaNetworkAccessEnabled,
        DnsBlockModeIpcEntity dnsBlockMode)
    {
        return new MainSettingsIpcEntity
        {
            KillSwitchMode = killSwitchMode,
            SplitTunnel = new SplitTunnelSettingsIpcEntity
            {
                Mode = SplitTunnelModeIpcEntity.Disabled,
                AppPaths = [],
                Ips = [],
            },
            IsLocalAreaNetworkAccessEnabled = isLocalAreaNetworkAccessEnabled,
            DnsBlockMode = dnsBlockMode,
            PortForwardingForApps = true,
        };
    }

    private static VpnState CreateVpnState(VpnStatus status, VpnError error)
    {
        return new VpnState(
            status,
            error,
            "203.0.113.1",
            REMOTE_IP,
            443,
            default);
    }
}
