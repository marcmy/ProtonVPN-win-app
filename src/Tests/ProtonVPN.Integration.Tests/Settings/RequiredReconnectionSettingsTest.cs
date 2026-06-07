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
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.RequiredReconnections;

namespace ProtonVPN.Integration.Tests.Settings;

[TestClass]
public class RequiredReconnectionSettingsTest
{
    [TestMethod]
    [DataRow(nameof(ISettings.SplitTunnelingStandardAppsList))]
    [DataRow(nameof(ISettings.SplitTunnelingInverseAppsList))]
    [DataRow(nameof(ISettings.SplitTunnelingStandardIpAddressesList))]
    [DataRow(nameof(ISettings.SplitTunnelingInverseIpAddressesList))]
    public void IsReconnectionRequired_WhenSplitTunnelingListChanges_ReturnsFalse(string settingName)
    {
        // Arrange
        ISettings settings = Substitute.For<ISettings>();
        settings.IsSplitTunnelingEnabled.Returns(true);
        RequiredReconnectionSettings requiredReconnectionSettings = new(
            Substitute.For<IConnectionManager>(),
            settings);

        // Act
        bool result = requiredReconnectionSettings.IsReconnectionRequired(settingName);

        // Assert
        Assert.IsFalse(result);
    }
}
