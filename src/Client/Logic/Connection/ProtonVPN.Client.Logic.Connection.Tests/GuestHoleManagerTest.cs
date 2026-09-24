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
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Client.Logic.Connection.Tests;

[TestClass]
public class GuestHoleManagerTest
{
    [TestMethod]
    public async Task ExecuteAsync_ShouldIgnoreRawDisconnectedStateBeforeGuestHoleConnects()
    {
        ILogger logger = Substitute.For<ILogger>();
        IEventMessageSender eventMessageSender = Substitute.For<IEventMessageSender>();
        IGuestHoleConnector guestHoleConnector = Substitute.For<IGuestHoleConnector>();

        TaskCompletionSource<bool> disconnectRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        guestHoleConnector.ConnectToGuestHoleAsync().Returns(Task.CompletedTask);
        guestHoleConnector.DisconnectFromGuestHoleAsync().Returns(_ =>
        {
            disconnectRequested.TrySetResult(true);
            return Task.CompletedTask;
        });

        GuestHoleManager manager = new(logger, eventMessageSender, guestHoleConnector);
        Task<Result?> use = manager.ExecuteAsync<Result>(
            () => Task.FromResult<Result>(null!),
            CancellationToken.None);

        manager.Receive(CreateRawDisconnectedState());
        await Task.Delay(100);

        Assert.IsTrue(manager.IsActive, "The ordinary tunnel teardown was mistaken for Guest Hole teardown.");
        Assert.IsFalse(use.IsCompleted);

        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Connected));
        await disconnectRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
        manager.Receive(CreateRawDisconnectedState());

        Result? result = await use.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNull(result);
        Assert.IsFalse(manager.IsActive);
    }

    [TestMethod]
    public async Task ExecuteAsync_ShouldCompleteRequestedDisconnectFromRawServiceDisconnectedState()
    {
        ILogger logger = Substitute.For<ILogger>();
        IEventMessageSender eventMessageSender = Substitute.For<IEventMessageSender>();
        IGuestHoleConnector guestHoleConnector = Substitute.For<IGuestHoleConnector>();

        TaskCompletionSource<bool> disconnectRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        guestHoleConnector.ConnectToGuestHoleAsync().Returns(Task.CompletedTask);
        guestHoleConnector.DisconnectFromGuestHoleAsync().Returns(_ =>
        {
            disconnectRequested.TrySetResult(true);
            return Task.CompletedTask;
        });

        GuestHoleManager manager = new(logger, eventMessageSender, guestHoleConnector);
        Task<Result?> use = manager.ExecuteAsync<Result>(
            () => Task.FromResult<Result>(null!),
            CancellationToken.None);

        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Connected));
        await disconnectRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));

        manager.Receive(CreateRawDisconnectedState());

        Result? result = await use.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNull(result);
        Assert.IsFalse(manager.IsActive);
        await guestHoleConnector.Received(1).DisconnectFromGuestHoleAsync();
    }

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
        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Disconnected));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await secondUse);
        await guestHoleConnector.Received(1).DisconnectFromGuestHoleAsync();
    }

    [TestMethod]
    public async Task ExecuteAsync_WhenPreviousUseRequestedDisconnect_ShouldNotStartNextUseBeforeDisconnectedState()
    {
        ILogger logger = Substitute.For<ILogger>();
        IEventMessageSender eventMessageSender = Substitute.For<IEventMessageSender>();
        IGuestHoleConnector guestHoleConnector = Substitute.For<IGuestHoleConnector>();

        int connectCallCount = 0;
        TaskCompletionSource<bool> disconnectRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> secondActionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseSecondAction = new(TaskCreationOptions.RunContinuationsAsynchronously);
        guestHoleConnector.ConnectToGuestHoleAsync().Returns(_ =>
        {
            Interlocked.Increment(ref connectCallCount);
            return Task.CompletedTask;
        });
        guestHoleConnector.DisconnectFromGuestHoleAsync().Returns(_ =>
        {
            disconnectRequested.TrySetResult(true);
            return Task.CompletedTask;
        });

        GuestHoleManager manager = new(logger, eventMessageSender, guestHoleConnector);
        Task<Result?> firstUse = manager.ExecuteAsync<Result>(async () =>
        {
            await manager.DisconnectAsync();
            return Result.Ok();
        }, CancellationToken.None);

        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Connected));
        await disconnectRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));

        using CancellationTokenSource secondUseCancellationTokenSource = new();
        Task<Result?> secondUse = manager.ExecuteAsync<Result>(async () =>
        {
            secondActionStarted.TrySetResult(true);
            await releaseSecondAction.Task;
            return Result.Ok();
        }, secondUseCancellationTokenSource.Token);

        Assert.AreEqual(
            1,
            Volatile.Read(ref connectCallCount),
            "A second Guest Hole connection started before the first disconnect reached the Disconnected state.");

        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Disconnected));

        Result? firstResult = await firstUse.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNotNull(firstResult);
        Assert.IsTrue(firstResult.Success);
        Assert.IsTrue(SpinWait.SpinUntil(
            () => Volatile.Read(ref connectCallCount) == 2,
            TimeSpan.FromSeconds(3)),
            "The second Guest Hole connection did not start after the first reached Disconnected.");

        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Connected));
        await secondActionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        secondUseCancellationTokenSource.Cancel();
        manager.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Disconnected));
        releaseSecondAction.TrySetResult(true);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await secondUse);
    }

    private static VpnStateIpcEntity CreateRawDisconnectedState()
    {
        return new()
        {
            Status = VpnStatusIpcEntity.Disconnected,
            Error = VpnErrorTypeIpcEntity.NoneKeepEnabledKillSwitch,
        };
    }
}
