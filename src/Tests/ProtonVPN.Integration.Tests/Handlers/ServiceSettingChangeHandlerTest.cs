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
using ProtonVPN.Client.Handlers;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Messages;

namespace ProtonVPN.Integration.Tests.Handlers;

[TestClass]
public class ServiceSettingChangeHandlerTest
{
    [TestMethod]
    [DataRow(nameof(ISettings.SplitTunnelingStandardAppsList))]
    [DataRow(nameof(ISettings.SplitTunnelingInverseAppsList))]
    [DataRow(nameof(ISettings.SplitTunnelingStandardIpAddressesList))]
    [DataRow(nameof(ISettings.SplitTunnelingInverseIpAddressesList))]
    public async Task Receive_WhenSplitTunnelingListSettingChanges_SendsServiceSettingsAsync(string propertyName)
    {
        // Arrange
        IVpnServiceSettingsUpdater vpnServiceSettingsUpdater = Substitute.For<IVpnServiceSettingsUpdater>();
        vpnServiceSettingsUpdater.SendAsync().Returns(Task.CompletedTask);
        ServiceSettingChangeHandler handler = new(vpnServiceSettingsUpdater, Substitute.For<ISettings>());

        // Act
        handler.Receive(new SettingChangedMessage(propertyName, typeof(object), null, null));

        // Assert
        await vpnServiceSettingsUpdater.Received(1).SendAsync();
    }
}
