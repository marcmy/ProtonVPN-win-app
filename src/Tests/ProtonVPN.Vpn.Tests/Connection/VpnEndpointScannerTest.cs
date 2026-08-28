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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Core.Networking;
using ProtonVPN.Common.Legacy.Threading;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.Connection;
using ProtonVPN.Vpn.PortScanning;

namespace ProtonVPN.Vpn.Tests.Connection;

[TestClass]
public class VpnEndpointScannerTest
{
    [TestMethod]
    public async Task ScanForBestEndpointAsync_ShouldCancelInFlightTcpProbe_WhenCallerCancelsAsync()
    {
        // Arrange
        CancellationObservingTcpPortScanner tcpPortScanner = new();
        VpnEndpointScanner subject = CreateSubject(tcpPortScanner);
        (VpnEndpoint endpoint, IReadOnlyDictionary<VpnProtocol, IReadOnlyCollection<int>> ports,
            IList<VpnProtocol> preferredProtocols) = CreateOpenVpnTcpScanParameters();
        using CancellationTokenSource cancellationTokenSource = new();

        // Act
        Task<VpnEndpoint> scanTask = subject.ScanForBestEndpointAsync(
            endpoint,
            ports,
            preferredProtocols,
            cancellationTokenSource.Token);

        await tcpPortScanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellationTokenSource.Cancel();
        VpnEndpoint result = await scanTask.WaitAsync(TimeSpan.FromSeconds(1));

        // Assert
        result.IsEmpty.Should().BeTrue();
        tcpPortScanner.ObservedCancellationToken.CanBeCanceled.Should().BeTrue();
        tcpPortScanner.ObservedCancellationToken.IsCancellationRequested.Should().BeTrue();
        tcpPortScanner.Completed.Task.IsCompleted.Should().BeTrue();
    }

    [TestMethod]
    public async Task ScanForBestEndpointAsync_ShouldCancelInFlightTcpProbe_WhenScannerTimesOutAsync()
    {
        // Arrange
        CancellationObservingTcpPortScanner tcpPortScanner = new();
        VpnEndpointScanner subject = CreateSubject(tcpPortScanner);
        (VpnEndpoint endpoint, IReadOnlyDictionary<VpnProtocol, IReadOnlyCollection<int>> ports,
            IList<VpnProtocol> preferredProtocols) = CreateOpenVpnTcpScanParameters();

        // Act
        Task<VpnEndpoint> scanTask = subject.ScanForBestEndpointAsync(
            endpoint,
            ports,
            preferredProtocols,
            CancellationToken.None);

        await tcpPortScanner.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        VpnEndpoint result = await scanTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        result.IsEmpty.Should().BeTrue();
        tcpPortScanner.ObservedCancellationToken.CanBeCanceled.Should().BeTrue();
        tcpPortScanner.ObservedCancellationToken.IsCancellationRequested.Should().BeTrue();
        tcpPortScanner.Completed.Task.IsCompleted.Should().BeTrue();
    }

    private static VpnEndpointScanner CreateSubject(ITcpPortScanner tcpPortScanner)
    {
        return new VpnEndpointScanner(
            Substitute.For<ILogger>(),
            new ImmediateTaskQueue(),
            tcpPortScanner,
            new UnusedUdpPingClient());
    }

    private static (VpnEndpoint Endpoint,
        IReadOnlyDictionary<VpnProtocol, IReadOnlyCollection<int>> Ports,
        IList<VpnProtocol> PreferredProtocols) CreateOpenVpnTcpScanParameters()
    {
        VpnHost host = new(
            name: "server.test",
            ip: "10.0.0.1",
            label: string.Empty,
            x25519PublicKey: null,
            signature: string.Empty,
            isIpv6Supported: false,
            relayIpByProtocol: null);

        VpnEndpoint endpoint = new(host, VpnProtocol.OpenVpnTcp);
        IReadOnlyDictionary<VpnProtocol, IReadOnlyCollection<int>> ports =
            new Dictionary<VpnProtocol, IReadOnlyCollection<int>>
            {
                [VpnProtocol.OpenVpnTcp] = [443]
            };
        IList<VpnProtocol> preferredProtocols = [VpnProtocol.OpenVpnTcp];

        return (endpoint, ports, preferredProtocols);
    }

    private sealed class ImmediateTaskQueue : ITaskQueue
    {
        public Task<T> Enqueue<T>(Func<Task<T>> function)
        {
            return function();
        }
    }

    private sealed class CancellationObservingTcpPortScanner : ITcpPortScanner
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedCancellationToken { get; private set; }

        public async Task<bool> IsAliveAsync(string ip, int port, CancellationToken cancellationToken)
        {
            ObservedCancellationToken = cancellationToken;
            Started.TrySetResult(true);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            Completed.TrySetResult(true);
            return false;
        }
    }

    private sealed class UnusedUdpPingClient : IUdpPingClient
    {
        public Task<bool> PingAsync(string ip, int port, string serverKeyBase64, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }
    }
}
