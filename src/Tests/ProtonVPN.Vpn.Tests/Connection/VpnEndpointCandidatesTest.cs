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

using System.Collections.Generic;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.Connection;

namespace ProtonVPN.Vpn.Tests.Connection;

[TestClass]
public class VpnEndpointCandidatesTest
{
    [TestMethod]
    public void NextIp_ShouldSkipHost_WhenAllPreferredProtocolIpsWereAlreadyTried()
    {
        // Arrange
        VpnEndpointCandidates subject = new();
        subject.Set([
            CreateHost("server-1.test", "10.0.0.1", "10.0.0.2"),
            CreateHost("server-2.test", "10.0.0.2", "10.0.0.1"),
            CreateHost("server-3.test", "10.0.0.3", "10.0.0.4")
        ]);

        VpnConfig config = new(new VpnConfigParameters
        {
            VpnProtocol = VpnProtocol.OpenVpnTcp,
            PreferredProtocols = [VpnProtocol.OpenVpnTcp, VpnProtocol.OpenVpnUdp],
            Ports = new Dictionary<VpnProtocol, IReadOnlyCollection<int>>()
        });

        // Act
        VpnEndpoint first = subject.NextIp(config);
        VpnEndpoint second = subject.NextIp(config);
        VpnEndpoint third = subject.NextIp(config);

        // Assert
        first.Server.Ip.Should().Be("10.0.0.1");
        second.Server.Ip.Should().Be("10.0.0.3");
        third.IsEmpty.Should().BeTrue();
    }

    private static VpnHost CreateHost(string name, string ip, string tcpRelayIp)
    {
        return new VpnHost(
            name: name,
            ip: ip,
            label: string.Empty,
            x25519PublicKey: null,
            signature: string.Empty,
            isIpv6Supported: false,
            relayIpByProtocol: new Dictionary<VpnProtocol, string>
            {
                [VpnProtocol.OpenVpnTcp] = tcpRelayIp
            });
    }
}
