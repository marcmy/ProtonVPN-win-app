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
using ProtonVPN.Vpn.ServerValidation;

namespace ProtonVPN.Service.Tests.Vpn;

[TestClass]
public class ConnectionProbeTest
{
    [TestMethod]
    public async Task SelectEndpointAsync_ShouldSkipServerThatFailsValidation()
    {
        ILogger logger = Substitute.For<ILogger>();
        IEndpointScanner endpointScanner = Substitute.For<IEndpointScanner>();
        IServerValidator serverValidator = Substitute.For<IServerValidator>();
        IVpnEndpointCandidates candidates = Substitute.For<IVpnEndpointCandidates>();

        VpnHost invalidServer = CreateServer("invalid.example.com", "192.0.2.10");
        VpnHost validServer = CreateServer("valid.example.com", "192.0.2.20");
        VpnEndpoint invalidEndpoint = new(invalidServer, VpnProtocol.WireGuardTls);
        VpnEndpoint validEndpoint = new(validServer, VpnProtocol.WireGuardTls);
        VpnEndpoint selectedEndpoint = new(validServer, VpnProtocol.WireGuardTls, 443);
        VpnConfig config = new(new VpnConfigParameters
        {
            PreferredProtocols = [VpnProtocol.WireGuardTls],
        });

        candidates.NextIp(config).Returns(invalidEndpoint, validEndpoint, VpnEndpoint.Empty);
        serverValidator.Validate(invalidServer).Returns(VpnError.ServerValidationError);
        serverValidator.Validate(validServer).Returns(VpnError.None);
        endpointScanner.ScanForBestEndpointAsync(
                validEndpoint,
                config.Ports,
                Arg.Any<IList<VpnProtocol>>(),
                CancellationToken.None)
            .Returns(selectedEndpoint);

        ConnectionProbe subject = new(logger, endpointScanner, serverValidator);

        VpnEndpoint result = await subject.SelectEndpointAsync(candidates, config, CancellationToken.None);

        Assert.AreSame(selectedEndpoint, result);
        await endpointScanner.DidNotReceive().ScanForBestEndpointAsync(
            invalidEndpoint,
            config.Ports,
            Arg.Any<IList<VpnProtocol>>(),
            CancellationToken.None);
        await endpointScanner.Received(1).ScanForBestEndpointAsync(
            validEndpoint,
            config.Ports,
            Arg.Any<IList<VpnProtocol>>(),
            CancellationToken.None);
    }

    private static VpnHost CreateServer(string name, string ip)
    {
        return new VpnHost(
            name,
            ip,
            string.Empty,
            default,
            string.Empty,
            false,
            new Dictionary<VpnProtocol, string>());
    }
}
