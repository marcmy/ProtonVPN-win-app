/*
 * Copyright (c) 2023 Proton AG
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

using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.RequestCreators;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Client.Logic.Connection;

public class VpnServiceSettingsUpdater : IVpnServiceSettingsUpdater
{
    private static readonly TimeSpan SettingsCoalesceDelay = TimeSpan.FromMilliseconds(20);

    private readonly IVpnServiceCaller _vpnServiceCaller;
    private readonly IMainSettingsRequestCreator _mainSettingsRequestCreator;
    private readonly IConnectionManager _connectionManager;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _sendRequestSync = new();

    private bool _sendRequested;
    private bool _sendWorkerRunning;
    private Task _sendWorkerTask = Task.CompletedTask;

    public VpnServiceSettingsUpdater(
        IVpnServiceCaller vpnServiceCaller,
        IMainSettingsRequestCreator mainSettingsRequestCreator,
        IConnectionManager connectionManager)
    {
        _vpnServiceCaller = vpnServiceCaller;
        _mainSettingsRequestCreator = mainSettingsRequestCreator;
        _connectionManager = connectionManager;
    }

    public Task SendAsync()
    {
        lock (_sendRequestSync)
        {
            _sendRequested = true;

            if (!_sendWorkerRunning)
            {
                _sendWorkerRunning = true;
                _sendWorkerTask = RunSendWorkerAsync();
            }

            return _sendWorkerTask;
        }
    }

    private async Task RunSendWorkerAsync()
    {
        while (true)
        {
            // Settings pages persist changed properties one at a time and emit a message for each
            // setter. Give that synchronous burst a very small window to collapse into one full
            // settings snapshot instead of rebuilding the service state once per property.
            await Task.Delay(SettingsCoalesceDelay).ConfigureAwait(false);

            lock (_sendRequestSync)
            {
                _sendRequested = false;
            }

            await SendLatestSettingsSnapshotAsync().ConfigureAwait(false);

            lock (_sendRequestSync)
            {
                if (!_sendRequested)
                {
                    _sendWorkerRunning = false;
                    return;
                }
            }
        }
    }

    private async Task SendLatestSettingsSnapshotAsync()
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Build the snapshot only after acquiring the lock. If settings changed while another
            // service RPC was in flight, this guarantees the next snapshot observes the latest
            // values rather than allowing an older partial snapshot to win last-write-wins.
            MainSettingsIpcEntity settings = _mainSettingsRequestCreator.Create(_connectionManager.CurrentConnectionIntent);
            await _vpnServiceCaller.ApplySettingsAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task SendAsync(KillSwitchModeIpcEntity killSwitchMode)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            MainSettingsIpcEntity settings = _mainSettingsRequestCreator.Create(_connectionManager.CurrentConnectionIntent);
            settings.KillSwitchMode = killSwitchMode;
            await _vpnServiceCaller.ApplySettingsAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
