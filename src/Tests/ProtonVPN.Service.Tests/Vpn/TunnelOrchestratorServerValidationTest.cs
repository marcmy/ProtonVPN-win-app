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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Service.Vpn;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.Connection;
using ProtonVPN.Vpn.NRPT;
using ProtonVPN.Vpn.ServerValidation;

namespace ProtonVPN.Service.Tests.Vpn;

[TestClass]
public class TunnelOrchestratorServerValidationTest
{
    [TestMethod]
    public async Task ConnectAsync_WhenServerValidationFails_ShouldRejectBeforeTunnelSetup()
    {
        IServerValidator serverValidator = Substitute.For<IServerValidator>();
        IWireGuardConnection wireGuardConnection = Substitute.For<IWireGuardConnection>();
        IIPv6Manager ipv6Manager = Substitute.For<IIPv6Manager>();
        INrptWrapper nrptWrapper = Substitute.For<INrptWrapper>();
        VpnEndpoint endpoint = new(default(VpnHost), VpnProtocol.WireGuardUdp);
        VpnConfig config = CreateVpnConfig();
        serverValidator.Validate(endpoint.Server).Returns(VpnError.ServerValidationError);

        TunnelOrchestrator sut = CreateTunnelOrchestrator(serverValidator, wireGuardConnection, ipv6Manager, nrptWrapper);

        VpnError result = await sut.ConnectAsync(endpoint, default, config, CancellationToken.None);

        Assert.AreEqual(VpnError.ServerValidationError, result);
        await wireGuardConnection.DidNotReceiveWithAnyArgs().ConnectAsync(default!, default, default!, default);
        await ipv6Manager.DidNotReceiveWithAnyArgs().HandleIPv6OnConnectAsync(default, default);
        nrptWrapper.DidNotReceiveWithAnyArgs().SetConnectionConfig(default!, default, default);
    }

    [TestMethod]
    public async Task ConnectAsync_WhenServerValidationSucceeds_ShouldContinueToTunnelConnection()
    {
        IServerValidator serverValidator = Substitute.For<IServerValidator>();
        IWireGuardConnection wireGuardConnection = Substitute.For<IWireGuardConnection>();
        IIPv6Manager ipv6Manager = Substitute.For<IIPv6Manager>();
        INrptWrapper nrptWrapper = Substitute.For<INrptWrapper>();
        VpnEndpoint endpoint = new(default(VpnHost), VpnProtocol.WireGuardUdp);
        VpnConfig config = CreateVpnConfig();
        serverValidator.Validate(endpoint.Server).Returns(VpnError.None);
        wireGuardConnection.ConnectAsync(endpoint, default, config, CancellationToken.None).Returns(VpnError.None);
        wireGuardConnection.ObserveStatesAsync(Arg.Any<CancellationToken>()).Returns(EmptyStates());

        TunnelOrchestrator sut = CreateTunnelOrchestrator(serverValidator, wireGuardConnection, ipv6Manager, nrptWrapper);

        VpnError result = await sut.ConnectAsync(endpoint, default, config, CancellationToken.None);

        Assert.AreEqual(VpnError.None, result);
        await wireGuardConnection.Received(1).ConnectAsync(endpoint, default, config, CancellationToken.None);
    }

    private static VpnConfig CreateVpnConfig()
    {
        return new(new VpnConfigParameters
        {
            VpnProtocol = VpnProtocol.WireGuardUdp,
            PreferredProtocols = [],
        });
    }

    private static TunnelOrchestrator CreateTunnelOrchestrator(
        IServerValidator serverValidator,
        IWireGuardConnection wireGuardConnection,
        IIPv6Manager ipv6Manager,
        INrptWrapper nrptWrapper)
    {
        return new(
            Substitute.For<ILogger>(),
            ipv6Manager,
            Substitute.For<IProTunConnection>(),
            wireGuardConnection,
            serverValidator,
            Substitute.For<IOpenVpnConnection>(),
            nrptWrapper);
    }

    private static async IAsyncEnumerable<VpnState> EmptyStates()
    {
        await Task.CompletedTask;
        yield break;
    }
}
