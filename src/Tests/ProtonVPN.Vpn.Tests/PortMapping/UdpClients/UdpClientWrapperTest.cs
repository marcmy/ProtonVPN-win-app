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
    public async Task Stop_ShouldNormalizeDisposedReceiveToCancellationWhenOperationIsCancelled()
    {
        using UdpClient gateway = CreateGateway();
        UdpClientWrapper sut = new();
        await BindClientAsync(sut, gateway);
        using CancellationTokenSource cancellationTokenSource = new();

        Task<byte[]> pendingReceive = sut.ReceiveAsync(cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();
        sut.Stop();
        Func<Task> action = async () => await pendingReceive;

        await action.Should().ThrowAsync<OperationCanceledException>();
        pendingReceive.IsCompleted.Should().BeTrue();
        sut.Dispose();
    }

    [TestMethod]
    public async Task Dispose_ShouldNormalizeDisposedReceiveToCancellationWhenOperationIsCancelled()
    {
        using UdpClient gateway = CreateGateway();
        UdpClientWrapper sut = new();
        await BindClientAsync(sut, gateway);
        using CancellationTokenSource cancellationTokenSource = new();

        Task<byte[]> pendingReceive = sut.ReceiveAsync(cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();
        sut.Dispose();
        Func<Task> action = async () => await pendingReceive;

        await action.Should().ThrowAsync<OperationCanceledException>();
        pendingReceive.IsCompleted.Should().BeTrue();
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
}