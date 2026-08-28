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
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.IssueReporting.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Vpn.Gateways;
using ProtonVPN.Vpn.PortMapping.Serializers.Common;
using ProtonVPN.Vpn.PortMapping.UdpClients;

namespace ProtonVPN.Vpn.Tests.PortMapping;

[TestClass]
public class PortMappingProtocolClientReceiveTest
{
    private IUdpClientWrapper _udpClientWrapper;
    private PortMappingProtocolClient _sut;

    [TestInitialize]
    public void TestInitialize()
    {
        _udpClientWrapper = Substitute.For<IUdpClientWrapper>();
        _sut = new(
            Substitute.For<ILogger>(),
            _udpClientWrapper,
            Substitute.For<IMessageSerializerProxy>(),
            Substitute.For<IGatewayCache>(),
            Substitute.For<IIssueReporter>());
    }

    [TestMethod]
    public async Task GetReplyOrTimeoutAsync_ShouldReturnSuccessfulResponse()
    {
        byte[] expected = [1, 2, 3, 4];
        CancellationToken? receivedToken = null;
        _udpClientWrapper
            .ReceiveAsync(Arg.Do<CancellationToken>(token => receivedToken = token))
            .Returns(Task.FromResult(expected));

        byte[] actual = await InvokeGetReplyOrTimeoutAsync(1000, CancellationToken.None);

        actual.Should().Equal(expected);
        receivedToken.Should().NotBeNull();
        receivedToken.Value.CanBeCanceled.Should().BeTrue();
    }

    [TestMethod]
    public async Task GetReplyOrTimeoutAsync_ShouldCancelReceiveAndThrowTimeoutException()
    {
        CancellationToken? receivedToken = null;
        int activeReceives = 0;
        _udpClientWrapper
            .ReceiveAsync(Arg.Do<CancellationToken>(token => receivedToken = token))
            .Returns(callInfo => WaitUntilCancelledAsync(callInfo.Arg<CancellationToken>(),
                () => Interlocked.Increment(ref activeReceives),
                () => Interlocked.Decrement(ref activeReceives)));

        Func<Task> action = async () => await InvokeGetReplyOrTimeoutAsync(25, CancellationToken.None);

        await action.Should().ThrowAsync<TimeoutException>();
        receivedToken.Should().NotBeNull();
        receivedToken.Value.IsCancellationRequested.Should().BeTrue();
        activeReceives.Should().Be(0);
    }

    [TestMethod]
    public async Task GetReplyOrTimeoutAsync_ShouldPreserveCallerCancellation()
    {
        _udpClientWrapper
            .ReceiveAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => WaitUntilCancelledAsync(callInfo.Arg<CancellationToken>()));
        using CancellationTokenSource cancellationTokenSource = new();

        Task<byte[]> receiveTask = InvokeGetReplyOrTimeoutAsync(5000, cancellationTokenSource.Token);
        cancellationTokenSource.Cancel();
        Func<Task> action = async () => await receiveTask;

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public async Task GetReplyOrTimeoutAsync_ShouldAllowCleanRetryAfterTimeout()
    {
        byte[] expected = [9, 8, 7];
        int receiveCount = 0;
        int activeReceives = 0;
        _udpClientWrapper
            .ReceiveAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                int currentReceive = Interlocked.Increment(ref receiveCount);
                return currentReceive == 1
                    ? WaitUntilCancelledAsync(callInfo.Arg<CancellationToken>(),
                        () => Interlocked.Increment(ref activeReceives),
                        () => Interlocked.Decrement(ref activeReceives))
                    : Task.FromResult(expected);
            });

        Func<Task> firstAttempt = async () => await InvokeGetReplyOrTimeoutAsync(25, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<TimeoutException>();

        activeReceives.Should().Be(0, "the timed-out receive must be terminal before a retry starts");
        byte[] actual = await InvokeGetReplyOrTimeoutAsync(1000, CancellationToken.None);

        actual.Should().Equal(expected);
        receiveCount.Should().Be(2);
        activeReceives.Should().Be(0);
    }

    [TestMethod]
    public async Task GetReplyOrTimeoutAsync_RepeatedTimeoutsShouldNotAccumulateReceives()
    {
        int activeReceives = 0;
        int maxActiveReceives = 0;
        _udpClientWrapper
            .ReceiveAsync(Arg.Any<CancellationToken>())
            .Returns(callInfo => WaitUntilCancelledAsync(callInfo.Arg<CancellationToken>(),
                () =>
                {
                    int active = Interlocked.Increment(ref activeReceives);
                    maxActiveReceives = Math.Max(maxActiveReceives, active);
                },
                () => Interlocked.Decrement(ref activeReceives)));

        for (int i = 0; i < 5; i++)
        {
            Func<Task> action = async () => await InvokeGetReplyOrTimeoutAsync(15, CancellationToken.None);
            await action.Should().ThrowAsync<TimeoutException>();
            activeReceives.Should().Be(0);
        }

        maxActiveReceives.Should().Be(1);
    }

    private Task<byte[]> InvokeGetReplyOrTimeoutAsync(int timeoutInMilliseconds, CancellationToken cancellationToken)
    {
        MethodInfo? method = typeof(PortMappingProtocolClient).GetMethod(
            "GetReplyOrTimeoutAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();

        return (Task<byte[]>)method.Invoke(_sut, [timeoutInMilliseconds, cancellationToken]);
    }

    private static async Task<byte[]> WaitUntilCancelledAsync(
        CancellationToken cancellationToken,
        Action? onStarted = null,
        Action? onCompleted = null)
    {
        onStarted?.Invoke();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
        finally
        {
            onCompleted?.Invoke();
        }
    }
}