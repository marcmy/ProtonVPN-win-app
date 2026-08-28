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

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Vpn.PortMapping.UdpClients;

namespace ProtonVPN.Vpn.Tests.PortMapping.UdpClients;

[TestClass]
public class UdpClientWrapperTest
{
    private static readonly TimeSpan TEST_TIMEOUT = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task ReceiveAsync_ShouldReturnResponse()
    {
        using UdpClient gateway = CreateGateway();
        using UdpClientWrapper sut = new();
        sut.Start(GetGatewayEndpoint(gateway));

        byte[] query = [1, 2, 3];
        sut.Send(query);
        using CancellationTokenSource cancellationTokenSource = new(TEST_TIMEOUT);
        UdpReceiveResult receivedQuery = await gateway.ReceiveAsync(cancellationTokenSource.Token);
        receivedQuery.Buffer.Should().Equal(query);

        byte[] expectedReply = [4, 5, 6];
        gateway.Send(expectedReply, expectedReply.Length, receivedQuery.RemoteEndPoint);

        byte[] actualReply = await sut.ReceiveAsync(cancellationTokenSource.Token);
        actualReply.Should().Equal(expectedReply);
    }

    [TestMethod]
    public async Task ReceiveAsync_ShouldStopUnderlyingReceiveWhenCancelled()
    {
        using UdpClient gateway = CreateGateway();
        using UdpClientWrapper sut = new();
        await BindClientAsync(sut, gateway);
        using CancellationTokenSource cancellationTokenSource = new();

        Task<byte[]> receiveTask = sut.ReceiveAsync(cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();
        Func<Task> action = async () => await receiveTask;

        await action.Should().ThrowAsync<OperationCanceledException>();
        receiveTask.IsCompleted.Should().BeTrue();
    }

    [TestMethod]
    public async Task ReceiveAsync_CancelledReceiveShouldNotStealRetryResponse()
    {
        using UdpClient gateway = CreateGateway();
        using UdpClientWrapper sut = new();
        await BindClientAsync(sut, gateway);
        using CancellationTokenSource firstAttemptCancellationTokenSource = new();

        Task<byte[]> firstReceive = sut.ReceiveAsync(firstAttemptCancellationTokenSource.Token);
        firstReceive.IsCompleted.Should().BeFalse("the first receive must be genuinely pending before cancellation");

        firstAttemptCancellationTokenSource.Cancel();
        Func<Task> firstAttempt = async () => await firstReceive;
        await firstAttempt.Should().ThrowAsync<OperationCanceledException>();
        firstReceive.IsCompleted.Should().BeTrue("the first receive must be terminal before the retry begins");

        byte[] retryQuery = [7, 8];
        sut.Send(retryQuery);
        using CancellationTokenSource retryCancellationTokenSource = new(TEST_TIMEOUT);
        UdpReceiveResult receivedRetryQuery = await gateway.ReceiveAsync(retryCancellationTokenSource.Token);
        receivedRetryQuery.Buffer.Should().Equal(retryQuery);

        Task<byte[]> secondReceive = sut.ReceiveAsync(retryCancellationTokenSource.Token);
        secondReceive.IsCompleted.Should().BeFalse("no retry response has been sent yet");

        byte[] expectedReply = [9, 10, 11];
        gateway.Send(expectedReply, expectedReply.Length, receivedRetryQuery.RemoteEndPoint);

        byte[] actualReply = await secondReceive;
        actualReply.Should().Equal(expectedReply);
        firstReceive.IsCompleted.Should().BeTrue("a completed first receive cannot consume the retry response");
    }

    [TestMethod]
    public async Task Reset_ShouldTerminatePendingReceiveAndRemainReusable()
    {
        using UdpClient gateway = CreateGateway();
        using UdpClientWrapper sut = new();
        await BindClientAsync(sut, gateway);

        Task<byte[]> pendingReceive = sut.ReceiveAsync(CancellationToken.None);
        sut.Reset();

        Task completedTask = await Task.WhenAny(pendingReceive, Task.Delay(TEST_TIMEOUT));
        completedTask.Should().BeSameAs(pendingReceive, "Reset must close the socket used by the pending receive");
        ObserveCompletedTask(pendingReceive);

        byte[] query = [7];
        sut.Send(query);
        using CancellationTokenSource cancellationTokenSource = new(TEST_TIMEOUT);
        UdpReceiveResult receivedQuery = await gateway.ReceiveAsync(cancellationTokenSource.Token);
        byte[] expectedReply = [8];
        gateway.Send(expectedReply, expectedReply.Length, receivedQuery.RemoteEndPoint);

        byte[] actualReply = await sut.ReceiveAsync(cancellationTokenSource.Token);
        actualReply.Should().Equal(expectedReply);
    }

    [TestMethod]
    public async Task Stop_ShouldNormalizeDisposalTerminatedReceiveToCancellation()
    {
        await AssertDisposalDuringCancellationIsNormalizedAsync(sut => sut.Stop());
    }

    [TestMethod]
    public async Task Dispose_ShouldNormalizeDisposalTerminatedReceiveToCancellation()
    {
        await AssertDisposalDuringCancellationIsNormalizedAsync(sut => sut.Dispose());
    }

    private static async Task AssertDisposalDuringCancellationIsNormalizedAsync(Action<UdpClientWrapper> disposeAction)
    {
        using UdpClient gateway = CreateGateway();
        UdpClientWrapper sut = new();
        try
        {
            await BindClientAsync(sut, gateway);
            using CancellationTokenSource cancellationTokenSource = new();
            QueuedSynchronizationContext synchronizationContext = new();

            Task<byte[]> pendingReceive = StartReceiveWithQueuedContinuation(
                sut,
                cancellationTokenSource.Token,
                synchronizationContext);
            pendingReceive.IsCompleted.Should().BeFalse("the socket receive must be pending before disposal");

            disposeAction(sut);
            await synchronizationContext.WaitForContinuationAsync();
            pendingReceive.IsCompleted.Should().BeFalse(
                "socket disposal has terminated the underlying receive, but the wrapper continuation is intentionally held");

            cancellationTokenSource.Cancel();
            synchronizationContext.RunContinuation();

            Task completedTask = await Task.WhenAny(pendingReceive, Task.Delay(TEST_TIMEOUT));
            completedTask.Should().BeSameAs(pendingReceive, "the normalized cancellation must complete promptly");

            Func<Task> action = async () => await pendingReceive;
            await action.Should().ThrowAsync<OperationCanceledException>();
            pendingReceive.IsCompleted.Should().BeTrue();
        }
        finally
        {
            sut.Dispose();
        }
    }

    private static Task<byte[]> StartReceiveWithQueuedContinuation(
        UdpClientWrapper sut,
        CancellationToken cancellationToken,
        QueuedSynchronizationContext synchronizationContext)
    {
        SynchronizationContext previousSynchronizationContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(synchronizationContext);
            return sut.ReceiveAsync(cancellationToken);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousSynchronizationContext);
        }
    }

    private static UdpClient CreateGateway()
    {
        return new(new IPEndPoint(IPAddress.Loopback, 0));
    }

    private static IPEndPoint GetGatewayEndpoint(UdpClient gateway)
    {
        return (IPEndPoint)gateway.Client.LocalEndPoint!;
    }

    private static async Task BindClientAsync(UdpClientWrapper sut, UdpClient gateway)
    {
        sut.Start(GetGatewayEndpoint(gateway));
        sut.Send([0]);
        using CancellationTokenSource cancellationTokenSource = new(TEST_TIMEOUT);
        await gateway.ReceiveAsync(cancellationTokenSource.Token);
    }

    private static void ObserveCompletedTask(Task<byte[]> task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly TaskCompletionSource<bool> _continuationPosted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SendOrPostCallback _callback;
        private object _state;

        public override void Post(SendOrPostCallback d, object state)
        {
            _callback = d;
            _state = state;
            _continuationPosted.TrySetResult(true);
        }

        public Task WaitForContinuationAsync()
        {
            return _continuationPosted.Task.WaitAsync(TEST_TIMEOUT);
        }

        public void RunContinuation()
        {
            SendOrPostCallback callback = _callback ??
                throw new InvalidOperationException("No receive continuation was posted by socket disposal.");
            callback(_state);
        }
    }
}
