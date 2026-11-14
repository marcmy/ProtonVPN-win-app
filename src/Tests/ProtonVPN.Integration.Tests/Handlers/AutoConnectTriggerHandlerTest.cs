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
using ProtonVPN.Client.Logic.Auth.Contracts;
using ProtonVPN.Client.Logic.Auth.Contracts.Models;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Enums;
using ProtonVPN.Client.Logic.Connection.Contracts.GuestHole;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.Contracts.Models.Intents;
using ProtonVPN.Client.Logic.Recents.Contracts;
using ProtonVPN.Client.Logic.Recents.Contracts.Messages;
using ProtonVPN.Client.Logic.Servers.Cache;
using ProtonVPN.Client.Logic.Servers.Contracts.Messages;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.StatisticalEvents.Contracts.Dimensions;

namespace ProtonVPN.Integration.Tests.Handlers;

[TestClass]
public class AutoConnectTriggerHandlerTest
{
    [TestMethod]
    public async Task Receive_WhenGuestHoleIsActive_ShouldNotAutoConnectAsync()
    {
        // Arrange
        bool isGuestHoleActive = true;
        IConnectionManager connectionManager = Substitute.For<IConnectionManager>();
        IRecentConnectionsManager recentConnectionsManager = Substitute.For<IRecentConnectionsManager>();
        ISettings settings = Substitute.For<ISettings>();
        IServersCache serversCache = Substitute.For<IServersCache>();
        IGuestHoleManager guestHoleManager = Substitute.For<IGuestHoleManager>();
        IUserAuthenticator userAuthenticator = Substitute.For<IUserAuthenticator>();

        ConfigureAutoConnectPrerequisites(
            connectionManager,
            settings,
            serversCache,
            guestHoleManager,
            userAuthenticator,
            () => isGuestHoleActive);

        AutoConnectTriggerHandler handler = new(
            connectionManager,
            recentConnectionsManager,
            settings,
            serversCache,
            guestHoleManager,
            userAuthenticator);

        // Act
        SetHandlerReady(handler);

        // Assert
        await connectionManager.DidNotReceive().ConnectAsync(
            Arg.Any<VpnTriggerDimension>(),
            Arg.Any<IConnectionIntent>());
    }

    [TestMethod]
    public async Task Receive_WhenGuestHoleBecomesInactive_ShouldReconsiderAutoConnectAsync()
    {
        // Arrange
        bool isGuestHoleActive = true;
        IConnectionIntent expectedConnectionIntent = ConnectionIntent.Default;
        IConnectionManager connectionManager = Substitute.For<IConnectionManager>();
        IRecentConnectionsManager recentConnectionsManager = Substitute.For<IRecentConnectionsManager>();
        ISettings settings = Substitute.For<ISettings>();
        IServersCache serversCache = Substitute.For<IServersCache>();
        IGuestHoleManager guestHoleManager = Substitute.For<IGuestHoleManager>();
        IUserAuthenticator userAuthenticator = Substitute.For<IUserAuthenticator>();

        ConfigureAutoConnectPrerequisites(
            connectionManager,
            settings,
            serversCache,
            guestHoleManager,
            userAuthenticator,
            () => isGuestHoleActive);
        recentConnectionsManager.GetDefaultConnection().Returns(expectedConnectionIntent);
        connectionManager.ConnectAsync(VpnTriggerDimension.Auto, expectedConnectionIntent).Returns(Task.CompletedTask);

        AutoConnectTriggerHandler handler = new(
            connectionManager,
            recentConnectionsManager,
            settings,
            serversCache,
            guestHoleManager,
            userAuthenticator);
        SetHandlerReady(handler);
        await connectionManager.DidNotReceive().ConnectAsync(
            Arg.Any<VpnTriggerDimension>(),
            Arg.Any<IConnectionIntent>());

        // Act
        isGuestHoleActive = false;
        handler.Receive(new GuestHoleStatusChangedMessage(false));

        // Assert
        recentConnectionsManager.Received(1).GetDefaultConnection();
        await connectionManager.Received(1).ConnectAsync(
            VpnTriggerDimension.Auto,
            expectedConnectionIntent);
    }

    private static void ConfigureAutoConnectPrerequisites(
        IConnectionManager connectionManager,
        ISettings settings,
        IServersCache serversCache,
        IGuestHoleManager guestHoleManager,
        IUserAuthenticator userAuthenticator,
        Func<bool> isGuestHoleActive)
    {
        connectionManager.IsDisconnected.Returns(true);
        settings.IsAutoConnectEnabled.Returns(true);
        settings.ConnectionCertificate.Returns(new ConnectionCertificate
        {
            Pem = "certificate",
            RequestUtcDate = DateTimeOffset.UtcNow.AddMinutes(-1),
            RefreshUtcDate = DateTimeOffset.UtcNow.AddMinutes(30),
            ExpirationUtcDate = DateTimeOffset.UtcNow.AddHours(1),
        });
        serversCache.IsEmpty().Returns(false);
        guestHoleManager.IsActive.Returns(_ => isGuestHoleActive());
        userAuthenticator.IsLoggedIn.Returns(true);
        userAuthenticator.IsAutoLogin.Returns(true);
    }

    private static void SetHandlerReady(AutoConnectTriggerHandler handler)
    {
        handler.Receive(new ServerListChangedMessage());
        handler.Receive(new RecentConnectionsChangedMessage());
        handler.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Disconnected));
    }
}
