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

using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Enums;
using ProtonVPN.Client.Logic.Connection.Contracts.GuestHole;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Common.Core.Extensions;
using ProtonVPN.Common.Legacy.Abstract;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.GuestHoleLogs;

namespace ProtonVPN.Client.Logic.Connection.GuestHole;

public class GuestHoleManager : IGuestHoleManager, IEventMessageReceiver<ConnectionStatusChangedMessage>
{
    private const int CONNECTED_FUNC_DELAY_IN_MS = 1000;

    // Diagnostic-only trigger for the Guest Hole Windows smoke matrix branch.
    // Creating %TEMP%\ProtonVPN-GuestHole-Diagnostic\start opens a real Guest Hole and
    // keeps the on-connected callback pending. Creating \release completes the callback,
    // causing the normal Guest Hole teardown path to run. The \active marker exists only
    // while the Guest Hole has reached the Connected state and the callback is being held.
    private const string DIAGNOSTIC_DIRECTORY_NAME = "ProtonVPN-GuestHole-Diagnostic";
    private const string DIAGNOSTIC_START_FILE_NAME = "start";
    private const string DIAGNOSTIC_RELEASE_FILE_NAME = "release";
    private const string DIAGNOSTIC_ACTIVE_FILE_NAME = "active";
    private const int DIAGNOSTIC_POLL_INTERVAL_IN_MS = 250;

    private static readonly TimeSpan _semaphoreTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger _logger;
    private readonly IEventMessageSender _eventMessageSender;
    private readonly IGuestHoleConnector _guestHoleConnector;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _disconnectSync = new();
    private readonly object _diagnosticSync = new();
    private readonly string _diagnosticDirectoryPath = Path.Combine(Path.GetTempPath(), DIAGNOSTIC_DIRECTORY_NAME);

    private bool _isActive;
    private bool _wasConnected;
    private Func<Task<Result>>? _onConnectedFunc;
    private TaskCompletionSource<Result?>? _tcs;
    private TaskCompletionSource<bool>? _disconnectCompletionSource;
    private ConnectionStatus _lastVpnStatus = ConnectionStatus.Disconnected;
    private Task? _diagnosticGuestHoleTask;
    private TaskCompletionSource<bool>? _diagnosticReleaseCompletionSource;

    public bool IsActive => _isActive;

    public GuestHoleManager(
        ILogger logger,
        IEventMessageSender eventMessageSender,
        IGuestHoleConnector guestHoleConnector)
    {
        _logger = logger;
        _eventMessageSender = eventMessageSender;
        _guestHoleConnector = guestHoleConnector;

        InitializeDiagnosticGuestHoleTrigger();
    }

    public async Task<T?> ExecuteAsync<T>(Func<Task<Result>> onConnectedFunc, CancellationToken cancellationToken) where T : Result
    {
        if (!await _semaphore.WaitAsync(_semaphoreTimeout, cancellationToken))
        {
            _logger.Warn<GuestHoleLog>("Guest hole is already in use. Timed out waiting for access.");
            return null;
        }

        try
        {
            _onConnectedFunc = onConnectedFunc;

            // Run continuations asynchronously so TrySetResult completes the Task first, and the code awaiting it in
            // ExecuteAsync resumes later instead of immediately inside Receive/HandleDisconnection.
            _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            SetStatus(true);

            try
            {
                await _guestHoleConnector.ConnectToGuestHoleAsync();

                Result? result = await _tcs.Task.WaitAsync(cancellationToken);
                if (result is null)
                {
                    await DisconnectAsync();
                }

                return (T?)result;
            }
            catch (GuestHoleException e)
            {
                _logger.Warn<GuestHoleLog>("Failed to connect to guest hole.", e);

                HandleDisconnection();
                return null;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Info<GuestHoleLog>("Guest hole connection was cancelled.");

                await DisconnectAsync();

                throw;
            }
        }
        finally
        {
            await WaitForPendingDisconnectAsync();
            _semaphore.Release();
        }
    }

    private async Task WaitForPendingDisconnectAsync()
    {
        TaskCompletionSource<bool>? disconnectCompletionSource;
        lock (_disconnectSync)
        {
            disconnectCompletionSource = _disconnectCompletionSource;
        }

        if (disconnectCompletionSource is null)
        {
            return;
        }

        try
        {
            await disconnectCompletionSource.Task.WaitAsync(_semaphoreTimeout);
        }
        catch (TimeoutException)
        {
            lock (_disconnectSync)
            {
                if (ReferenceEquals(_disconnectCompletionSource, disconnectCompletionSource))
                {
                    _disconnectCompletionSource = null;
                }
            }

            _logger.Warn<GuestHoleLog>("Timed out waiting for Guest Hole to report the Disconnected state after disconnect was requested.");
        }
    }

    private void SetStatus(bool isActive)
    {
        lock (_disconnectSync)
        {
            _isActive = isActive;
        }

        _eventMessageSender.Send(new GuestHoleStatusChangedMessage(isActive));
    }

    public void Receive(ConnectionStatusChangedMessage message)
    {
        HandleConnectionStatusChangedAsync(message).FireAndForget();
    }

    private async Task HandleConnectionStatusChangedAsync(ConnectionStatusChangedMessage message)
    {
        if (!_isActive || _lastVpnStatus == message.ConnectionStatus)
        {
            return;
        }

        _lastVpnStatus = message.ConnectionStatus;

        switch (message.ConnectionStatus)
        {
            case ConnectionStatus.Connected when _tcs is not null &&
                                                 _onConnectedFunc is not null:
                _logger.Info<GuestHoleLog>("Connected to guest hole");

                _wasConnected = true;
                Result? result;
                try
                {
                    await Task.Delay(CONNECTED_FUNC_DELAY_IN_MS);
                    result = await _onConnectedFunc();
                }
                catch (Exception e)
                {
                    _logger.Error<GuestHoleLog>("Failed to execute action inside guest hole", e);
                    result = null;
                }

                SetTaskCompletionSourceResult(result);
                break;
            case ConnectionStatus.Disconnected:
                HandleDisconnection();
                break;
        }
    }

    private void HandleDisconnection()
    {
        if (!_wasConnected)
        {
            SetTaskCompletionSourceResult(null);
        }

        SetStatus(false);
        CompletePendingDisconnect();
        _logger.Info<GuestHoleLog>("Disconnected from guest hole.");
    }

    private void CompletePendingDisconnect()
    {
        TaskCompletionSource<bool>? disconnectCompletionSource;
        lock (_disconnectSync)
        {
            disconnectCompletionSource = _disconnectCompletionSource;
            _disconnectCompletionSource = null;
        }

        disconnectCompletionSource?.TrySetResult(true);
    }

    private void SetTaskCompletionSourceResult(Result? result)
    {
        if (_tcs is null)
        {
            return;
        }

        _tcs.TrySetResult(result);
        _tcs = null;
        _onConnectedFunc = null;
        _wasConnected = false;
    }

    public async Task DisconnectAsync()
    {
        lock (_disconnectSync)
        {
            if (_isActive)
            {
                _disconnectCompletionSource ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        await _guestHoleConnector.DisconnectFromGuestHoleAsync();
    }

    private void InitializeDiagnosticGuestHoleTrigger()
    {
        try
        {
            Directory.CreateDirectory(_diagnosticDirectoryPath);
            TryDeleteDiagnosticFile(DIAGNOSTIC_START_FILE_NAME);
            TryDeleteDiagnosticFile(DIAGNOSTIC_RELEASE_FILE_NAME);
            TryDeleteDiagnosticFile(DIAGNOSTIC_ACTIVE_FILE_NAME);

            WatchDiagnosticGuestHoleSignalsAsync().FireAndForget();
            _logger.Info<GuestHoleLog>($"Diagnostic Guest Hole trigger ready at '{_diagnosticDirectoryPath}'.");
        }
        catch (Exception e)
        {
            _logger.Error<GuestHoleLog>("Failed to initialize diagnostic Guest Hole trigger.", e);
        }
    }

    private async Task WatchDiagnosticGuestHoleSignalsAsync()
    {
        string startFilePath = GetDiagnosticFilePath(DIAGNOSTIC_START_FILE_NAME);
        string releaseFilePath = GetDiagnosticFilePath(DIAGNOSTIC_RELEASE_FILE_NAME);

        while (true)
        {
            try
            {
                if (File.Exists(startFilePath))
                {
                    File.Delete(startFilePath);
                    StartDiagnosticGuestHole();
                }

                if (File.Exists(releaseFilePath))
                {
                    File.Delete(releaseFilePath);
                    ReleaseDiagnosticGuestHole();
                }
            }
            catch (Exception e)
            {
                _logger.Error<GuestHoleLog>("Diagnostic Guest Hole trigger polling failed.", e);
            }

            await Task.Delay(DIAGNOSTIC_POLL_INTERVAL_IN_MS);
        }
    }

    private void StartDiagnosticGuestHole()
    {
        lock (_diagnosticSync)
        {
            if (_diagnosticGuestHoleTask is { IsCompleted: false })
            {
                _logger.Warn<GuestHoleLog>("Diagnostic Guest Hole start ignored because a diagnostic Guest Hole is already running.");
                return;
            }

            _diagnosticReleaseCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> releaseCompletionSource = _diagnosticReleaseCompletionSource;
            _diagnosticGuestHoleTask = RunDiagnosticGuestHoleAsync(releaseCompletionSource);
        }
    }

    private void ReleaseDiagnosticGuestHole()
    {
        TaskCompletionSource<bool>? releaseCompletionSource;
        lock (_diagnosticSync)
        {
            releaseCompletionSource = _diagnosticReleaseCompletionSource;
        }

        if (releaseCompletionSource is null)
        {
            _logger.Warn<GuestHoleLog>("Diagnostic Guest Hole release ignored because no diagnostic Guest Hole is running.");
            return;
        }

        _logger.Info<GuestHoleLog>("Releasing diagnostic Guest Hole hold.");
        releaseCompletionSource.TrySetResult(true);
    }

    private async Task RunDiagnosticGuestHoleAsync(TaskCompletionSource<bool> releaseCompletionSource)
    {
        string activeFilePath = GetDiagnosticFilePath(DIAGNOSTIC_ACTIVE_FILE_NAME);

        try
        {
            _logger.Info<GuestHoleLog>("Starting diagnostic Guest Hole through the real GuestHoleManager path.");

            await ExecuteAsync<Result>(
                async () =>
                {
                    File.WriteAllText(activeFilePath, DateTimeOffset.Now.ToString("O"));
                    _logger.Info<GuestHoleLog>("Diagnostic Guest Hole is connected and held. Create the 'release' marker to tear it down.");

                    try
                    {
                        await releaseCompletionSource.Task;
                    }
                    finally
                    {
                        TryDeleteDiagnosticFile(DIAGNOSTIC_ACTIVE_FILE_NAME);
                    }

                    // A null result intentionally tells ExecuteAsync to run its normal Guest Hole disconnect path.
                    return null!;
                },
                CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.Error<GuestHoleLog>("Diagnostic Guest Hole failed.", e);
        }
        finally
        {
            TryDeleteDiagnosticFile(DIAGNOSTIC_ACTIVE_FILE_NAME);

            lock (_diagnosticSync)
            {
                if (ReferenceEquals(_diagnosticReleaseCompletionSource, releaseCompletionSource))
                {
                    _diagnosticReleaseCompletionSource = null;
                }
            }

            _logger.Info<GuestHoleLog>("Diagnostic Guest Hole operation finished.");
        }
    }

    private string GetDiagnosticFilePath(string fileName)
    {
        return Path.Combine(_diagnosticDirectoryPath, fileName);
    }

    private void TryDeleteDiagnosticFile(string fileName)
    {
        try
        {
            string filePath = GetDiagnosticFilePath(fileName);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception e)
        {
            _logger.Warn<GuestHoleLog>($"Failed to delete diagnostic Guest Hole marker '{fileName}'.", e);
        }
    }
}
