/*
 * Copyright (c) 2025 Proton AG
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
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Vpn.OpenVpn;

namespace ProtonVPN.Vpn.PortScanning;

public class TcpPortScanner : ITcpPortScanner
{
    private readonly byte[] _staticKey;

    public TcpPortScanner(IStaticConfiguration config)
    {
        _staticKey = config.OpenVpn.StaticKey;
    }

    public async Task<bool> IsAliveAsync(string ip, int port, CancellationToken cancellationToken)
    {
        OpenVpnHandshake packet = new(_staticKey);
        IPEndPoint endpoint = new(IPAddress.Parse(ip), port);
        using Socket socket = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

            byte[] bytes = packet.Bytes(true);
            await socket.SendAsync(bytes.AsMemory(), SocketFlags.None, cancellationToken).ConfigureAwait(false);

            byte[] answer = new byte[1024];
            int received = await socket.ReceiveAsync(answer.AsMemory(), SocketFlags.None, cancellationToken).ConfigureAwait(false);

            return received > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}