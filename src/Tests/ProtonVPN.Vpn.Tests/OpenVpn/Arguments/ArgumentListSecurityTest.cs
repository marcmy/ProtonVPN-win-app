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
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN. If not, see <https://www.gnu.org/licenses/>.
 */


using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Configurations.Contracts.Entities;
using ProtonVPN.Vpn.OpenVpn.Arguments;

namespace ProtonVPN.Vpn.Tests.OpenVpn.Arguments;

[TestClass]
public class ArgumentListSecurityTest
{
    [TestMethod]
    public void ValuesContainingQuotesSpacesOrOptionsRemainSingleArguments()
    {
        const string path = "C:\\VPN data\\odd \" name.ovpn";
        const string host = "server.example --route attacker";
        IOpenVpnConfigurations config = Substitute.For<IOpenVpnConfigurations>();
        config.ConfigPath.Returns(path);
        config.ExitEventName.Returns("exit event");
        config.TlsExportCertFolder.Returns("C:\\cert files");
        config.TlsVerifyExePath.Returns("C:\\verify tools\\verify.exe");
        var arguments = new CommandLineArguments().Add(new BasicArguments(config)).Add(new TlsVerifyArguments(config, host)).ToArgumentList();
        Assert.AreEqual(path, arguments[1]);
        Assert.AreEqual("exit event", arguments[4]);
        Assert.AreEqual(host, arguments[8]);
        Assert.IsFalse(arguments.Contains("--route"));
    }
}
