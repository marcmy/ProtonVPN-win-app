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

using CommunityToolkit.Mvvm.ComponentModel;
using ProtonVPN.Client.Common.Dispatching;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Bases.ViewModels;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Localization.Extensions;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Enums;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.Contracts.Models;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Extensions;
using ProtonVPN.Client.Settings.Contracts.Messages;
using ProtonVPN.Common.Legacy.Abstract;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Client.UI.Main.Home.Status;

public partial class ConnectionStatusHeaderViewModel : ActivatableViewModelBase,
    IEventMessageReceiver<ConnectionErrorMessage>,
    IEventMessageReceiver<ConnectionStatusChangedMessage>,
    IEventMessageReceiver<ConnectionDetailsChangedMessage>,
    IEventMessageReceiver<SettingChangedMessage>
{
    private const int REFRESH_TIMER_INTERVAL_IN_MS = 1000;
    private const int HEALTH_REFRESH_TIMER_INTERVAL_IN_MS = 15000;
    private const int HEALTH_PROBE_SAMPLE_COUNT = 4;

    private readonly IDispatcherTimer _refreshTimer;
    private readonly IDispatcherTimer _healthRefreshTimer;

    private readonly IConnectionManager _connectionManager;
    private readonly ISettings _settings;
    private readonly IVpnServiceCaller _vpnServiceCaller;

    private bool _isHealthRefreshInProgress;
    private string? _lastHealthProbeAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtectionDescription))]
    private TimeSpan _sessionLength = TimeSpan.Zero;

    [ObservableProperty]
    private string _currentServerName = string.Empty;

    [ObservableProperty]
    private string _healthGrade = "Checking…";

    [ObservableProperty]
    private string _healthLatency = "—";

    [ObservableProperty]
    private string _healthPacketLoss = "—";

    [ObservableProperty]
    private string _healthLoad = "—";

    [ObservableProperty]
    private string _healthRoute = "Physical adapter (direct)";

    [ObservableProperty]
    private string _healthLastChecked = "Waiting for first check";

    [ObservableProperty]
    private bool _isHealthChecking = true;

    [ObservableProperty]
    private bool _isHealthUnavailable;

    [ObservableProperty]
    private bool _isHealthPoor;

    [ObservableProperty]
    private bool _isHealthFair;

    [ObservableProperty]
    private bool _isHealthGood;

    [ObservableProperty]
    private bool _isHealthExcellent;

    public bool IsConnected => _connectionManager.IsConnected;

    public bool IsConnecting => _connectionManager.IsConnecting;

    public bool IsDisconnected => _connectionManager.IsDisconnected;

    public bool IsInternetUnavailable => IsDisconnected && _settings.IsAdvancedKillSwitchActive();

    public bool IsTwoFactorRequired => _connectionManager.IsTwoFactorError;

    public string ProtectionTitle =>
        _connectionManager.ConnectionStatus switch
        {
            ConnectionStatus.Disconnected => _settings.IsAdvancedKillSwitchActive()
                ? Localizer.Get("Home_ConnectionDetails_AdvancedKillSwitchActivated")
                : Localizer.Get("Home_ConnectionDetails_Unprotected"),
            ConnectionStatus.Connecting => _connectionManager.IsTwoFactorError
                ? Localizer.Get("Home_ConnectionDetails_TwoFactorRequired_Title")
                : Localizer.Get("Home_ConnectionDetails_Connecting"),
            ConnectionStatus.Connected => Localizer.Get("Home_ConnectionDetails_Protected"),
            _ => string.Empty,
        };

    public string ProtectionDescription =>
        _connectionManager.ConnectionStatus switch
        {
            ConnectionStatus.Disconnected => _settings.IsAdvancedKillSwitchActive()
                ? Localizer.Get("Home_ConnectionDetails_ConnectToRestoreConnection")
                : Localizer.Get("Home_ConnectionDetails_UnprotectedSubLabel"),
            ConnectionStatus.Connecting => _connectionManager.IsTwoFactorError
                ? Localizer.Get("Home_ConnectionDetails_TwoFactorRequired_Description")
                : Localizer.Get("Home_ConnectionDetails_ConnectingSubLabel"),
            ConnectionStatus.Connected => Localizer.GetFormattedTime(SessionLength) ?? string.Empty,
            _ => string.Empty,
        };

    public ConnectionStatusHeaderViewModel(
        IConnectionManager connectionManager,
        ISettings settings,
        IVpnServiceCaller vpnServiceCaller,
        IViewModelHelper viewModelHelper)
        : base(viewModelHelper)
    {
        _connectionManager = connectionManager;
        _settings = settings;
        _vpnServiceCaller = vpnServiceCaller;

        _refreshTimer = UIThreadDispatcher.GetTimer(TimeSpan.FromMilliseconds(REFRESH_TIMER_INTERVAL_IN_MS));
        _refreshTimer.Tick += OnRefreshTimerTick;

        _healthRefreshTimer = UIThreadDispatcher.GetTimer(TimeSpan.FromMilliseconds(HEALTH_REFRESH_TIMER_INTERVAL_IN_MS));
        _healthRefreshTimer.Tick += OnHealthRefreshTimerTick;
    }

    public void Receive(ConnectionErrorMessage message)
    {
        ExecuteOnUIThread(InvalidateConnectionError);
    }

    public void Receive(ConnectionStatusChangedMessage message)
    {
        ExecuteOnUIThread(InvalidateConnectionStatus);
    }

    public void Receive(ConnectionDetailsChangedMessage message)
    {
        ExecuteOnUIThread(() =>
        {
            InvalidateCurrentServerDetails();
            RestartHealthMonitoring();
        });
    }

    public void Receive(SettingChangedMessage message)
    {
        if (message.PropertyName is nameof(ISettings.KillSwitchMode) or nameof(ISettings.IsKillSwitchEnabled))
        {
            ExecuteOnUIThread(InvalidateConnectionStatus);
        }
    }

    protected override void OnActivated()
    {
        base.OnActivated();

        InvalidateAutoRefreshTimer();
        InvalidateHealthRefreshTimer();
    }

    protected override void OnDeactivated()
    {
        base.OnDeactivated();

        InvalidateAutoRefreshTimer();
        StopHealthMonitoring();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();

        OnPropertyChanged(nameof(ProtectionTitle));
        OnPropertyChanged(nameof(ProtectionDescription));
    }

    private void InvalidateConnectionError()
    {
        OnPropertyChanged(nameof(IsTwoFactorRequired));
        OnPropertyChanged(nameof(ProtectionTitle));
        OnPropertyChanged(nameof(ProtectionDescription));
    }

    private void InvalidateConnectionStatus()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(IsInternetUnavailable));
        OnPropertyChanged(nameof(IsTwoFactorRequired));
        OnPropertyChanged(nameof(ProtectionTitle));
        OnPropertyChanged(nameof(ProtectionDescription));

        InvalidateCurrentServerDetails();
        InvalidateAutoRefreshTimer();
        InvalidateHealthRefreshTimer();
    }

    private void InvalidateAutoRefreshTimer()
    {
        if (_connectionManager.IsConnected)
        {
            if (!_refreshTimer.IsEnabled)
            {
                InvalidateSessionLength();
                _refreshTimer.Start();
            }
        }
        else if (_refreshTimer.IsEnabled)
        {
            _refreshTimer.Stop();
        }
    }

    private void InvalidateHealthRefreshTimer()
    {
        if (_connectionManager.IsConnected)
        {
            if (!_healthRefreshTimer.IsEnabled)
            {
                _healthRefreshTimer.Start();
            }

            RestartHealthMonitoring();
        }
        else
        {
            StopHealthMonitoring();
            ResetHealthDisplay();
        }
    }

    private void RestartHealthMonitoring()
    {
        _lastHealthProbeAddress = null;
        SetHealthState(HealthState.Checking);
        _ = RefreshCurrentServerHealthAsync();
    }

    private void StopHealthMonitoring()
    {
        if (_healthRefreshTimer.IsEnabled)
        {
            _healthRefreshTimer.Stop();
        }

        _lastHealthProbeAddress = null;
    }

    private void OnRefreshTimerTick(object? sender, object e)
    {
        InvalidateSessionLength();
    }

    private async void OnHealthRefreshTimerTick(object? sender, object e)
    {
        await RefreshCurrentServerHealthAsync();
    }

    private void InvalidateSessionLength()
    {
        ConnectionDetails? connectionDetails = _connectionManager.CurrentConnectionDetails;
        SessionLength = connectionDetails?.EstablishedConnectionTimeUtc is null
            ? TimeSpan.Zero
            : DateTime.UtcNow - connectionDetails.EstablishedConnectionTimeUtc.Value;
    }

    private void InvalidateCurrentServerDetails()
    {
        ConnectionDetails? connectionDetails = _connectionManager.CurrentConnectionDetails;
        CurrentServerName = connectionDetails?.ServerName ?? string.Empty;
        HealthLoad = connectionDetails is null ? "—" : $"{connectionDetails.ServerLoad:P0}";
    }

    private async Task RefreshCurrentServerHealthAsync()
    {
        if (_isHealthRefreshInProgress || !_connectionManager.IsConnected)
        {
            return;
        }

        ConnectionDetails? connectionDetails = _connectionManager.CurrentConnectionDetails;
        string? probeAddress = GetProbeAddress(connectionDetails);
        if (connectionDetails is null || string.IsNullOrWhiteSpace(probeAddress))
        {
            SetUnavailableHealth("No endpoint is available for the current server.");
            return;
        }

        string serverId = connectionDetails.ServerId;
        _isHealthRefreshInProgress = true;

        try
        {
            if (!string.Equals(_lastHealthProbeAddress, probeAddress, StringComparison.OrdinalIgnoreCase))
            {
                SetHealthState(HealthState.Checking);
            }

            Result<ServerHealthProbeResultIpcEntity> result = await _vpnServiceCaller.ProbeServerHealthAsync(
                new ServerHealthProbeRequestIpcEntity
                {
                    Address = probeAddress,
                });

            ConnectionDetails? currentConnectionDetails = _connectionManager.CurrentConnectionDetails;
            if (!_connectionManager.IsConnected ||
                currentConnectionDetails is null ||
                !string.Equals(currentConnectionDetails.ServerId, serverId, StringComparison.Ordinal))
            {
                return;
            }

            InvalidateCurrentServerDetails();

            if (!result.Success)
            {
                SetUnavailableHealth(
                    string.IsNullOrWhiteSpace(result.Error)
                        ? "The current server health check could not be completed."
                        : result.Error);
                return;
            }

            ServerHealthProbeResultIpcEntity measurement = result.Value;
            _lastHealthProbeAddress = probeAddress;
            ApplyHealthMeasurement(measurement, currentConnectionDetails.ServerLoad);
        }
        finally
        {
            _isHealthRefreshInProgress = false;
        }
    }

    private void ApplyHealthMeasurement(ServerHealthProbeResultIpcEntity measurement, double serverLoad)
    {
        DateTime checkedAtUtc = DateTime.SpecifyKind(measurement.CheckedAtUtc, DateTimeKind.Utc);
        DateTimeOffset checkedAt = new(checkedAtUtc);

        HealthLoad = $"{serverLoad:P0}";
        HealthRoute = measurement.UsedPhysicalRoute
            ? "Physical adapter (direct)"
            : "Route unavailable";
        HealthLastChecked = $"Updated {checkedAt.ToLocalTime():T}";

        if (measurement.SuccessfulSamples == 0 || measurement.AverageLatencyMilliseconds is null)
        {
            SetUnavailableHealth(
                measurement.Error ?? "No ICMP replies were received. The server may block ping.",
                preserveTimestamp: true);
            return;
        }

        HealthLatency = $"{measurement.AverageLatencyMilliseconds.Value:0} ms";
        HealthPacketLoss = $"{measurement.PacketLossPercent:0}%";

        double score = CalculateHealthScore(
            measurement.AverageLatencyMilliseconds.Value,
            measurement.PacketLossPercent,
            serverLoad);

        if (score >= 85)
        {
            HealthGrade = "Excellent";
            SetHealthState(HealthState.Excellent);
        }
        else if (score >= 65)
        {
            HealthGrade = "Good";
            SetHealthState(HealthState.Good);
        }
        else if (score >= 40)
        {
            HealthGrade = "Fair";
            SetHealthState(HealthState.Fair);
        }
        else
        {
            HealthGrade = "Poor";
            SetHealthState(HealthState.Poor);
        }
    }

    private void SetUnavailableHealth(string error, bool preserveTimestamp = false)
    {
        HealthGrade = "Unavailable";
        HealthLatency = "—";
        HealthPacketLoss = "—";
        HealthRoute = error;
        if (!preserveTimestamp)
        {
            HealthLastChecked = "Check failed";
        }

        SetHealthState(HealthState.Unavailable);
    }

    private void ResetHealthDisplay()
    {
        CurrentServerName = string.Empty;
        HealthGrade = "Checking…";
        HealthLatency = "—";
        HealthPacketLoss = "—";
        HealthLoad = "—";
        HealthRoute = "Physical adapter (direct)";
        HealthLastChecked = "Waiting for first check";
        SetHealthState(HealthState.Checking);
    }

    private void SetHealthState(HealthState state)
    {
        IsHealthChecking = state == HealthState.Checking;
        IsHealthUnavailable = state == HealthState.Unavailable;
        IsHealthPoor = state == HealthState.Poor;
        IsHealthFair = state == HealthState.Fair;
        IsHealthGood = state == HealthState.Good;
        IsHealthExcellent = state == HealthState.Excellent;
    }

    private static string? GetProbeAddress(ConnectionDetails? connectionDetails)
    {
        if (connectionDetails is null)
        {
            return null;
        }

        string? connectedIpv4Address = connectionDetails.ServerIpAddress?.Ipv4Address;
        if (!string.IsNullOrWhiteSpace(connectedIpv4Address))
        {
            return connectedIpv4Address;
        }

        if (!string.IsNullOrWhiteSpace(connectionDetails.EntryIpAddress))
        {
            return connectionDetails.EntryIpAddress;
        }

        return connectionDetails.PhysicalServer.RelayIpByProtocol.Values
            .FirstOrDefault(ipAddress => !string.IsNullOrWhiteSpace(ipAddress));
    }

    private static double CalculateHealthScore(double latencyMilliseconds, double packetLossPercent, double serverLoad)
    {
        double latencyScore = latencyMilliseconds switch
        {
            <= 40 => 100,
            <= 80 => 85,
            <= 140 => 65,
            <= 220 => 40,
            <= 350 => 20,
            _ => 5,
        };

        double reliabilityScore = Math.Clamp(100 - packetLossPercent * 2, 0, 100);
        double loadScore = 100 - Math.Clamp(serverLoad, 0, 1) * 100;
        double score = latencyScore * 0.45 + reliabilityScore * 0.45 + loadScore * 0.10;

        if (packetLossPercent >= 50)
        {
            return Math.Min(score, 39);
        }

        if (packetLossPercent >= 25)
        {
            return Math.Min(score, 64);
        }

        return score;
    }

    private enum HealthState
    {
        Checking,
        Unavailable,
        Poor,
        Fair,
        Good,
        Excellent,
    }
}
