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

using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    [ObservableProperty]
    private bool _hasSearchInput;

    [ObservableProperty]
    private bool _isBrowsingAllServers;

    [ObservableProperty]
    private ICountriesComponent _selectedCountriesComponent;

    public List<ICountriesComponent> CountriesComponents { get; }

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
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(ExampleCountries));
        OnPropertyChanged(nameof(ExampleCities));
        _ = ReloadResultsAsync();
    }

    partial void OnSelectedCountriesComponentChanged(ICountriesComponent value)
    {
        _ = ReloadResultsAsync();
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
        return IsBrowsingAllServers
            ? LoadAllServersAsync(generation)
            : SearchAsync(generation);
    }

    private async Task SearchAsync(long generation)
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

        ServerFeatures? serverFeatures = GetServerFeatures();
        Func<ILocation, ConnectionItemBase?> itemFactory = GetConnectionItemCreationFunction();
        List<ConnectionItemBase> result = await GetSearchResultsAsync(input, serverFeatures, itemFactory);

        if (!IsCurrentGeneration(generation))
        {
            return;
        }

        HasSearchInput = true;
        SetSearchResult(result);
        TriggerServerSearchTimerIfNecessary(input, result);
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
        Func<ILocation, ConnectionItemBase?> itemFactory)
    {
        return (await _globalSearch.SearchAsync(input, serverFeatures))
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
        ResetItems(result);
        ResetGroups();

        InvalidateActiveConnection();
        InvalidateMaintenanceStates();
        InvalidateRestrictions();

        OnPropertyChanged(nameof(HasItems));
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
        ExecuteOnUIThread(() =>
        {
            if (HasSearchInput)
            {
                _ = ReloadResultsAsync();
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

        ExecuteOnUIThread(() => _ = ReloadResultsAsync());
    }

    public void Receive(LocationNamesChangedMessage message)
    {
        ExecuteOnUIThread(() => _ = ReloadResultsAsync());
    }
}
