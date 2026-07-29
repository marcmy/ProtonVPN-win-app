/*
 * Copyright (c) 2025 Proton AG
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
using System.Linq;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.NetworkFilter;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.ProTun.Contracts.Adapters;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.Settings;
using ProtonVPN.Service.SplitTunneling;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.SplitTunnel;

namespace ProtonVPN.Service.Tests.SplitTunneling;

[TestClass]
public class SplitTunnelTest
{
    private ILogger _logger;
    private ISplitTunnelRouting _splitTunnelRouting;
    private INetworkUtilities _networkUtilities;
    private ISystemNetworkInterfaces _networkInterfaces;
    private IConfiguration _config;
    private IServiceSettings _serviceSettings;
    private ISplitTunnelClient _splitTunnelClient;
    private IAppFilter _appFilter;
    private IPermittedRemoteAddress _permittedRemoteAddress;
    private IAdapterDetailsCache _proTunAdapterDetailsCache;

    [TestInitialize]
    public void TestInitialize()
    {
        _logger = Substitute.For<ILogger>();
        _splitTunnelRouting = Substitute.For<ISplitTunnelRouting>();
        _networkUtilities = Substitute.For<INetworkUtilities>();
        _networkInterfaces = Substitute.For<ISystemNetworkInterfaces>();
        _config = Substitute.For<IConfiguration>();
        _serviceSettings = Substitute.For<IServiceSettings>();
        _splitTunnelClient = Substitute.For<ISplitTunnelClient>();
        _appFilter = Substitute.For<IAppFilter>();
        _permittedRemoteAddress = Substitute.For<IPermittedRemoteAddress>();
        _proTunAdapterDetailsCache = Substitute.For<IAdapterDetailsCache>();
    }

    [TestMethod]
    public void OnVpnConnecting_WhenBlockMode_DisableReversed()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block
        });
        SplitTunnel splitTunnel = GetSplitTunnel(false, true);

        // Act
        splitTunnel.OnVpnConnecting(GetConnectingVpnState());

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnConnecting_WhenBlockMode_Disable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel(true);

        // Act
        splitTunnel.OnVpnConnecting(GetConnectingVpnState());

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnConnected_PermitRemoteAddressesOnBlockMode()
    {
        // Arrange
        string[] addresses = ["127.0.0.1", "192.168.0.1", "8.8.8.8"];
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            Ips = addresses,
            AppPaths = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _permittedRemoteAddress.Received(1).Add(
            Arg.Is<string[]>(actual => actual.SequenceEqual(addresses)),
            NetworkFilter.Action.HardPermit);
    }

    [TestMethod]
    public void OnVpnConnected_WhenBlockMode_CallEnable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _splitTunnelClient.Received(1).EnableExcludeMode(Arg.Any<string[]>(), Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
    }

    [TestMethod]
    public void OnVpnConnected_WhenBlockMode_CalloutDriverStart()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());
    }

    [TestMethod]
    public void OnVpnConnected_WhenPermitMode_CalloutDriverStart()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());
    }

    [TestMethod]
    public void OnVpnConnected_WhenDisabled_CalloutDriverDoNotStart()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Disabled
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());
    }

    [TestMethod]
    public void OnVpnConnected_WhenDisabled_DoNotEnable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Disabled
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _splitTunnelClient.Received(0);
    }

    [TestMethod]
    public void OnVpnConnected_WhenPermitMode_EnableReversed()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _splitTunnelClient
            .Received(1)
            .EnableIncludeMode(Arg.Any<string[]>(), Arg.Any<IPAddress>(), Arg.Any<IPAddress>());
    }

    [TestMethod]
    public void OnVpnConnected_PermitAppsOnBlockMode()
    {
        // Arrange
        string[] apps = ["app1", "app2", "app3"];
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = apps,
            Ips = [],
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Assert
        _appFilter.Received(1).Add(apps, Arg.Any<Tuple<Layer, NetworkFilter.Action>[]>());
    }

    [TestMethod]
    public void OnVpnConnecting_ShouldBlockApps_WhenModeIsPermit()
    {
        // Arrange
        string[] apps = ["app1", "app2", "app3"];
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit,
            AppPaths = apps
        });
        SplitTunnel splitTunnel = GetSplitTunnel(true);

        // Act
        splitTunnel.OnVpnConnecting(GetConnectingVpnState());

        // Assert
        _appFilter.Received(1).Add(apps, Arg.Any<Tuple<Layer, NetworkFilter.Action>[]>());
    }

    [TestMethod]
    public void OnVpnDisconnected_ManualDisconnect_ShouldDisable()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Block
        });
        SplitTunnel splitTunnel = GetSplitTunnel(true);

        // Act
        splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnDisconnected_ManualDisconnect_ShouldDisableReversed()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel(false, true);

        // Act
        splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));

        // Assert
        _splitTunnelClient.Received(1).Disable();
    }

    [TestMethod]
    public void OnVpnDisconnected_ManualDisconnect_ShouldStopCalloutDriver()
    {
        // Arrange
        _serviceSettings.SplitTunnelSettings.Returns(new SplitTunnelSettingsIpcEntity
        {
            Mode = SplitTunnelModeIpcEntity.Permit
        });
        SplitTunnel splitTunnel = GetSplitTunnel();

        // Act
        splitTunnel.OnVpnDisconnected(GetDisconnectedVpnState(true));
    }

    [TestMethod]
    public void OnServiceSettingsChanged_WhenConnectedInBlockMode_ReappliesSplitTunnelSettings()
    {
        // Arrange
        string[] initialApps = ["initial-app.exe"];
        string[] updatedApps = ["updated-app.exe"];
        SplitTunnelSettingsIpcEntity splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = initialApps,
            Ips = [],
        };
        _serviceSettings.SplitTunnelSettings.Returns(_ => splitTunnelSettings);
        SplitTunnel splitTunnel = GetSplitTunnel();
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Act
        splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = updatedApps,
            Ips = [],
        };
        ((IServiceSettingsAware)splitTunnel).OnServiceSettingsChanged(new MainSettingsIpcEntity());

        // Assert
        _splitTunnelClient.Received(1).EnableExcludeMode(
            Arg.Is<string[]>(paths => paths.SequenceEqual(updatedApps)),
            Arg.Any<IPAddress>(),
            Arg.Any<IPAddress>());
    }

    [TestMethod]
    public void OnServiceSettingsChanged_WhenConnected_ReplacesRoutesWithCurrentAddresses()
    {
        // Arrange
        string[] initialAddresses = ["192.0.2.10"];
        string[] updatedAddresses = ["198.51.100.20"];
        SplitTunnelSettingsIpcEntity splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = initialAddresses,
        };
        _serviceSettings.SplitTunnelSettings.Returns(_ => splitTunnelSettings);

        SplitTunnel splitTunnel = GetSplitTunnel();
        splitTunnel.UpdateContext(CreateSplitTunnelContext(initialAddresses));
        splitTunnel.OnVpnConnected(GetConnectedVpnState());

        // Act
        splitTunnelSettings = new()
        {
            Mode = SplitTunnelModeIpcEntity.Block,
            AppPaths = [],
            Ips = updatedAddresses,
        };
        ((IServiceSettingsAware)splitTunnel).OnServiceSettingsChanged(new MainSettingsIpcEntity());

        // Assert
        _splitTunnelRouting.Received(1).DeleteRoutes(
            Arg.Is<VpnConfig>(config => config.SplitTunnelIPs.SequenceEqual(initialAddresses)));
        _splitTunnelRouting.Received(1).SetUpRoutingTable(
            Arg.Is<VpnConfig>(config => config.SplitTunnelIPs.SequenceEqual(updatedAddresses)),
            "1.1.1.1",
            Arg.Any<bool>());
    }

    private SplitTunnel GetSplitTunnel(bool enabled = false, bool reverseEnabled = false)
    {
        return new SplitTunnel(
            enabled,
            reverseEnabled,
            _logger,
            _splitTunnelRouting,
            _networkUtilities,
            _networkInterfaces,
            _config,
            _serviceSettings,
            _splitTunnelClient,
            _appFilter,
            _permittedRemoteAddress,
            _proTunAdapterDetailsCache);
    }

    private VpnState GetConnectedVpnState()
    {
        return new VpnState(
            VpnStatus.Connected,
            VpnError.None,
            "1.1.1.1",
            "2.2.2.2",
            443,
            VpnProtocol.Smart);
    }

    private VpnState GetDisconnectedVpnState(bool manualDisconnect = false)
    {
        return new VpnState(
            VpnStatus.Disconnected,
            manualDisconnect ? VpnError.None : VpnError.Unknown,
            "1.1.1.1",
            "2.2.2.2",
            443,
            VpnProtocol.Smart);
    }

    private VpnState GetConnectingVpnState()
    {
        return new VpnState(
            VpnStatus.Disconnected,
            VpnError.None,
            "1.1.1.1",
            "2.2.2.2",
            443,
            VpnProtocol.Smart);
    }

    private static SplitTunnelContext CreateSplitTunnelContext(string[] addresses)
    {
        VpnConfig config = new(new VpnConfigParameters
        {
            SplitTunnelMode = SplitTunnelMode.Block,
            SplitTunnelIPs = addresses,
            VpnProtocol = VpnProtocol.WireGuardUdp,
            PreferredProtocols = [],
        });
        VpnHost server = new(
            "test.protonvpn.net",
            "203.0.113.1",
            string.Empty,
            default,
            string.Empty,
            true,
            null);

        return new SplitTunnelContext(config, new VpnEndpoint(server, VpnProtocol.WireGuardUdp, 443));
    }
}
