/*
 * Copyright (c) 2024 Proton AG
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
using ProtonVPN.Client.Localization.Contracts;
using ProtonVPN.Client.Localization.Extensions;
using ProtonVPN.Client.Logic.Searches.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;

namespace ProtonVPN.Client.Logic.Searches;

public class GlobalSearch : IGlobalSearch
{
    private readonly IServersLoader _serversLoader;
    //private readonly IProfilesManager _profilesManager;
    private readonly ILocalizationProvider _localizer;

    public GlobalSearch(IServersLoader serversLoader,
        //IProfilesManager profilesManager,
        ILocalizationProvider localizer)
    {
        _serversLoader = serversLoader;
        //_profilesManager = profilesManager;
        _localizer = localizer;
    }

    public async Task<List<ILocation>> SearchAsync(
        string? input,
        ServerFeatures? serverFeatures = null,
        SearchCategory categories = SearchCategory.All,
        CancellationToken cancellationToken = default)
    {
        input = input.NormalizeInput();

        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        Task<List<ILocation>> serversTask = categories.HasFlag(SearchCategory.Servers)
            ? Task.Run(() => SearchServers(input, serverFeatures, cancellationToken), cancellationToken)
            : Task.FromResult(new List<ILocation>());

        Task<List<ILocation>> countriesTask = categories.HasFlag(SearchCategory.Countries)
            ? Task.Run(() => SearchCountries(input, serverFeatures, cancellationToken), cancellationToken)
            : Task.FromResult(new List<ILocation>());

        Task<List<ILocation>> statesTask = categories.HasFlag(SearchCategory.States)
            ? Task.Run(() => SearchStates(input, serverFeatures, cancellationToken), cancellationToken)
            : Task.FromResult(new List<ILocation>());

        Task<List<ILocation>> citiesTask = categories.HasFlag(SearchCategory.Cities)
            ? Task.Run(() => SearchCities(input, serverFeatures, cancellationToken), cancellationToken)
            : Task.FromResult(new List<ILocation>());

        await Task.WhenAll(serversTask, citiesTask, statesTask, countriesTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        List<ILocation> result = new(
            countriesTask.Result.Count
            + statesTask.Result.Count
            + citiesTask.Result.Count
            + serversTask.Result.Count);

        result.AddRange(countriesTask.Result);
        result.AddRange(statesTask.Result);
        result.AddRange(citiesTask.Result);
        result.AddRange(serversTask.Result);
        return result;
    }

    private List<ILocation> SearchServers(
        string input,
        ServerFeatures? serverFeatures,
        CancellationToken cancellationToken)
    {
        IEnumerable<Server> servers = serverFeatures is null
            ? _serversLoader.GetServers()
            : _serversLoader.GetServersByFeatures(serverFeatures.Value);

        bool isServerNameSearch = IsServerNameSearch(input);
        string? serverNumberInput = GetServerNumberInput(input);
        List<ILocation> result = [];

        foreach (Server server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if ((isServerNameSearch && SearchMatcher.MatchesServer(server, input))
                || (serverNumberInput is not null && MatchesServerNumber(server.Name, serverNumberInput))
                || MatchesServerLocation(server, input))
            {
                result.Add(server);
            }
        }

        return result;
    }

    private bool MatchesServerLocation(Server server, string input)
    {
        return SearchMatcher.Equals(_localizer.GetCountryName(server.ExitCountry), input)
            || SearchMatcher.Equals(_localizer.GetCityName(server.City, server.ExitCountry), input)
            || (!string.IsNullOrWhiteSpace(server.State)
                && SearchMatcher.Equals(_localizer.GetStateName(server.State, server.ExitCountry), input));
    }

    private static bool IsServerNameSearch(string input)
    {
        return input.Contains('#')
            || input.Contains('-')
            || input.Any(char.IsDigit);
    }

    private static string? GetServerNumberInput(string input)
    {
        string serverNumberInput = input.TrimStart('#');
        return serverNumberInput.Length > 0 && serverNumberInput.All(char.IsDigit)
            ? serverNumberInput
            : null;
    }

    private static bool MatchesServerNumber(string serverName, string serverNumberInput)
    {
        int separatorIndex = serverName.LastIndexOf('#');
        return separatorIndex >= 0
            && separatorIndex < serverName.Length - 1
            && serverName[(separatorIndex + 1)..].StartsWith(serverNumberInput, StringComparison.InvariantCultureIgnoreCase);
    }

    private List<ILocation> SearchCities(
        string input,
        ServerFeatures? serverFeatures,
        CancellationToken cancellationToken)
    {
        IEnumerable<City> cities = serverFeatures is null
            ? _serversLoader.GetCities()
            : _serversLoader.GetCitiesByFeatures(serverFeatures.Value);

        List<ILocation> result = [];
        foreach (City city in cities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string localizedName = _localizer.GetCityName(city.Name, city.CountryCode);
            if (SearchMatcher.MatchesCity(localizedName, input))
            {
                result.Add(city);
            }
        }

        return result;
    }

    private List<ILocation> SearchStates(
        string input,
        ServerFeatures? serverFeatures,
        CancellationToken cancellationToken)
    {
        IEnumerable<State> states = serverFeatures is null
            ? _serversLoader.GetStates()
            : _serversLoader.GetStatesByFeatures(serverFeatures.Value);

        List<ILocation> result = [];
        foreach (State state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string localizedName = _localizer.GetStateName(state.Name, state.CountryCode);
            if (SearchMatcher.MatchesState(localizedName, input))
            {
                result.Add(state);
            }
        }

        return result;
    }

    private List<ILocation> SearchCountries(
        string input,
        ServerFeatures? serverFeatures,
        CancellationToken cancellationToken)
    {
        IEnumerable<Country> countries = serverFeatures is null
            ? _serversLoader.GetCountries()
            : _serversLoader.GetCountriesByFeatures(serverFeatures.Value);

        List<ILocation> result = [];
        foreach (Country country in countries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string localizedName = _localizer.GetCountryName(country.Code);
            if (SearchMatcher.MatchesCountry(country, localizedName, input))
            {
                result.Add(country);
            }
        }

        return result;
    }

    //private IEnumerable<ILocation> SearchGateways(string input)
    //{
    //    IEnumerable<string> gateways = _serversLoader.GetGateways();
    //    return gateways.Where(g => IsAMatch(g, input))
    //        .Select(g => new ConnectionIntent(new GatewayLocationIntent(g)));
    //}

    //private IEnumerable<ILocation> SearchProfiles(string input)
    //{
    //    IOrderedEnumerable<IConnectionProfile> profiles = _profilesManager.GetAll();
    //    return profiles.Where(p => IsAMatch(p.Name, input));
    //}
}
