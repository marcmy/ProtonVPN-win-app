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
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.PortForwarding;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.ConnectionLogs;
using ProtonVPN.Service.Settings;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.PortMapping;

namespace ProtonVPN.Service.PortMapping;

internal sealed class PortForwardingForAppsNatPmpResponder : IDisposable
{
    private const int NatPmpPort = 5351;
    private const byte NatPmpVersion = 0;
    private const byte PublicAddressOperation = 0;
    private const byte MapUdpOperation = 1;
    private const byte MapTcpOperation = 2;
    private const uint LeaseSeconds = 60;

    private readonly object _sync = new();
    private readonly ILogger _logger;
    private readonly IServiceSettings _serviceSettings;
    private readonly IPortMappingProtocolClient _portMappingProtocolClient;

    private VpnState _vpnState = VpnState.Default;
    private PortForwardingState _portForwardingState = PortForwardingState.Default;
    private CancellationTokenSource _cancellationTokenSource;
    private UdpClient _udpClient;

    public PortForwardingForAppsNatPmpResponder(
        ILogger logger,
        IServiceSettings serviceSettings,
        IPortMappingProtocolClient portMappingProtocolClient)
    {
        _logger = logger;
        _serviceSettings = serviceSettings;
        _portMappingProtocolClient = portMappingProtocolClient;

        _serviceSettings.SettingsChanged += OnSettingsChanged;
        _portMappingProtocolClient.StateChanged += OnPortMappingStateChanged;
    }

    public void SetVpnState(VpnState vpnState)
    {
        lock (_sync)
        {
            _vpnState = vpnState ?? VpnState.Default;
        }

        ReconcileState();
    }

    public async Task StopAsync()
    {
        await StopResponderAsync();
    }

    private void OnSettingsChanged(object sender, ProtonVPN.ProcessCommunication.Contracts.Entities.Settings.MainSettingsIpcEntity e)
    {
        ReconcileState();
    }

    private void OnPortMappingStateChanged(object sender, EventArgs<PortForwardingState> e)
    {
        lock (_sync)
        {
            _portForwardingState = e.Data ?? PortForwardingState.Default;
        }

        ReconcileState();
    }

    private void ReconcileState()
    {
        if (ShouldRun())
        {
            StartResponderIfNeeded();
        }
        else
        {
            _ = StopResponderAsync();
        }
    }

    private bool ShouldRun()
    {
        lock (_sync)
        {
            return _serviceSettings.IsPortForwardingForAppsEnabled &&
                   _vpnState.Status == VpnStatus.Connected &&
                   _vpnState.PortForwarding &&
                   IPAddress.TryParse(_vpnState.LocalIp, out _) &&
                   _portForwardingState.Status == PortMappingStatus.SleepingUntilRefresh &&
                   _portForwardingState.MappedPort?.MappedPort?.ExternalPort > 0;
        }
    }

    private int CurrentForwardedPort
    {
        get
        {
            lock (_sync)
            {
                return _portForwardingState.MappedPort?.MappedPort?.ExternalPort ?? 0;
            }
        }
    }

    private string LocalIp
    {
        get
        {
            lock (_sync)
            {
                return _vpnState.LocalIp;
            }
        }
    }

    private void StartResponderIfNeeded()
    {
        lock (_sync)
        {
            if (_cancellationTokenSource is not null)
            {
                return;
            }

            _cancellationTokenSource = new();
        }

        _logger.Info<ConnectionLog>($"Starting NAT-PMP app port forwarding responder. LocalIp={LocalIp}, ForwardedPort={CurrentForwardedPort}.");
        _ = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
    }

    private async Task StopResponderAsync()
    {
        CancellationTokenSource cancellationTokenSource;
        lock (_sync)
        {
            cancellationTokenSource = _cancellationTokenSource;
            _cancellationTokenSource = null;
        }

        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
            _logger.Info<ConnectionLog>("Stopped NAT-PMP app port forwarding responder.");
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>("Failed to stop NAT-PMP app port forwarding responder cleanly.", e);
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        await Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using UdpClient client = new(AddressFamily.InterNetwork);
            _udpClient = client;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, NatPmpPort));

            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult receiveResult = await client.ReceiveAsync(cancellationToken);
                byte[] response = CreateResponse(receiveResult.Buffer);
                if (response.Length > 0)
                {
                    await client.SendAsync(response, response.Length, receiveResult.RemoteEndPoint);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>("NAT-PMP app port forwarding responder failed.", e);
            await StopResponderAsync();
        }
    }

    private byte[] CreateResponse(byte[] request)
    {
        if (request.Length < 2 || request[0] != NatPmpVersion)
        {
            return [];
        }

        byte operation = request[1];
        uint epoch = unchecked((uint)(Environment.TickCount64 / 1000));

        if (operation == PublicAddressOperation)
        {
            byte[] response = new byte[12];
            response[0] = NatPmpVersion;
            response[1] = 128 + PublicAddressOperation;
            WriteUInt16(response, 2, 0);
            WriteUInt32(response, 4, epoch);
            IPAddress.TryParse(LocalIp, out IPAddress localIp);
            byte[] addressBytes = (localIp ?? IPAddress.Any).GetAddressBytes();
            Buffer.BlockCopy(addressBytes, 0, response, 8, 4);
            return response;
        }

        if ((operation == MapUdpOperation || operation == MapTcpOperation) && request.Length >= 12)
        {
            ushort internalPort = ReadUInt16(request, 4);
            uint requestedLifetime = ReadUInt32(request, 8);
            ushort externalPort = requestedLifetime == 0 ? (ushort)0 : (ushort)CurrentForwardedPort;
            uint lifetime = requestedLifetime == 0 ? 0 : LeaseSeconds;

            byte[] response = new byte[16];
            response[0] = NatPmpVersion;
            response[1] = (byte)(128 + operation);
            WriteUInt16(response, 2, 0);
            WriteUInt32(response, 4, epoch);
            WriteUInt16(response, 8, internalPort);
            WriteUInt16(response, 10, externalPort);
            WriteUInt32(response, 12, lifetime);

            _logger.Info<ConnectionLog>($"NAT-PMP app mapping response. Protocol={(operation == MapTcpOperation ? "TCP" : "UDP")}, InternalPort={internalPort}, ExternalPort={externalPort}, Lifetime={lifetime}.");
            return response;
        }

        return [];
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    public void Dispose()
    {
        _serviceSettings.SettingsChanged -= OnSettingsChanged;
        _portMappingProtocolClient.StateChanged -= OnPortMappingStateChanged;
        _ = StopResponderAsync();
    }
}
