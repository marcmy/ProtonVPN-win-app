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
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Enums;
using ProtonVPN.Client.Logic.Connection.Contracts.GuestHole;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.GuestHole;
using ProtonVPN.Common.Legacy.Abstract;
using ProtonVPN.Logging.Contracts;

namespace ProtonVPN.Client.Logic.Connection.Tests;

[TestClass]
public class GuestHoleManagerTest
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldSerializeConcurrentGuestHoleUses()
    {
        ILogger logger = Substitute.For<ILogger>();
        IEventMessageSender eventMessageSender = Substitute.For<IEventMessageSender>();
        IGuestHoleConnector guestHoleConnector = Substitute.For<IGuestHoleConnector>();

        int connectCallCount = 0;
        TaskCompletionSource<bool> secondConnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        guestHoleConnector.ConnectToGuestHoleAsync().Returns(_ =>
        {
            if (Interlocked.Increment(ref connectCallCount) == 2)
            {
                secondConnectStarted.TrySetResult(true);
            }

            return Task.CompletedTask;
        });
        guestHoleConnector.DisconnectFromGuestHoleAsync().Returns(Task.CompletedTask);

        GuestHoleManager manager = new(logger, eventMessageSender, guestHoleConnector);
        TaskCompletionSource<bool> firstActionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseFirstAction = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Result?> firstUse = manager.ExecuteAsync<Result>(async () =>
        {
            firstActionStarted.TrySetResult(true);
            await releaseFirstAction.Task;
            return Result.Ok();
        }, CancellationToken.None);

        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Connected));
        await firstActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        using CancellationTokenSource secondUseCancellationTokenSource = new();
        Task<Result?> secondUse = manager.ExecuteAsync<Result>(
            () => Task.FromResult(Result.Ok()),
            secondUseCancellationTokenSource.Token);

        await Task.Delay(100);
        Assert.AreEqual(1, Volatile.Read(ref connectCallCount), "The second Guest Hole use entered before the first released the semaphore.");

        releaseFirstAction.TrySetResult(true);
        Result? firstResult = await firstUse.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNotNull(firstResult);
        Assert.IsTrue(firstResult.Success);

        await secondConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(2, Volatile.Read(ref connectCallCount));

        secondUseCancellationTokenSource.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await secondUse);
        await guestHoleConnector.Received(1).DisconnectFromGuestHoleAsync();
    }
}
