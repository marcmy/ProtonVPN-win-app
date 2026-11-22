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
using ProtonVPN.Client.Common.UI.ServerHealth;
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
using ProtonVPN.Client.Models.Connections;

namespace ProtonVPN.Client.UI.Main.Home.Status;

public partial class ConnectionStatusHeaderViewModel : ActivatableViewModelBase,
    IEventMessageReceiver<ConnectionErrorMessage>,
    IEventMessageReceiver<ConnectionStatusChangedMessage>,
    IEventMessageReceiver<ConnectionDetailsChangedMessage>,
    IEventMessageReceiver<SettingChangedMessage>
{
    private const int REFRESH_TIMER_INTERVAL_IN_MS = 1000;
    private const int HEALTH_REFRESH_TIMER_INTERVAL_IN_MS = 60000;
    private static readonly ServerHealthHistoryStore _healthHistoryStore =
        ServerHealthHistorySession.Current;

    private readonly IDispatcherTimer _refreshTimer;
    private readonly IDispatcherTimer _healthRefreshTimer;
    private readonly IConnectionManager _connectionManager;
    private readonly ISettings _settings;
    private readonly IVpnServiceCaller _vpnServiceCaller;

    private CancellationTokenSource? _healthRefreshCancellation;
    private ServerHealthHistoryKey? _currentHealthKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProtectionDescription))]
    public partial TimeSpan SessionLength { get; set; } = TimeSpan.Zero;

    [ObservableProperty]
    public partial string CurrentServerName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ServerHealthSnapshot? CurrentServerHealthSnapshot { get; set; }

    [ObservableProperty]
    public partial string HealthGrade { get; set; } = "Checking…";

    [ObservableProperty]
    public partial string HealthLatency { get; set; } = "—";

    [ObservableProperty]
    public partial string HealthPacketLoss { get; set; } = "—";

    [ObservableProperty]
    public partial string HealthLossCount { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HealthStatus { get; set; } = "Checking…";

    [ObservableProperty]
    public partial string HealthLoad { get; set; } = "—";

    [ObservableProperty]
    public partial string HealthRoute { get; set; } = "Physical adapter (direct)";

    [ObservableProperty]
    public partial string HealthLastChecked { get; set; } = "Waiting for first check";

    [ObservableProperty]
    public partial bool IsHealthChecking { get; set; } = true;

    [ObservableProperty]
    public partial bool IsHealthUnavailable { get; set; }

    [ObservableProperty]
    public partial bool IsHealthPoor { get; set; }

    [ObservableProperty]
    public partial bool IsHealthFair { get; set; }

    [ObservableProperty]
    public partial bool IsHealthGood { get; set; }

    [ObservableProperty]
    public partial bool IsHealthExcellent { get; set; }

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

        _healthHistoryStore.SnapshotChanged += OnHealthSnapshotChanged;
        InvalidateAutoRefreshTimer();
        InvalidateHealthRefreshTimer();
    }

    protected override void OnDeactivated()
    {
        _healthHistoryStore.SnapshotChanged -= OnHealthSnapshotChanged;
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
        IServerHealthSource? source = CreateCurrentServerHealthSource();
        string? probeAddress = source?.HealthProbeAddress;
        if (source is null || string.IsNullOrWhiteSpace(probeAddress))
        {
            CancelHealthRefresh();
            _currentHealthKey = null;
            CurrentServerHealthSnapshot = null;
            SetUnavailableHealth("No endpoint is available for the current server.");
            return;
        }

        ServerHealthHistoryKey key = ServerHealthHistoryKey.Create(
            source.HealthServerId,
            probeAddress);
        if (_currentHealthKey == key)
        {
            ApplyHealthSnapshot(_healthHistoryStore.GetSnapshot(key));
            return; // Load/statistics messages must not restart measurement of an unchanged endpoint.
        }
        CancelHealthRefresh();
        _currentHealthKey = key;
        ApplyHealthSnapshot(_healthHistoryStore.GetSnapshot(_currentHealthKey.Value));
        _ = RefreshCurrentServerHealthAsync();
    }

    private void StopHealthMonitoring()
    {
        CancelHealthRefresh();
        _currentHealthKey = null;
        if (_healthRefreshTimer.IsEnabled)
        {
            _healthRefreshTimer.Stop();
        }
    }

    private void CancelHealthRefresh()
    {
        CancellationTokenSource? cancellation = _healthRefreshCancellation;
        _healthRefreshCancellation = null;
        cancellation?.Cancel();
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

    private IServerHealthSource? CreateCurrentServerHealthSource()
    {
        ConnectionDetails? details = _connectionManager.CurrentConnectionDetails;
        string? address = GetProbeAddress(details);
        return details is null || string.IsNullOrWhiteSpace(address)
            ? null
            : new CurrentServerHealthSource(
                _vpnServiceCaller,
                details.ServerId,
                address,
                details.ServerLoad);
    }

    private async Task RefreshCurrentServerHealthAsync()
    {
        if (_healthRefreshCancellation is not null || !_connectionManager.IsConnected)
        {
            return;
        }

        IServerHealthSource? source = CreateCurrentServerHealthSource();
        string? probeAddress = source?.HealthProbeAddress;
        if (source is null || string.IsNullOrWhiteSpace(probeAddress))
        {
            SetUnavailableHealth("No endpoint is available for the current server.");
            return;
        }

        ServerHealthHistoryKey requestedKey = ServerHealthHistoryKey.Create(
            source.HealthServerId,
            probeAddress);
        _currentHealthKey = requestedKey;
        using CancellationTokenSource cancellation = new();
        _healthRefreshCancellation = cancellation;
        try
        {
            await _healthHistoryStore.ProbeAsync(source, cancellation.Token);
            if (_connectionManager.IsConnected && _currentHealthKey == requestedKey)
            {
                ApplyHealthSnapshot(_healthHistoryStore.GetSnapshot(requestedKey));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_healthRefreshCancellation, cancellation))
            {
                _healthRefreshCancellation = null;
            }
        }
    }

    private void OnHealthSnapshotChanged(object? sender, ServerHealthSnapshotChangedEventArgs e)
    {
        if (_currentHealthKey is not ServerHealthHistoryKey key || e.Snapshot.Key != key)
        {
            return;
        }

        ExecuteOnUIThread(() =>
        {
            if (_currentHealthKey == e.Snapshot.Key)
            {
                ApplyHealthSnapshot(_healthHistoryStore.GetSnapshot(key));
            }
        });
    }

    private void ApplyHealthSnapshot(ServerHealthSnapshot snapshot)
    {
        CurrentServerHealthSnapshot = snapshot;
        ServerHealthPresentation presentation = ServerHealthPresentation.FromSnapshot(snapshot);
        HealthGrade = presentation.GradeText;
        HealthLatency = presentation.LatencyText;
        HealthPacketLoss = presentation.PacketLossText;
        HealthLossCount = presentation.LossCountText;
        HealthStatus = presentation.StatusText == presentation.GradeText ? string.Empty : presentation.StatusText;
        InvalidateCurrentServerDetails();
        HealthRoute = presentation.RouteText;
        HealthLastChecked = presentation.LastCheckedText;
        SetHealthState(presentation.ActiveBarCount switch
        {
            4 => HealthState.Excellent,
            3 => HealthState.Good,
            2 => HealthState.Fair,
            1 => HealthState.Poor,
            _ when snapshot.LatestMeasurement?.IsCompleteFailure == true => HealthState.Unavailable,
            _ when snapshot.IsChecking => HealthState.Checking,
            _ => HealthState.Idle,
        });
    }

    private void SetUnavailableHealth(string error)
    {
        HealthGrade = "Unavailable";
        HealthLatency = "—";
        HealthPacketLoss = "—";
        HealthLossCount = string.Empty;
        HealthStatus = "Check unavailable";
        HealthRoute = error;
        HealthLastChecked = "Check failed";
        SetHealthState(HealthState.Unavailable);
    }

    private void ResetHealthDisplay()
    {
        _currentHealthKey = null;
        CurrentServerHealthSnapshot = null;
        CurrentServerName = string.Empty;
        HealthGrade = "Checking…";
        HealthLatency = "—";
        HealthPacketLoss = "—";
        HealthLossCount = string.Empty;
        HealthStatus = "Checking…";
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

        return ServerPingSource.GetIpv4Address(connectionDetails.EntryIpAddress)
            ?? ServerPingSource.GetIpv4Address(
                connectionDetails.PhysicalServer.RelayIpByProtocol.GetValueOrDefault(connectionDetails.Protocol));
    }

    private sealed class CurrentServerHealthSource : IServerHealthSource
    {
        private readonly IVpnServiceCaller _vpnServiceCaller;

        public string HealthServerId { get; }
        public string? HealthProbeAddress { get; }
        public double HealthServerLoad { get; }

        public CurrentServerHealthSource(
            IVpnServiceCaller vpnServiceCaller,
            string serverId,
            string probeAddress,
            double serverLoad)
        {
            _vpnServiceCaller = vpnServiceCaller;
            HealthServerId = serverId;
            HealthProbeAddress = probeAddress;
            HealthServerLoad = serverLoad;
        }

        public Task<ServerHealthProbeMeasurement> ProbeHealthAsync(CancellationToken cancellationToken) =>
            ServerPingSource.ProbeAddressAsync(_vpnServiceCaller, HealthProbeAddress, HealthServerLoad,
                quickFirstResponse: false, cancellationToken);
    }

    private enum HealthState
    {
        Idle,
        Checking,
        Unavailable,
        Poor,
        Fair,
        Good,
        Excellent,
    }
}
