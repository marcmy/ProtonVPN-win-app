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

using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts;
using ProtonVPN.OperatingSystems.Network.Contracts.Routing;
using ProtonVPN.Vpn.Gateways;
using ProtonVPN.Vpn.SplitTunnel;

namespace ProtonVPN.Vpn.Tests.SplitTunnel;

[TestClass]
public class SplitTunnelRoutingTest
{
    private ILogger _logger = null!;
    private IStaticConfiguration _config = null!;
    private IGatewayCache _gatewayCache = null!;
    private IIpv4GatewayResolver _ipv4GatewayResolver = null!;
    private IRoutingTableHelper _routingTableHelper = null!;
    private INetworkUtilities _networkUtilities = null!;
    private ISystemNetworkInterfaces _networkInterfaces = null!;
    private INetworkInterfaceProvider _networkInterfaceProvider = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _logger = Substitute.For<ILogger>();
        _config = Substitute.For<IStaticConfiguration>();
        _gatewayCache = Substitute.For<IGatewayCache>();
        _ipv4GatewayResolver = Substitute.For<IIpv4GatewayResolver>();
        _routingTableHelper = Substitute.For<IRoutingTableHelper>();
        _networkUtilities = Substitute.For<INetworkUtilities>();
        _networkInterfaces = Substitute.For<ISystemNetworkInterfaces>();
        _networkInterfaceProvider = Substitute.For<INetworkInterfaceProvider>();
    }

    [TestMethod]
    public void SetUpRoutingTable_BlockMode_WhenGatewayResolverFails_ShouldUsePhysicalGatewayFallback()
    {
        // Arrange
        const string excludedHardwareId = "vpn-hardware-id";
        const uint physicalInterfaceIndex = 12;
        IPAddress physicalGateway = IPAddress.Parse("192.168.1.1");
        IPAddress excludedAddress = IPAddress.Parse("1.1.1.1");
        OpenVpnAdapter openVpnAdapter = default;

        INetworkInterface physicalInterface = Substitute.For<INetworkInterface>();
        physicalInterface.Index.Returns(physicalInterfaceIndex);
        physicalInterface.DefaultGateway.Returns(physicalGateway);

        INetworkInterface tunnelInterface = Substitute.For<INetworkInterface>();
        tunnelInterface.Index.Returns(99u);

        _config.GetHardwareId(VpnProtocol.WireGuard, openVpnAdapter).Returns(excludedHardwareId);
        _networkInterfaces.GetBestInterfaceExcludingHardwareId(excludedHardwareId).Returns(physicalInterface);
        _networkInterfaces.GetInterfaces().Returns([physicalInterface, tunnelInterface]);
        _networkInterfaceProvider.GetByVpnProtocol(VpnProtocol.WireGuard, openVpnAdapter).Returns(tunnelInterface);

        VpnConfig vpnConfig = new(new VpnConfigParameters
        {
            SplitTunnelMode = SplitTunnelMode.Block,
            SplitTunnelIPs = [excludedAddress.ToString()],
            VpnProtocol = VpnProtocol.WireGuard,
            OpenVpnAdapter = openVpnAdapter,
            PreferredProtocols = [],
        });

        SplitTunnelRouting sut = new(
            _logger,
            _config,
            _gatewayCache,
            _ipv4GatewayResolver,
            _routingTableHelper,
            _networkUtilities,
            _networkInterfaces,
            _networkInterfaceProvider);

        // Act
        sut.SetUpRoutingTable(vpnConfig, "10.2.0.2", isIpv6Supported: false);

        // Assert
        _routingTableHelper.Received(1).CreateRoute(Arg.Is<RouteConfiguration>(route =>
            route.Destination.Ip.Equals(excludedAddress) &&
            route.Gateway != null &&
            route.Gateway.Ip.Equals(physicalGateway) &&
            route.InterfaceIndex == physicalInterfaceIndex &&
            route.Metric == 1 &&
            !route.IsIpv6));
    }
}
