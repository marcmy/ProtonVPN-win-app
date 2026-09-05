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
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Crypto.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.ProTun.Contracts;
using ProtonVPN.ProTun.Contracts.Adapters;
using ProtonVPN.ProTun.Contracts.ConnectionArguments;
using ProtonVPN.ProTun.Contracts.Traffic;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.Gateways;
using ProtonVPN.Vpn.ProTun;

namespace ProtonVPN.Vpn.Tests.ProTun;

[TestClass]
public class ProTunConnectionTest
{
    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public async Task ConnectAsync_ShouldGateIpv6ByUserSettingAndServerCapability(
        bool isIpv6Enabled,
        bool isServerIpv6Supported,
        bool expectedIsIpv6Enabled)
    {
        Channel<VpnState> stateChannel = Channel.CreateUnbounded<VpnState>();
        Channel<NetworkTraffic> trafficChannel = Channel.CreateUnbounded<NetworkTraffic>();
        IProTunManager proTunManager = Substitute.For<IProTunManager>();
        IProTunTrafficManager trafficManager = Substitute.For<IProTunTrafficManager>();
        IX25519KeyGenerator x25519KeyGenerator = Substitute.For<IX25519KeyGenerator>();
        IAdapterDetailsCache adapterDetailsCache = Substitute.For<IAdapterDetailsCache>();
        ConnectionArgs capturedArgs = null;

        proTunManager.StateChannel.Returns(stateChannel);
        proTunManager.TrafficChannel.Returns(trafficChannel);
        proTunManager
            .ConnectAsync(Arg.Do<ConnectionArgs>(args => capturedArgs = args), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stateChannel.Writer.TryWrite(new VpnState(VpnStatus.Connected, VpnProtocol.ProTunUdp));
                return Task.CompletedTask;
            });
        trafficManager.StartAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        x25519KeyGenerator
            .FromEd25519SecretKey(Arg.Any<SecretKey>())
            .Returns(new SecretKey([1, 2, 3], KeyAlgorithm.X25519));

        ProTunConnection sut = new(
            Substitute.For<ILogger>(),
            Substitute.For<IGatewayCache>(),
            proTunManager,
            trafficManager,
            x25519KeyGenerator,
            adapterDetailsCache);

        VpnConfig config = new(new VpnConfigParameters
        {
            VpnProtocol = VpnProtocol.ProTunUdp,
            PreferredProtocols = [],
            IsIpv6Enabled = isIpv6Enabled,
        });
        VpnEndpoint endpoint = new(CreateServer(isServerIpv6Supported), VpnProtocol.ProTunUdp);
        VpnCredentials credentials = new(CreateClientKeyPair());
        using CancellationTokenSource cts = new();

        VpnError result = await sut.ConnectAsync(endpoint, credentials, config, cts.Token);
        cts.Cancel();

        Assert.AreEqual(VpnError.None, result);
        Assert.IsNotNull(capturedArgs);
        Assert.AreEqual(expectedIsIpv6Enabled, capturedArgs.IsIpv6Enabled);
    }

    private static VpnHost CreateServer(bool isIpv6Supported)
    {
        return new VpnHost(
            "server.example.com",
            "203.0.113.10",
            "entry",
            new PublicKey([4, 5, 6], KeyAlgorithm.X25519),
            "signature",
            isIpv6Supported,
            new Dictionary<VpnProtocol, string>());
    }

    private static AsymmetricKeyPair CreateClientKeyPair()
    {
        return new AsymmetricKeyPair(
            new SecretKey([7, 8, 9], KeyAlgorithm.Ed25519),
            new PublicKey([10, 11, 12], KeyAlgorithm.Ed25519));
    }
}
