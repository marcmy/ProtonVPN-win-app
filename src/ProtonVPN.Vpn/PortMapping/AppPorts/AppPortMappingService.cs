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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.ConnectionLogs;
using ProtonVPN.Vpn.Gateways;

namespace ProtonVPN.Vpn.PortMapping.AppPorts;

// App-facing NAT-PMP mapper for arbitrary local application ports.
// Unlike Proton's built-in PortMappingProtocolClient, this accepts internal != external
// as a normal successful result because Proton's gateway may assign a random public port.
public sealed class AppPortMappingService : IAppPortMappingService, IDisposable
{
    private const int NAT_PMP_PORT = 5351;
    private const int RECEIVE_TIMEOUT_MILLISECONDS = 3000;
    private const uint DEFAULT_REQUESTED_LIFETIME_SECONDS = 7200;
    private const uint MIN_RENEWAL_DELAY_SECONDS = 15;

    private readonly ILogger _logger;
    private readonly IGatewayCache _gatewayCache;
    private readonly ConcurrentDictionary<MappingKey, ActiveMapping> _activeMappings = new();

    public AppPortMappingService(ILogger logger, IGatewayCache gatewayCache)
    {
        _logger = logger;
        _gatewayCache = gatewayCache;
    }

    public async Task<IReadOnlyList<AppPortMappingResult>> MapTcpAndUdpAsync(
        ushort internalPort,
        ushort suggestedExternalPort = 0,
        uint requestedLifetimeSeconds = DEFAULT_REQUESTED_LIFETIME_SECONDS,
        CancellationToken cancellationToken = default)
    {
        AppPortMappingResult tcpResult = await MapAsync(
            AppPortMappingProtocol.Tcp,
            internalPort,
            suggestedExternalPort,
            requestedLifetimeSeconds,
            cancellationToken);

        AppPortMappingResult udpResult = await MapAsync(
            AppPortMappingProtocol.Udp,
            internalPort,
            suggestedExternalPort,
            requestedLifetimeSeconds,
            cancellationToken);

        return new[] { tcpResult, udpResult };
    }

    public async Task<AppPortMappingResult> MapAsync(
        AppPortMappingProtocol protocol,
        ushort internalPort,
        ushort suggestedExternalPort = 0,
        uint requestedLifetimeSeconds = DEFAULT_REQUESTED_LIFETIME_SECONDS,
        CancellationToken cancellationToken = default)
    {
        if (internalPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(internalPort), "Internal port must be non-zero for app mappings.");
        }

        IPAddress gateway = _gatewayCache.Get() ?? throw new InvalidOperationException("VPN gateway is missing; app NAT-PMP mapping cannot start.");
        IPEndPoint endpoint = new(gateway, NAT_PMP_PORT);

        byte[] request = CreateMappingRequest(protocol, internalPort, suggestedExternalPort, requestedLifetimeSeconds);
        byte[] response = await SendAndReceiveAsync(endpoint, request, cancellationToken);
        AppPortMappingResult result = ParseMappingResponse(protocol, response);

        if (!result.IsSuccess)
        {
            _logger.Warn<ConnectionLog>($"App NAT-PMP mapping failed. Protocol={protocol}, InternalPort={internalPort}, " +
                $"SuggestedExternalPort={suggestedExternalPort}, ResultCode={result.ResultCode}.");
            return result;
        }

        _logger.Info<ConnectionLog>($"App NAT-PMP mapping successful. Protocol={protocol}, " +
            $"InternalPort={result.InternalPort}, ExternalPort={result.ExternalPort}, Lifetime={result.LifetimeSeconds}s.");

        RememberMapping(result, suggestedExternalPort, requestedLifetimeSeconds);
        return result;
    }

    public async Task DestroyAsync(
        AppPortMappingProtocol protocol,
        ushort internalPort,
        CancellationToken cancellationToken = default)
    {
        MappingKey key = new(protocol, internalPort);
        if (_activeMappings.TryRemove(key, out ActiveMapping activeMapping))
        {
            activeMapping.CancellationTokenSource.Cancel();
            activeMapping.CancellationTokenSource.Dispose();
        }

        IPAddress gateway = _gatewayCache.Get();
        if (gateway is null)
        {
            _logger.Warn<ConnectionLog>($"Skipping app NAT-PMP destroy because gateway is missing. Protocol={protocol}, InternalPort={internalPort}.");
            return;
        }

        try
        {
            byte[] request = CreateMappingRequest(protocol, internalPort, suggestedExternalPort: 0, requestedLifetimeSeconds: 0);
            byte[] response = await SendAndReceiveAsync(new(gateway, NAT_PMP_PORT), request, cancellationToken);
            AppPortMappingResult result = ParseMappingResponse(protocol, response);

            _logger.Info<ConnectionLog>($"App NAT-PMP destroy result. Protocol={protocol}, InternalPort={internalPort}, " +
                $"ResultCode={result.ResultCode}, Lifetime={result.LifetimeSeconds}s.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.Error<ConnectionLog>($"App NAT-PMP destroy failed. Protocol={protocol}, InternalPort={internalPort}.", e);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        MappingKey[] keys = _activeMappings.Keys.ToArray();
        foreach (MappingKey key in keys)
        {
            await DestroyAsync(key.Protocol, key.InternalPort, cancellationToken);
        }
    }

    public IReadOnlyList<AppPortMappingResult> GetActiveMappings()
    {
        return _activeMappings.Values.Select(m => m.Result).ToList();
    }

    private void RememberMapping(AppPortMappingResult result, ushort suggestedExternalPort, uint requestedLifetimeSeconds)
    {
        MappingKey key = new(result.Protocol, result.InternalPort);
        if (_activeMappings.TryRemove(key, out ActiveMapping previousMapping))
        {
            previousMapping.CancellationTokenSource.Cancel();
            previousMapping.CancellationTokenSource.Dispose();
        }

        CancellationTokenSource cancellationTokenSource = new();
        ActiveMapping activeMapping = new(result, suggestedExternalPort, requestedLifetimeSeconds, cancellationTokenSource);
        _activeMappings[key] = activeMapping;
        ScheduleRenewal(activeMapping);
    }

    private void ScheduleRenewal(ActiveMapping activeMapping)
    {
        uint lifetimeSeconds = activeMapping.Result.LifetimeSeconds;
        uint delaySeconds = Math.Max(MIN_RENEWAL_DELAY_SECONDS, lifetimeSeconds / 2);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), activeMapping.CancellationTokenSource.Token);
                if (!activeMapping.CancellationTokenSource.IsCancellationRequested)
                {
                    await MapAsync(
                        activeMapping.Result.Protocol,
                        activeMapping.Result.InternalPort,
                        activeMapping.SuggestedExternalPort,
                        activeMapping.RequestedLifetimeSeconds,
                        activeMapping.CancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger.Error<ConnectionLog>($"App NAT-PMP renewal failed. Protocol={activeMapping.Result.Protocol}, " +
                    $"InternalPort={activeMapping.Result.InternalPort}, ExternalPort={activeMapping.Result.ExternalPort}.", e);
            }
        });
    }

    private async Task<byte[]> SendAndReceiveAsync(IPEndPoint endpoint, byte[] request, CancellationToken cancellationToken)
    {
        using UdpClient udpClient = new();
        udpClient.Client.ReceiveTimeout = RECEIVE_TIMEOUT_MILLISECONDS;
        udpClient.Connect(endpoint);

        await udpClient.SendAsync(request, cancellationToken);

        Task<UdpReceiveResult> receiveTask = udpClient.ReceiveAsync(cancellationToken).AsTask();
        Task timeoutTask = Task.Delay(RECEIVE_TIMEOUT_MILLISECONDS, cancellationToken);
        Task completedTask = await Task.WhenAny(receiveTask, timeoutTask);
        if (completedTask != receiveTask)
        {
            throw new TimeoutException($"NAT-PMP gateway {endpoint} did not reply within {RECEIVE_TIMEOUT_MILLISECONDS}ms.");
        }

        UdpReceiveResult result = await receiveTask;
        return result.Buffer;
    }

    private static byte[] CreateMappingRequest(
        AppPortMappingProtocol protocol,
        ushort internalPort,
        ushort suggestedExternalPort,
        uint requestedLifetimeSeconds)
    {
        byte[] request = new byte[12];
        request[0] = 0;
        request[1] = (byte)protocol;
        WriteUInt16BigEndian(request, 4, internalPort);
        WriteUInt16BigEndian(request, 6, suggestedExternalPort);
        WriteUInt32BigEndian(request, 8, requestedLifetimeSeconds);
        return request;
    }

    private static AppPortMappingResult ParseMappingResponse(AppPortMappingProtocol protocol, byte[] response)
    {
        if (response.Length < 16)
        {
            throw new InvalidOperationException($"NAT-PMP mapping response is too short. Length={response.Length}.");
        }

        return new()
        {
            Protocol = protocol,
            ResultCode = ReadUInt16BigEndian(response, 2),
            StartOfEpochSeconds = ReadUInt32BigEndian(response, 4),
            InternalPort = ReadUInt16BigEndian(response, 8),
            ExternalPort = ReadUInt16BigEndian(response, 10),
            LifetimeSeconds = ReadUInt32BigEndian(response, 12),
            ExpirationDateUtc = DateTime.UtcNow.AddSeconds(ReadUInt32BigEndian(response, 12)),
        };
    }

    private static ushort ReadUInt16BigEndian(byte[] bytes, int offset)
    {
        return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
    }

    private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private static void WriteUInt16BigEndian(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value >> 8);
        bytes[offset + 1] = (byte)value;
    }

    private static void WriteUInt32BigEndian(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    public void Dispose()
    {
        foreach (ActiveMapping mapping in _activeMappings.Values)
        {
            mapping.CancellationTokenSource.Cancel();
            mapping.CancellationTokenSource.Dispose();
        }
        _activeMappings.Clear();
    }

    private readonly record struct MappingKey(AppPortMappingProtocol Protocol, ushort InternalPort);

    private sealed record ActiveMapping(
        AppPortMappingResult Result,
        ushort SuggestedExternalPort,
        uint RequestedLifetimeSeconds,
        CancellationTokenSource CancellationTokenSource);
}
