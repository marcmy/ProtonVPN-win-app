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

using Microsoft.UI.Xaml;
using ProtonVPN.Logging.Contracts.Events.AppLogs;
using ProtonVPN.Logging.Events;

namespace ProtonVPN.Client.Common.UI.Exceptions;

public sealed class ClientGlobalExceptionHandler : GlobalExceptionHandlerBase
{
    public void Initialize(Application app)
    {
        base.Initialize();
        app.UnhandledException += OnUiUnhandledException;
    }

    private void OnUiUnhandledException(object? sender, UnhandledExceptionEventArgs eventArgs)
    {
        TryLogException("UI unhandled exception", eventArgs.Exception, isFatal: false);

        // Proton VPN 5.1.6 also sets eventArgs.Handled = true here. That crash-suppression
        // policy is intentionally withheld until it is validated independently; the
        // centralized logging architecture does not require swallowing every WinUI exception.
    }

    protected override void LogFatal(string handler, Exception exception)
    {
        Logger?.Fatal<AppCrashLog>(handler, exception);
    }

    protected override void LogError(string handler, Exception exception)
    {
        Logger?.Error<AppLog>(handler, exception);
    }
}
