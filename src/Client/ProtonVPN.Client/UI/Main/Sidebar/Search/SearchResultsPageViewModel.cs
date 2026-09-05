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

using System.ComponentModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProtonVPN.Client.Common.UI.ServerHealth;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Enums;
using ProtonVPN.Client.Core.Services.Navigation;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Factories;
using ProtonVPN.Client.Localization.Extensions;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.Contracts.Preferences;
using ProtonVPN.Client.Logic.Searches.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Extensions;
using ProtonVPN.Client.Logic.Servers.Contracts.Messages;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;
using ProtonVPN.Client.Logic.Servers.Contracts.Searches;
using ProtonVPN.Client.Models.Connections;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.UI.Main.Sidebar.Bases;
using ProtonVPN.Client.UI.Main.Sidebar.Connections.Bases.Contracts;
using ProtonVPN.Client.UI.Main.Sidebar.Search.Contracts;
using ProtonVPN.Common.Core.Extensions;

namespace ProtonVPN.Client.UI.Main.Sidebar.Search;

public partial class SearchResultsPageViewModel : ConnectionListViewModelBase<ISidebarViewNavigator>,
    ISearchInputReceiver,
    IEventMessageReceiver<ConnectionStatusChangedMessage>,
    IEventMessageReceiver<ServerListChangedMessage>,
    IEventMessageReceiver<NewServerFoundMessage>,
    IEventMessageReceiver<LocationNamesChangedMessage>
{
    private readonly IGlobalSearch _globalSearch;
    private readonly ILocationItemFactory _locationItemFactory;
    private readonly IServerFinder _serverFinder;
    private readonly IExclusionChecker _exclusionChecker;

    private string _input = string.Empty;
    private long _resultsGeneration;
    private CancellationTokenSource? _resultsCancellationTokenSource;
    private CancellationTokenSource? _pingFilterProbeCancellationTokenSource;
    private List<ConnectionItemBase> _unfilteredSearchResult = [];

    [ObservableProperty]
    private bool _hasSearchInput;

    [ObservableProperty]
    private bool _isBrowsingAllServers;

    [ObservableProperty]
    private ICountriesComponent _selectedCountriesComponent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoResults))]
    private bool _isMeasuringPingFilter;

    public List<ICountriesComponent> CountriesComponents { get; }

    public ServerPingFilterSession PingFilter { get; } = ServerPingFilterSession.Current;

    public bool ShowNoResults => !HasItems && !IsMeasuringPingFilter;

    public string ExampleCountries => $"{Localizer.GetCountryName("JP")}, {Localizer.GetCountryName("US")}";
    public string ExampleCities => $"{Localizer.GetCityName("Tokyo", "JP")}, {Localizer.GetCityName("Los Angeles", "US")}";
    public string ExampleServers => "JP#75, US-NY#166";

    public SearchResultsPageViewModel(
        ISettings settings,
        IConnectionManager connectionManager,
        IServersLoader serversLoader,
        ISidebarViewNavigator parentViewNavigator,
        IGlobalSearch globalSearch,
        ILocationItemFactory locationItemFactory,
        IConnectionGroupFactory connectionGroupFactory,
        IEnumerable<ICountriesComponent> countriesComponents,
        IViewModelHelper viewModelHelper,
        IServerFinder serverFinder,
        IExclusionChecker exclusionChecker)
        : base(parentViewNavigator,
               settings,
               serversLoader,
               connectionManager,
               connectionGroupFactory,
               viewModelHelper)
    {
        _globalSearch = globalSearch;
        _locationItemFactory = locationItemFactory;
        _serverFinder = serverFinder;
        _exclusionChecker = exclusionChecker;

        CountriesComponents = new(countriesComponents.OrderBy(p => p.SortIndex));
        _selectedCountriesComponent = CountriesComponents.First();
        PingFilter.PropertyChanged += OnPingFilterPropertyChanged;
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(ExampleCountries));
        OnPropertyChanged(nameof(ExampleCities));
        ReloadResultsAsync().FireAndForget();
    }

    partial void OnSelectedCountriesComponentChanged(ICountriesComponent value)
    {
        ReloadResultsAsync().FireAndForget();
    }

    public Task SearchAsync(string input)
    {
        IsBrowsingAllServers = false;
        _input = input;
        return ReloadResultsAsync();
    }

    private Task ReloadResultsAsync()
    {
        long generation = Interlocked.Increment(ref _resultsGeneration);
        CancellationTokenSource cancellationTokenSource = new();
        CancellationTokenSource? previousCancellationTokenSource =
            Interlocked.Exchange(ref _resultsCancellationTokenSource, cancellationTokenSource);

        previousCancellationTokenSource?.Cancel();
        previousCancellationTokenSource?.Dispose();

        return IsBrowsingAllServers
            ? LoadAllServersAsync(generation)
            : SearchAsync(generation, cancellationTokenSource.Token);
    }

    private async Task SearchAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            string input = _input;
            if (string.IsNullOrWhiteSpace(input))
            {
                if (!IsCurrentGeneration(generation))
                {
                    return;
                }

                HasSearchInput = false;
                SetSearchResult([]);
                _serverFinder.Cancel();
                return;
            }

            if (IsCurrentGeneration(generation))
            {
                HasSearchInput = true;
            }

            ServerFeatures? serverFeatures = GetServerFeatures();
            Func<ILocation, ConnectionItemBase?> itemFactory = GetConnectionItemCreationFunction();
            List<ConnectionItemBase> result = await GetSearchResultsAsync(
                input,
                serverFeatures,
                itemFactory,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested || !IsCurrentGeneration(generation))
            {
                return;
            }

            SetSearchResult(result);
            TriggerServerSearchTimerIfNecessary(input, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [RelayCommand]
    private Task BrowseAllServersAsync()
    {
        _input = string.Empty;
        IsBrowsingAllServers = true;
        return ReloadResultsAsync();
    }

    private Task LoadAllServersAsync(long generation)
    {
        ServerFeatures? serverFeatures = GetServerFeatures();
        Func<ILocation, ConnectionItemBase?> itemFactory = GetConnectionItemCreationFunction();
        IEnumerable<Server> servers = serverFeatures is null
            ? ServersLoader.GetServers()
            : ServersLoader.GetServersByFeatures(serverFeatures.Value);

        List<ConnectionItemBase> result = servers
            .Where(server => !_exclusionChecker.IsServerExcluded(server))
            .Select(itemFactory)
            .Where(ci => ci is not null)
            .Cast<ConnectionItemBase>()
            .ToList();

        if (IsCurrentGeneration(generation))
        {
            HasSearchInput = true;
            SetSearchResult(result);
            _serverFinder.Cancel();
        }

        return Task.CompletedTask;
    }

    private async Task<List<ConnectionItemBase>> GetSearchResultsAsync(
        string input,
        ServerFeatures? serverFeatures,
        Func<ILocation, ConnectionItemBase?> itemFactory,
        CancellationToken cancellationToken)
    {
        List<ILocation> locations = await _globalSearch.SearchAsync(
            input,
            serverFeatures,
            cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        return locations
            .Where(location => !IsLocationExcluded(location))
            .Select(itemFactory)
            .Where(ci => ci is not null)
            .Cast<ConnectionItemBase>()
            .ToList();
    }

    private bool IsCurrentGeneration(long generation)
    {
        return generation == Volatile.Read(ref _resultsGeneration);
    }

    private bool IsLocationExcluded(ILocation location)
    {
        return location switch
        {
            Country country => _exclusionChecker.IsCountryExcluded(country),
            State state => _exclusionChecker.IsStateExcluded(state),
            City city => _exclusionChecker.IsCityExcluded(city),
            Server server => _exclusionChecker.IsServerExcluded(server),
            _ => false,
        };
    }

    private void TriggerServerSearchTimerIfNecessary(string input, IEnumerable<ConnectionItemBase> result)
    {
        if (!result.OfType<ServerLocationItemBase>().Any())
        {
            _serverFinder.Search(input);
        }
        else
        {
            _serverFinder.Cancel();
        }
    }

    private ServerFeatures? GetServerFeatures()
    {
        return SelectedCountriesComponent.ConnectionType switch
        {
            CountriesConnectionType.SecureCore => ServerFeatures.SecureCore,
            CountriesConnectionType.P2P => ServerFeatures.P2P,
            CountriesConnectionType.Tor => ServerFeatures.Tor,
            _ => null,
        };
    }

    private void SetSearchResult(IEnumerable<ConnectionItemBase> result)
    {
        StopPingFilterProbes();
        _unfilteredSearchResult = result.ToList();
        ApplySearchResult();
        StartPingFilterProbes();
    }

    private void ApplySearchResult()
    {
        IEnumerable<ConnectionItemBase> result = PingFilter.IsActive
            ? _unfilteredSearchResult.Where(IsPingFilterMatch)
            : _unfilteredSearchResult;

        ResetItems(result);
        ResetGroups();

        InvalidateActiveConnection();
        InvalidateMaintenanceStates();
        InvalidateRestrictions();

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private bool IsPingFilterMatch(ConnectionItemBase item)
    {
        if (item is not ServerLocationItemBase server)
        {
            return true;
        }

        return !server.IsUnderMaintenance && PingFilter.Matches(server);
    }

    private void OnPingFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServerPingFilterSession.SelectedOption) || !HasSearchInput)
        {
            return;
        }

        StopPingFilterProbes();
        ApplySearchResult();
        StartPingFilterProbes();
    }

    private void StartPingFilterProbes()
    {
        if (!PingFilter.IsActive)
        {
            IsMeasuringPingFilter = false;
            return;
        }

        List<ServerLocationItemBase> servers = _unfilteredSearchResult
            .OfType<ServerLocationItemBase>()
            .Where(server => !server.IsUnderMaintenance && !string.IsNullOrWhiteSpace(server.HealthProbeAddress))
            .ToList();

        if (servers.Count == 0)
        {
            IsMeasuringPingFilter = false;
            return;
        }

        _pingFilterProbeCancellationTokenSource = new();
        IsMeasuringPingFilter = true;
        _ = ProbeForPingFilterAsync(servers, _pingFilterProbeCancellationTokenSource.Token);
    }

    private void StopPingFilterProbes()
    {
        _pingFilterProbeCancellationTokenSource?.Cancel();
        _pingFilterProbeCancellationTokenSource?.Dispose();
        _pingFilterProbeCancellationTokenSource = null;
        IsMeasuringPingFilter = false;
    }

    private async Task ProbeForPingFilterAsync(
        IReadOnlyCollection<ServerLocationItemBase> servers,
        CancellationToken cancellationToken)
    {
        try
        {
            List<Task<ServerHealthSnapshot>> pending = servers
                .Select(server => PingFilter.ProbeAsync(server, cancellationToken))
                .ToList();
            int completedSinceRefresh = 0;

            while (pending.Count > 0)
            {
                Task<ServerHealthSnapshot> completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                await completed;

                cancellationToken.ThrowIfCancellationRequested();
                completedSinceRefresh++;

                if (completedSinceRefresh >= 8 || pending.Count == 0)
                {
                    ApplySearchResult();
                    completedSinceRefresh = 0;
                }
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                IsMeasuringPingFilter = false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            OnPropertyChanged(nameof(ShowNoResults));
        }
    }

    private Func<ILocation, ConnectionItemBase?> GetConnectionItemCreationFunction()
    {
        return SelectedCountriesComponent.ConnectionType switch
        {
            CountriesConnectionType.SecureCore => CreateSecureCoreConnectionItem,
            CountriesConnectionType.P2P => CreateP2PConnectionItem,
            CountriesConnectionType.Tor => CreateTorConnectionItem,
            _ => CreateStandardConnectionItem,
        };
    }

    private ConnectionItemBase? CreateSecureCoreConnectionItem(ILocation location)
    {
        if (location is Server server)
        {
            return _locationItemFactory.GetServer(server, isSearchItem: true);
        }
        else if (location is Country country)
        {
            return _locationItemFactory.GetSecureCoreCountry(country, isSearchItem: true);
        }

        return null;
    }

    private ConnectionItemBase? CreateP2PConnectionItem(ILocation location)
    {
        if (location is Server server)
        {
            return _locationItemFactory.GetP2PServer(server, isSearchItem: true);
        }
        else if (location is City city)
        {
            return _locationItemFactory.GetP2PCity(city, isSearchItem: true);
        }
        else if (location is State state)
        {
            return _locationItemFactory.GetP2PState(state, isSearchItem: true);
        }
        else if (location is Country country)
        {
            return _locationItemFactory.GetP2PCountry(country, isSearchItem: true);
        }

        return null;
    }

    private ConnectionItemBase? CreateTorConnectionItem(ILocation location)
    {
        if (location is Server server)
        {
            return _locationItemFactory.GetTorServer(server, isSearchItem: true);
        }
        else if (location is Country country)
        {
            return _locationItemFactory.GetTorCountry(country, isSearchItem: true);
        }

        return null;
    }

    private ConnectionItemBase? CreateStandardConnectionItem(ILocation location)
    {
        if (location is Server server)
        {
            if (server.Features.IsB2B())
            {
                return _locationItemFactory.GetGatewayServer(server);
            }
            else
            {
                return _locationItemFactory.GetServer(server, isSearchItem: true);
            }
        }
        else if (location is City city)
        {
            return _locationItemFactory.GetCity(city, isSearchItem: true);
        }
        else if (location is State state)
        {
            return _locationItemFactory.GetState(state, isSearchItem: true);
        }
        else if (location is Country country)
        {
            return _locationItemFactory.GetCountry(country, isSearchItem: true);
        }

        return null;
    }

    public void Receive(ConnectionStatusChangedMessage message)
    {
        ExecuteOnUIThread(InvalidateActiveConnection);
    }

    public void Receive(ServerListChangedMessage message)
    {
        ExecuteOnUIThread(async () =>
        {
            if (HasSearchInput)
            {
                await ReloadResultsAsync();
            }
            else
            {
                InvalidateActiveConnection();
                InvalidateMaintenanceStates();
                InvalidateRestrictions();
            }
        });
    }

    public void Receive(NewServerFoundMessage message)
    {
        if (IsBrowsingAllServers || string.IsNullOrWhiteSpace(_input))
        {
            return;
        }

        ExecuteOnUIThread(ReloadResultsAsync);
    }

    public void Receive(LocationNamesChangedMessage message)
    {
        ExecuteOnUIThread(ReloadResultsAsync);
    }
}
