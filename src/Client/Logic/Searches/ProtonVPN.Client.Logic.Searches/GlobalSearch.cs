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
        SearchCategory categories = SearchCategory.All)
    {
        input = input.NormalizeInput();

        if (string.IsNullOrWhiteSpace(input))
        {
            return [];
        }

        Task<IEnumerable<ILocation>> serversTask = categories.HasFlag(SearchCategory.Servers)
            ? Task.Run(() => SearchServers(input, serverFeatures))
            : Task.FromResult<IEnumerable<ILocation>>([]);

        Task<IEnumerable<ILocation>> countriesTask = categories.HasFlag(SearchCategory.Countries)
            ? Task.Run(() => SearchCountries(input, serverFeatures))
            : Task.FromResult<IEnumerable<ILocation>>([]);

        Task<IEnumerable<ILocation>> statesTask = categories.HasFlag(SearchCategory.States)
            ? Task.Run(() => SearchStates(input, serverFeatures))
            : Task.FromResult<IEnumerable<ILocation>>([]);

        Task<IEnumerable<ILocation>> citiesTask = categories.HasFlag(SearchCategory.Cities)
            ? Task.Run(() => SearchCities(input, serverFeatures))
            : Task.FromResult<IEnumerable<ILocation>>([]);

        //Task<IEnumerable<IConnectionIntent>> gatewaysTask = Task.Run(() => SearchGateways(input));
        //Task<IEnumerable<IConnectionIntent>> profilesTask = Task.Run(() => SearchProfiles(input));

        Task.WaitAll(serversTask, citiesTask, statesTask, countriesTask/*, gatewaysTask, profilesTask*/);

        IEnumerable<ILocation> servers = await serversTask;
        IEnumerable<ILocation> cities = await citiesTask;
        IEnumerable<ILocation> states = await statesTask;
        IEnumerable<ILocation> countries = await countriesTask;
        //IEnumerable<IConnectionIntent> gateways = await gatewaysTask;
        //IEnumerable<IConnectionIntent> profiles = await profilesTask;

        //return profiles.Concat(gateways).Concat(countries).Concat(states).Concat(cities).Concat(servers).ToList();
        return countries
            .Concat(states)
            .Concat(cities)
            .Concat(servers)
            .ToList();
    }

    private IEnumerable<ILocation> SearchServers(string input, ServerFeatures? serverFeatures)
    {
        IEnumerable<Server> servers = serverFeatures is null
            ? _serversLoader.GetServers()
            : _serversLoader.GetServersByFeatures(serverFeatures.Value);

        bool isServerNameSearch = IsServerNameSearch(input);
        string? serverNumberInput = GetServerNumberInput(input);

        return servers.Where(server =>
            (isServerNameSearch && SearchMatcher.MatchesServer(server, input))
            || (serverNumberInput is not null && MatchesServerNumber(server.Name, serverNumberInput))
            || MatchesServerLocation(server, input));
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

    private IEnumerable<ILocation> SearchCities(string input, ServerFeatures? serverFeatures)
    {
        IEnumerable<City> cities = serverFeatures is null
            ? _serversLoader.GetCities()
            : _serversLoader.GetCitiesByFeatures(serverFeatures.Value);

        List<LocalizedLocation> localizedCities = cities.Select(city => new LocalizedLocation()
        {
            Location = city,
            LocalizedName = _localizer.GetCityName(city.Name, city.CountryCode)
        }).ToList();

        return localizedCities
            .Where(c => SearchMatcher.MatchesCity(c.LocalizedName, input))
            .Select(c => c.Location);
    }

    private IEnumerable<ILocation> SearchStates(string input, ServerFeatures? serverFeatures)
    {
        IEnumerable<State> states = serverFeatures is null
            ? _serversLoader.GetStates()
            : _serversLoader.GetStatesByFeatures(serverFeatures.Value);

        List<LocalizedLocation> localizedStates = states.Select(state => new LocalizedLocation()
        {
            Location = state,
            LocalizedName = _localizer.GetStateName(state.Name, state.CountryCode)
        }).ToList();

        return localizedStates
            .Where(s => SearchMatcher.MatchesState(s.LocalizedName, input))
            .Select(s => s.Location);
    }

    private IEnumerable<ILocation> SearchCountries(string input, ServerFeatures? serverFeatures)
    {
        IEnumerable<Country> countries = serverFeatures is null
            ? _serversLoader.GetCountries()
            : _serversLoader.GetCountriesByFeatures(serverFeatures.Value);

        List<LocalizedCountry> localizedCountries = countries.Select(c => new LocalizedCountry()
        {
            Country = c,
            LocalizedName = _localizer.GetCountryName(c.Code)
        }).ToList();

        return localizedCountries
            .Where(c => SearchMatcher.MatchesCountry(c.Country, c.LocalizedName, input))
            .Select(c => c.Country);
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
