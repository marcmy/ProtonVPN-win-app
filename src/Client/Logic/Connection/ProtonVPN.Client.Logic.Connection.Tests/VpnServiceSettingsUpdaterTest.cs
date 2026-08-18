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

using NSubstitute;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Models.Intents;
using ProtonVPN.Client.Logic.Connection.Contracts.RequestCreators;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;

namespace ProtonVPN.Client.Logic.Connection.Tests;

[TestClass]
public class VpnServiceSettingsUpdaterTest
{
    [TestMethod]
    public async Task SendAsync_WhenCallsOverlap_SerializesAndBuildsLatestSnapshotAfterPreviousSendCompletes()
    {
        // Arrange
        IVpnServiceCaller vpnServiceCaller = Substitute.For<IVpnServiceCaller>();
        IMainSettingsRequestCreator mainSettingsRequestCreator = Substitute.For<IMainSettingsRequestCreator>();
        IConnectionManager connectionManager = Substitute.For<IConnectionManager>();
        IConnectionIntent connectionIntent = Substitute.For<IConnectionIntent>();

        connectionManager.CurrentConnectionIntent.Returns(connectionIntent);

        MainSettingsIpcEntity firstSettings = new();
        MainSettingsIpcEntity secondSettings = new();
        int createCount = 0;
        mainSettingsRequestCreator.Create(connectionIntent).Returns(_ =>
            Interlocked.Increment(ref createCount) == 1 ? firstSettings : secondSettings);

        TaskCompletionSource<bool> firstApplyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseFirstApply = new(TaskCreationOptions.RunContinuationsAsynchronously);

        vpnServiceCaller.ApplySettingsAsync(Arg.Any<MainSettingsIpcEntity>()).Returns(callInfo =>
        {
            MainSettingsIpcEntity settings = callInfo.Arg<MainSettingsIpcEntity>();
            if (ReferenceEquals(settings, firstSettings))
            {
                firstApplyStarted.TrySetResult(true);
                return releaseFirstApply.Task;
            }

            return Task.CompletedTask;
        });

        VpnServiceSettingsUpdater updater = new(
            vpnServiceCaller,
            mainSettingsRequestCreator,
            connectionManager);

        // Act
        Task firstSend = updater.SendAsync();
        await firstApplyStarted.Task;
        Task secondSend = updater.SendAsync();

        // Assert: the second request must not snapshot settings while the first RPC is in flight.
        Assert.AreEqual(1, Volatile.Read(ref createCount));
        await vpnServiceCaller.Received(1).ApplySettingsAsync(firstSettings);

        releaseFirstApply.SetResult(true);
        await Task.WhenAll(firstSend, secondSend);

        Assert.AreEqual(2, Volatile.Read(ref createCount));
        await vpnServiceCaller.Received(1).ApplySettingsAsync(secondSettings);
    }
}
