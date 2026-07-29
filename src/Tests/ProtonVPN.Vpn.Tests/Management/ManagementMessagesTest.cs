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

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Vpn.Management;

namespace ProtonVPN.Vpn.Tests.Management;

[TestClass]
public class ManagementMessagesTest
{
    private readonly ManagementMessages _sut = new();

    [TestMethod]
    public void Username_ShouldKeepSecretInCommandAndRedactItFromLog()
    {
        const string secret = "user name";

        ManagementMessage message = _sut.Username(secret);

        message.ToString().Should().Contain("user\\ name");
        message.LogText.Should().Be("username 'Auth' [...]");
        message.LogText.Should().NotContain(secret);
    }

    [TestMethod]
    public void Password_ShouldKeepSecretInCommandAndRedactItFromLog()
    {
        const string secret = "super secret";

        ManagementMessage message = _sut.Password(secret);

        message.ToString().Should().Contain("super\\ secret");
        message.LogText.Should().Be("password 'Auth' [...]");
        message.LogText.Should().NotContain(secret);
    }
}
