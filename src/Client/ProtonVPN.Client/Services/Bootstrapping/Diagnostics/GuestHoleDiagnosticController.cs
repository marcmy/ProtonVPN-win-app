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

using ProtonVPN.Client.Logic.Connection.Contracts.GuestHole;
using ProtonVPN.Common.Legacy.Abstract;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.AppLogs;

namespace ProtonVPN.Client.Services.Bootstrapping.Diagnostics;

/// <summary>
/// Diagnostic-only control surface for the Windows Guest Hole smoke matrix.
///
/// This intentionally exercises IGuestHoleManager itself. The controller does not modify
/// routes, DNS, firewall policy, settings, or VPN state directly. A start request enters the
/// normal Guest Hole connection path and keeps the real Guest Hole callback alive until a stop
/// request arrives. The callback then asks IGuestHoleManager to perform its normal disconnect.
///
/// This class exists only on the diagnostics/guest-hole-windows-smoke-matrix branch and must not
/// be carried into a production release.
/// </summary>
internal sealed class GuestHoleDiagnosticController
{
    private const string START_EVENT_NAME = @"Local\ProtonVPN.Diagnostics.GuestHole.Start";
    private const string STOP_EVENT_NAME = @"Local\ProtonVPN.Diagnostics.GuestHole.Stop";
    private const string ACTIVE_EVENT_NAME = @"Local\ProtonVPN.Diagnostics.GuestHole.Active";
    private const string IDLE_EVENT_NAME = @"Local\ProtonVPN.Diagnostics.GuestHole.Idle";
    private const string FAILED_EVENT_NAME = @"Local\ProtonVPN.Diagnostics.GuestHole.Failed";

    private readonly IGuestHoleManager _guestHoleManager;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    private readonly EventWaitHandle _startEvent = new(false, EventResetMode.AutoReset, START_EVENT_NAME);
    private readonly EventWaitHandle _stopEvent = new(false, EventResetMode.AutoReset, STOP_EVENT_NAME);
    private readonly EventWaitHandle _activeEvent = new(false, EventResetMode.ManualReset, ACTIVE_EVENT_NAME);
    private readonly EventWaitHandle _idleEvent = new(true, EventResetMode.ManualReset, IDLE_EVENT_NAME);
    private readonly EventWaitHandle _failedEvent = new(false, EventResetMode.ManualReset, FAILED_EVENT_NAME);

    private Task? _listenerTask;
    private Task? _executionTask;

    public GuestHoleDiagnosticController(IGuestHoleManager guestHoleManager, ILogger logger)
    {
        _guestHoleManager = guestHoleManager;
        _logger = logger;
    }

    public void Start()
    {
        lock (_sync)
        {
            _listenerTask ??= Task.Run(ListenForStartRequests);
        }
    }

    private void ListenForStartRequests()
    {
        while (true)
        {
            _startEvent.WaitOne();

            lock (_sync)
            {
                if (_executionTask is { IsCompleted: false })
                {
                    _logger.Info<AppLog>("Guest Hole diagnostic start request ignored because a diagnostic transition is already running.");
                    continue;
                }

                // Clear stale signals from a previous run before entering the genuine Guest Hole path.
                _stopEvent.Reset();
                _activeEvent.Reset();
                _idleEvent.Reset();
                _failedEvent.Reset();

                _executionTask = RunGuestHoleAsync();
            }
        }
    }

    private async Task RunGuestHoleAsync()
    {
        try
        {
            _logger.Info<AppLog>("Guest Hole diagnostic: starting genuine Guest Hole connection.");

            Result? result = await _guestHoleManager.ExecuteAsync<Result>(HoldGuestHoleAsync, CancellationToken.None);
            if (result is null)
            {
                _failedEvent.Set();
                _logger.Warn<AppLog>("Guest Hole diagnostic: Guest Hole did not become usable or disconnected before the diagnostic callback completed.");
            }
        }
        catch (Exception e)
        {
            _failedEvent.Set();
            _logger.Error<AppLog>("Guest Hole diagnostic: unexpected failure.", e);
        }
        finally
        {
            _activeEvent.Reset();
            _idleEvent.Set();
            _logger.Info<AppLog>("Guest Hole diagnostic: idle.");
        }
    }

    private async Task<Result> HoldGuestHoleAsync()
    {
        // GuestHoleManager calls this only after it has received the real Connected status and
        // applied its normal connected-callback delay, so this is a reliable smoke-test signal.
        _activeEvent.Set();
        _logger.Info<AppLog>("Guest Hole diagnostic: genuine Guest Hole is active; waiting for release request.");

        await Task.Run(() => _stopEvent.WaitOne());

        _logger.Info<AppLog>("Guest Hole diagnostic: release requested; performing genuine Guest Hole disconnect.");
        await _guestHoleManager.DisconnectAsync();

        return Result.Ok();
    }
}
