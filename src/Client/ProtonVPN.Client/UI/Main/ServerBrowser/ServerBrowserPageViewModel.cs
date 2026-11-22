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

using System.Collections.Concurrent;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Data;
using ProtonVPN.Client.Common.UI.ServerHealth;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Bases.ViewModels;
using ProtonVPN.Client.Core.Enums;
using ProtonVPN.Client.Core.Services.Activation;
using ProtonVPN.Client.Core.Services.Navigation;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Factories;
using ProtonVPN.Client.Logic.Auth.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Enums;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.Contracts.Preferences;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Extensions;
using ProtonVPN.Client.Logic.Servers.Contracts.Messages;
using ProtonVPN.Client.Logic.Users.Contracts.Messages;
using ProtonVPN.Client.Models.Connections;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Messages;

namespace ProtonVPN.Client.UI.Main.ServerBrowser;

public sealed partial class ServerBrowserPageViewModel : PageViewModelBase<IMainViewNavigator>,
    IEventMessageReceiver<ServerListChangedMessage>,
    IEventMessageReceiver<LocationNamesChangedMessage>,
    IEventMessageReceiver<ConnectionStatusChangedMessage>,
    IEventMessageReceiver<VpnPlanChangedMessage>,
    IEventMessageReceiver<SettingChangedMessage>,
    IEventMessageReceiver<LoggedOutMessage>
{
    private enum ServerSort { Recommended, Ping, Loss, Load, Location }

    private static readonly ServerHealthHistoryStore History = ServerHealthHistorySession.Current;
    private static readonly ServerHealthRefreshScheduler Refresh = ServerHealthHistorySession.Refresh;
    private static readonly int?[] MaxPingOptions = [null, 150, 100, 75, 50, 25];

    private readonly IServersLoader _servers;
    private readonly IExclusionChecker _exclusions;
    private readonly IConnectionManager _connectionManager;
    private readonly ILocationItemFactory _locationFactory;
    private readonly IUpsellCarouselWindowActivator _upsellActivator;
    private readonly ISettings _settings;

    private readonly ConcurrentDictionary<ServerHealthHistoryKey, byte> _changedKeys = new();
    private readonly Dictionary<string, ServerBrowserRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<ServerHealthHistoryKey, ServerBrowserRow> _rowsByKey = [];
    private readonly Dictionary<ServerBrowserRow, IDisposable> _visibleInterests = [];
    private readonly PriorityQueue<ServerBrowserRow, DateTimeOffset> _expirations = new();
    private ServerBrowserRow[] _scope = [];
    private HashSet<ServerBrowserRow> _scopeRows = [];
    private ServerBrowserRow[] _shownRows = [];
    private int _shownMatches;
    private int _discoveryCount;
    private bool _discoveryComplete;
    private bool _catalogueDirty = true;
    private bool _resettingFilters;
    private bool _projectionPending;
    private string? _connectedServerId;
    private ServerHealthUiRefreshQueue? _uiRefresh;
    private ServerHealthRefreshScheduler.Discovery? _discovery;
    private IDisposable? _selectedInterest;

    public CollectionViewSource Results { get; } = new() { IsSourceGrouped = true };
    public override string Title => Localizer.Get("ServerBrowser_Title");
    public bool IsEmpty => _shownRows.Length == 0;
    public string BackLabel => Localizer.Get(IsDetailsOpen ? "ServerBrowser_BackToResults" : "Common_Actions_Back");
    public string ActionLabel => Localizer.Get(SelectedRow is ServerBrowserRow row && IsRestricted(row)
        ? "Common_Actions_Upgrade"
        : _connectionManager.IsConnected ? "ServerBrowser_SwitchServer" : "Common_Actions_Connect");
    public string MeasuredLeg => SelectedRow is { IsSecureCore: true } row
        ? Localizer.GetFormat("ServerBrowser_SecureCoreMeasuredLeg", row.EntryCountry)
        : Localizer.Get("ServerBrowser_MeasuredLeg");

    public IReadOnlyList<string> SortOptions =>
    [
        Localizer.Get("ServerBrowser_SortRecommended"),
        Localizer.Get("ServerBrowser_SortPing"),
        Localizer.Get("ServerBrowser_SortLoss"),
        Localizer.Get("ServerBrowser_SortLoad"),
        Localizer.Get("ServerBrowser_Location"),
    ];

    public IReadOnlyList<string> PingOptions => MaxPingOptions
        .Select(limit => limit.HasValue
            ? Localizer.GetFormat("ServerBrowser_PingLimit", limit.Value)
            : Localizer.Get("ServerBrowser_PingAny"))
        .ToArray();

    [ObservableProperty]
    public partial int FeatureIndex { get; set; }

    [ObservableProperty]
    public partial int SortIndex { get; set; }

    [ObservableProperty]
    public partial int PingLimitIndex { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ServerBrowserRow? SelectedRow { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackLabel))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    public partial bool IsDetailsOpen { get; set; }

    [ObservableProperty]
    public partial ServerHealthSnapshot? SelectedSnapshot { get; set; }

    public event EventHandler? DiscoveryRequested;
    public event EventHandler? ResultsChanging;
    public event EventHandler? ResultsChanged;
    public event EventHandler? LiveProjectionRequested;
    public event EventHandler<string>? StatusAnnouncementRequested;

    public ServerBrowserPageViewModel(
        IMainViewNavigator navigator,
        IServersLoader servers,
        IExclusionChecker exclusions,
        IConnectionManager connectionManager,
        ILocationItemFactory locationFactory,
        IUpsellCarouselWindowActivator upsellActivator,
        ISettings settings,
        IViewModelHelper viewModelHelper) : base(navigator, viewModelHelper)
    {
        _servers = servers;
        _exclusions = exclusions;
        _connectionManager = connectionManager;
        _locationFactory = locationFactory;
        _upsellActivator = upsellActivator;
        _settings = settings;
        PingLimitIndex = Math.Max(0, Array.IndexOf(MaxPingOptions, ServerPingFilterSession.Current.MaxLatencyMilliseconds));
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        _uiRefresh = new(action => ExecuteOnUIThread(action), RefreshMeasurements);
        History.SnapshotChanged += OnSnapshotChanged;
        Refresh.ProgressChanged += OnProgressChanged;
        if (_catalogueDirty)
        {
            LoadCatalogue();
        }
        else
        {
            RefreshConnectionState(updateAll: true);
            UpdateScope();
        }
    }

    protected override void OnDeactivated()
    {
        History.SnapshotChanged -= OnSnapshotChanged;
        Refresh.ProgressChanged -= OnProgressChanged;
        _uiRefresh?.Dispose();
        _uiRefresh = null;
        _changedKeys.Clear();
        _expirations.Clear();
        StopDiscovery();
        SetVisibleRows([]);
        _selectedInterest?.Dispose();
        _selectedInterest = null;
        base.OnDeactivated();
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(SortOptions));
        OnPropertyChanged(nameof(PingOptions));
        OnPropertyChanged(nameof(BackLabel));
        InvalidateCatalogue();
    }

    partial void OnFeatureIndexChanged(int value)
    {
        if (IsActive && value >= 0)
        {
            UpdateScope();
        }
    }

    partial void OnQueryChanged(string value) => ApplyFilterChange();
    partial void OnSortIndexChanged(int value) => ApplyFilterChange();
    partial void OnPingLimitIndexChanged(int value) => ApplyFilterChange();

    [RelayCommand]
    private void ResetFilters()
    {
        _resettingFilters = true;
        Query = string.Empty;
        PingLimitIndex = 0;
        SortIndex = 0;
        _resettingFilters = false;
        ApplyFilterChange();
    }

    public async Task BackAsync()
    {
        if (IsDetailsOpen)
        {
            CloseDetails();
        }
        else
        {
            await ParentViewNavigator.NavigateToHomeViewAsync();
        }
    }

    public void Select(ServerBrowserRow row)
    {
        if (!_scopeRows.Contains(row) || row == SelectedRow && IsDetailsOpen)
        {
            return;
        }

        SetSelectedRow(row, openDetails: true);
    }

    private void SetSelectedRow(ServerBrowserRow? row, bool openDetails)
    {
        if (SelectedRow is ServerBrowserRow previous)
        {
            previous.IsSelected = false;
        }
        SelectedRow = row;
        if (row is not null)
        {
            row.IsSelected = true;
        }
        IsDetailsOpen = openDetails && row is not null;
        RefreshSelectedInterest();
        UpdateSelectedPresentation();
    }

    private void CloseDetails()
    {
        IsDetailsOpen = false;
        RefreshSelectedInterest();
        UpdateSelectedPresentation();
    }

    private bool CanConnect()
    {
        return IsActive && IsDetailsOpen && SelectedRow is ServerBrowserRow row
            && !string.Equals(row.Id, _connectedServerId, StringComparison.Ordinal)
            && _connectionManager.ConnectionStatus != ConnectionStatus.Connecting
            && (!row.IsUnderMaintenance || IsRestricted(row));
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (!CanConnect() || SelectedRow is not ServerBrowserRow row || _exclusions.IsServerExcluded(row.Server))
        {
            return;
        }

        ServerLocationItemBase item = row switch
        {
            { IsSecureCore: true } => new SecureCoreServerLocationItem(Localizer, _servers, _connectionManager, _upsellActivator, row.Server),
            _ when (CountriesConnectionType)FeatureIndex == CountriesConnectionType.P2P => _locationFactory.GetP2PServer(row.Server, isSearchItem: true),
            _ when row.Server.Features.IsSupported(ServerFeatures.Tor) => _locationFactory.GetTorServer(row.Server, isSearchItem: true),
            _ => _locationFactory.GetServer(row.Server, isSearchItem: true),
        };

        item.InvalidateIsRestricted(!IsRestricted(row));
        item.InvalidateIsActiveConnection(_connectionManager.CurrentConnectionDetails);
        if (!item.IsActiveConnection && item.ToggleConnectionCommand.CanExecute(null))
        {
            await item.ToggleConnectionCommand.ExecuteAsync(null);
        }
    }

    private void ApplyFilterChange()
    {
        if (IsActive && !_resettingFilters)
        {
            ApplyProjection(announce: true);
        }
    }

    private void LoadCatalogue()
    {
        string? selectedId = SelectedRow?.Id;
        bool reopen = IsDetailsOpen;
        _catalogueDirty = false;
        _rows.Clear();
        _rowsByKey.Clear();
        foreach (var server in _servers.GetServers())
        {
            if (!server.IsVisible || server.IsB2B() || !string.IsNullOrWhiteSpace(server.GatewayName)
                || _exclusions.IsServerExcluded(server) || _rows.ContainsKey(server.Id))
            {
                continue;
            }

            ServerBrowserRow row = new(server, Localizer);
            _rows.Add(row.Id, row);
            if (row.Key is ServerHealthHistoryKey key)
            {
                _rowsByKey.Add(key, row);
            }
        }

        RefreshConnectionState(updateAll: true);
        SetSelectedRow(selectedId is null ? null : _rows.GetValueOrDefault(selectedId), reopen);
        UpdateScope();
    }

    private void UpdateScope()
    {
        StopDiscovery();
        ProgressText = string.Empty;
        SetVisibleRows([]);
        ServerFeatures? features = (CountriesConnectionType)FeatureIndex switch
        {
            CountriesConnectionType.SecureCore => ServerFeatures.SecureCore,
            CountriesConnectionType.P2P => ServerFeatures.P2P,
            CountriesConnectionType.Tor => ServerFeatures.Tor,
            _ => null,
        };
        _scope = _rows.Values.Where(row => features is null
                || row.Server.IsPaid() && row.Server.Features.IsSupported(features.Value))
            .OrderBy(row => row.IsUnderMaintenance)
            .ThenBy(row => row.Server.Score)
            .ThenBy(row => row.Server.Load)
            .ThenBy(row => row.Id, StringComparer.Ordinal)
            .ToArray();
        _scopeRows = _scope.ToHashSet();
        if (SelectedRow is not null && !_scopeRows.Contains(SelectedRow))
        {
            SetSelectedRow(null, openDetails: false);
        }
        else
        {
            RefreshSelectedInterest();
            UpdateSelectedPresentation();
        }

        _expirations.Clear();
        foreach (ServerBrowserRow row in _scope)
        {
            RefreshRowMeasurement(row, newScope: true);
        }

        ApplyProjection(announce: true);
        DiscoveryRequested?.Invoke(this, EventArgs.Empty);
    }

    public void StartDiscovery(IReadOnlyList<ServerBrowserRow> displayed, bool listVisible)
    {
        if (!IsActive || _discovery is not null)
        {
            return;
        }

        IEnumerable<ServerBrowserRow> prioritized = listVisible && displayed.Count == 0 ? _shownRows.Take(12) : displayed;
        _discovery = Refresh.StartDiscovery(
            _scope.Where(CanProbe).Select(row => row.Source),
            prioritized.Where(row => _scopeRows.Contains(row) && CanProbe(row)).Select(row => row.Source));
        _discoveryCount = -1;
        _discoveryComplete = false;
        UpdateProgress();
    }

    private void StopDiscovery()
    {
        _discovery?.Dispose();
        _discovery = null;
    }

    public void SetVisibleRows(IReadOnlyList<ServerBrowserRow> displayed)
    {
        HashSet<ServerBrowserRow> visible = displayed
            .Where(row => IsActive && _scopeRows.Contains(row) && CanProbe(row))
            .ToHashSet();
        foreach (ServerBrowserRow row in _visibleInterests.Keys.Where(row => !visible.Contains(row)).ToArray())
        {
            _visibleInterests[row].Dispose();
            _visibleInterests.Remove(row);
        }
        foreach (ServerBrowserRow row in visible)
        {
            if (!_visibleInterests.ContainsKey(row))
            {
                _visibleInterests.Add(row, Refresh.Track(row.Source));
            }
        }
    }

    private void RefreshSelectedInterest()
    {
        _selectedInterest?.Dispose();
        _selectedInterest = IsActive && IsDetailsOpen && SelectedRow is ServerBrowserRow row && CanProbe(row)
            ? Refresh.Track(row.Source, prioritize: true)
            : null;
    }

    private bool CanProbe(ServerBrowserRow row) => !row.IsUnderMaintenance && row.Key.HasValue && !IsRestricted(row);

    private bool IsRestricted(ServerBrowserRow row) => (sbyte)row.Server.Tier > _settings.VpnPlan.MaxTier;

    private ServerHealthSnapshot? GetSnapshot(ServerBrowserRow row) =>
        row.Key is ServerHealthHistoryKey key && !IsRestricted(row) ? History.GetSnapshot(key) : null;

    private void OnSnapshotChanged(object? sender, ServerHealthSnapshotChangedEventArgs args)
    {
        _changedKeys[args.Snapshot.Key] = 0;
        _uiRefresh?.Request();
    }

    private void OnProgressChanged(object? sender, EventArgs args) => _uiRefresh?.Request();

    private void RefreshMeasurements()
    {
        bool orderChanged = false;
        foreach (var key in _changedKeys.Keys)
        {
            _changedKeys.TryRemove(key, out _);
            if (_rowsByKey.TryGetValue(key, out ServerBrowserRow? row) && _scopeRows.Contains(row))
            {
                orderChanged |= RefreshRowMeasurement(row);
            }
        }

        if (orderChanged)
        {
            RequestLiveProjection();
        }

        UpdateProgress();
    }

    private bool RefreshRowMeasurement(ServerBrowserRow row, bool newScope = false)
    {
        DateTimeOffset? previousExpiry = row.FreshUntil;
        ServerHealthSnapshot? snapshot = GetSnapshot(row);
        bool orderChanged = row.UpdateMeasurement(snapshot);
        if (row.FreshUntil is DateTimeOffset expiresAt && (newScope || expiresAt != previousExpiry))
        {
            _expirations.Enqueue(row, expiresAt);
        }
        if (IsDetailsOpen && row == SelectedRow)
        {
            SelectedSnapshot = snapshot;
        }
        return orderChanged;
    }

    private void RequestLiveProjection()
    {
        if (PingLimitIndex > 0 || (ServerSort)SortIndex is ServerSort.Ping or ServerSort.Loss)
        {
            _projectionPending = true;
            LiveProjectionRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RefreshExpiredMeasurements()
    {
        if (ExpireRows())
        {
            RequestLiveProjection();
        }
    }

    private bool ExpireRows()
    {
        bool orderChanged = false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        while (_expirations.TryPeek(out ServerBrowserRow? row, out DateTimeOffset expiry) && expiry <= now)
        {
            _expirations.Dequeue();
            if (row.FreshUntil == expiry)
            {
                orderChanged |= RefreshRowMeasurement(row);
            }
        }
        return orderChanged;
    }

    private void UpdateProgress()
    {
        if (_discovery is not { IsDisposed: false, TotalSources: > 0 } discovery)
        {
            return;
        }

        int completed = discovery.CompletedSources;
        if (_discoveryCount != completed)
        {
            _discoveryCount = completed;
            ProgressText = Localizer.GetFormat("ServerBrowser_PingProgress", completed, discovery.TotalSources);
        }

        if (discovery.IsRunning)
        {
            _discoveryComplete = false;
        }

        if (!_discoveryComplete && completed == discovery.TotalSources && !discovery.IsRunning)
        {
            _discoveryComplete = true;
            if (_projectionPending)
            {
                LiveProjectionRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                AnnounceResults();
            }
        }
    }

    public void ApplyPendingProjection()
    {
        if (_projectionPending && IsActive)
        {
            ApplyProjection(announce: _discoveryComplete);
        }
    }

    public ServerBrowserRow? FindShownRow(string id)
    {
        return _rows.TryGetValue(id, out ServerBrowserRow? row) && _shownRows.Contains(row) ? row : null;
    }

    private void ApplyProjection(bool announce)
    {
        _projectionPending = false;
        ExpireRows();
        CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
        string query = Query.Trim();
        int? threshold = MaxPingOptions[Math.Clamp(PingLimitIndex, 0, MaxPingOptions.Length - 1)];
        List<ServerBrowserRow> candidates = [];
        List<ServerBrowserRow> pending = [];
        foreach (ServerBrowserRow row in _scope)
        {
            if (!row.Matches(query, compare))
            {
                continue;
            }
            if (threshold is null || row.CurrentPing <= threshold.Value)
            {
                candidates.Add(row);
            }
            else if (row.CurrentPing is null)
            {
                pending.Add(row);
            }
        }
        ServerBrowserRow[] sorted = ApplySort(candidates).Concat(ApplySort(pending)).ToArray();

        if (candidates.Count != _shownMatches || !sorted.SequenceEqual(_shownRows) || Results.Source is null)
        {
            ResultsChanging?.Invoke(this, EventArgs.Empty);
            _shownRows = sorted;
            _shownMatches = candidates.Count;
            List<ServerBrowserGroup> groups = [new(sorted[.._shownMatches])];
            if (pending.Count > 0)
            {
                groups.Add(new(sorted[_shownMatches..], Localizer.Get("ServerBrowser_Pending")));
            }
            Results.Source = groups;
            OnPropertyChanged(nameof(Results));
            OnPropertyChanged(nameof(IsEmpty));
            ResultsChanged?.Invoke(this, EventArgs.Empty);
        }

        ResultText = Localizer.GetFormat("ServerBrowser_ResultCount", sorted.Length);
        if (announce)
        {
            AnnounceResults();
        }
    }

    private IEnumerable<ServerBrowserRow> ApplySort(List<ServerBrowserRow> rows) => (ServerSort)SortIndex switch
    {
        ServerSort.Ping => rows.OrderBy(row => row.CurrentPing ?? double.PositiveInfinity)
            .ThenBy(row => row.Id, StringComparer.Ordinal),
        ServerSort.Loss => rows.OrderBy(row => row.CurrentLoss ?? double.PositiveInfinity)
            .ThenByDescending(row => row.LossSampleCount)
            .ThenBy(row => row.CurrentPing ?? double.PositiveInfinity)
            .ThenBy(row => row.Id, StringComparer.Ordinal),
        ServerSort.Load => rows.OrderBy(row => row.Server.Load).ThenBy(row => row.Id, StringComparer.Ordinal),
        ServerSort.Location => rows.OrderBy(row => row.Country, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.City, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id, StringComparer.Ordinal),
        _ => rows,
    };

    private void AnnounceResults()
    {
        string text = _discovery is { IsRunning: false } ? $"{ResultText}. {ProgressText}" : ResultText;
        StatusAnnouncementRequested?.Invoke(this, text);
    }

    private void UpdateSelectedPresentation()
    {
        SelectedSnapshot = IsDetailsOpen && SelectedRow is not null ? GetSnapshot(SelectedRow) : null;
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(MeasuredLeg));
        ConnectCommand.NotifyCanExecuteChanged();
    }

    private void RefreshConnectionState(bool updateAll = false)
    {
        string? previousId = _connectedServerId;
        _connectedServerId = _connectionManager.IsConnected ? _connectionManager.CurrentConnectionDetails?.ServerId : null;
        if (updateAll)
        {
            foreach (ServerBrowserRow row in _rows.Values)
            {
                SetRowConnectionState(row);
            }
        }
        else if (previousId != _connectedServerId)
        {
            if (previousId is not null && _rows.TryGetValue(previousId, out var previous))
            {
                SetRowConnectionState(previous);
            }
            if (_connectedServerId is not null && _rows.TryGetValue(_connectedServerId, out var connected))
            {
                SetRowConnectionState(connected);
            }
        }
        UpdateSelectedPresentation();
    }

    private void SetRowConnectionState(ServerBrowserRow row) =>
        row.UpdateConnectionState(row.Id == _connectedServerId, IsRestricted(row));

    private void InvalidateCatalogue()
    {
        _catalogueDirty = true;
        if (IsActive)
        {
            LoadCatalogue();
        }
    }

    public void Receive(ServerListChangedMessage message) => ExecuteOnUIThread(InvalidateCatalogue);
    public void Receive(LocationNamesChangedMessage message) => ExecuteOnUIThread(InvalidateCatalogue);
    public void Receive(ConnectionStatusChangedMessage message) => ExecuteOnUIThreadIfActive(() => RefreshConnectionState());
    public void Receive(VpnPlanChangedMessage message) => ExecuteOnUIThreadIfActive(() =>
    {
        RefreshConnectionState(updateAll: true);
        UpdateScope();
    });

    public void Receive(SettingChangedMessage message)
    {
        if (message.PropertyName == nameof(ISettings.ExcludedLocationsList))
        {
            ExecuteOnUIThread(InvalidateCatalogue);
        }
    }

    public void Receive(LoggedOutMessage message) => ExecuteOnUIThread(() =>
    {
        SetSelectedRow(null, openDetails: false);
        _catalogueDirty = true;
        _rows.Clear();
        _rowsByKey.Clear();
        _scope = [];
        _scopeRows.Clear();
        _shownRows = [];
        _shownMatches = 0;
        Results.Source = null;
        OnPropertyChanged(nameof(Results));
        Query = string.Empty;
    });
}
