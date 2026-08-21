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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Client.Logic.Servers.Cache;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;

namespace ProtonVPN.Client.Logic.Servers.Tests;

[TestClass]
public class ServersLoaderTest
{
    private IServersCache _serversCache = null!;
    private ServersLoader _serversLoader = null!;

    [TestInitialize]
    public void Initialize()
    {
        _serversCache = Substitute.For<IServersCache>();
        _serversLoader = new ServersLoader(_serversCache, Substitute.For<IServerCountCache>());
    }

    [TestMethod]
    public void GetServersByCity_IncludesBlankStateServers_WhenCityHasSingleNamedState()
    {
        SetServers(
            CreateServer("US-NY#1", "New York", "NY"),
            CreateServer("US-NY#2", "New York", string.Empty),
            CreateServer("US-NY#3", "New York", " "),
            CreateServer("US-NJ#1", "Newark", "NJ"));

        City city = CreateCity("New York", "NY");

        List<string> result = _serversLoader.GetServersByCity(city)
            .Select(server => server.Id)
            .ToList();

        CollectionAssert.AreEquivalent(
            new[] { "US-NY#1", "US-NY#2", "US-NY#3" },
            result);
    }

    [TestMethod]
    public void GetServersByCity_SeparatesStates_WhenCityHasMultipleNamedStates()
    {
        SetServers(
            CreateServer("US-IL#1", "Springfield", "IL"),
            CreateServer("US-MO#1", "Springfield", "MO"),
            CreateServer("US-SP#1", "Springfield", string.Empty));

        List<string> illinois = _serversLoader.GetServersByCity(CreateCity("Springfield", "IL"))
            .Select(server => server.Id)
            .ToList();
        List<string> noState = _serversLoader.GetServersByCity(CreateCity("Springfield", null))
            .Select(server => server.Id)
            .ToList();

        CollectionAssert.AreEquivalent(new[] { "US-IL#1" }, illinois);
        CollectionAssert.AreEquivalent(new[] { "US-SP#1" }, noState);
    }

    [TestMethod]
    public void GetServersByFeaturesAndCity_IncludesBlankStateServers_ForMergedCity()
    {
        SetServers(
            CreateServer("US-NY#1", "New York", "NY", ServerFeatures.P2P),
            CreateServer("US-NY#2", "New York", string.Empty, ServerFeatures.P2P),
            CreateServer("US-NY#3", "New York", "NY"));

        City city = CreateCity("New York", "NY");

        List<string> result = _serversLoader.GetServersByFeaturesAndCity(ServerFeatures.P2P, city)
            .Select(server => server.Id)
            .ToList();

        CollectionAssert.AreEquivalent(new[] { "US-NY#1", "US-NY#2" }, result);
    }

    private void SetServers(params Server[] servers)
    {
        _serversCache.Servers.Returns(servers);
    }

    private static City CreateCity(string name, string? stateName)
    {
        return new City
        {
            CountryCode = "US",
            Name = name,
            StateName = stateName,
            Features = default,
            IsStandardUnderMaintenance = false,
            IsP2PUnderMaintenance = false,
            IsSecureCoreUnderMaintenance = false,
            IsTorUnderMaintenance = false,
        };
    }

    private static Server CreateServer(
        string id,
        string city,
        string state,
        ServerFeatures features = default)
    {
        return new Server
        {
            Id = id,
            Name = id,
            City = city,
            State = state,
            EntryCountry = "US",
            ExitCountry = "US",
            HostCountry = "US",
            Domain = $"{id}.example",
            Status = 1,
            Tier = ServerTiers.Plus,
            Features = features,
            Servers = [],
            GatewayName = string.Empty,
            StatusReference = new StatusReference(),
            EntryLocation = null!,
            ExitLocation = null!,
        };
    }
}
