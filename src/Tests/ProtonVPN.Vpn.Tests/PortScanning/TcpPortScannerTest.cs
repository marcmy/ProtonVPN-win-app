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
using ProtonVPN.Vpn.PortScanning;

namespace ProtonVPN.Vpn.Tests.PortScanning;

[TestClass]
public class TcpPortScannerTest
{
    [TestMethod]
    public async Task IsAliveAsync_ShouldReturnTrue_WhenTcpConnectionCanBeEstablishedWithoutApplicationHandshakeAsync()
    {
        // Arrange
        TcpPortScanner subject = new();
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
            using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(2));

            // Act
            bool result = await subject.IsAliveAsync(IPAddress.Loopback.ToString(), port, cancellationTokenSource.Token);
            using TcpClient acceptedClient = await acceptTask.WaitAsync(TimeSpan.FromSeconds(1));

            // Assert
            result.Should().BeTrue();
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task IsAliveAsync_ShouldReturnFalse_WhenIpIsInvalidAsync()
    {
        // Arrange
        TcpPortScanner subject = new();

        // Act
        bool result = await subject.IsAliveAsync("not-an-ip", 443, CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }
}
