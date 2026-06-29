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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;

namespace ProtonVPN.Client.Logic.Searches.Tests;

public partial class GlobalSearchTest
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public async Task TestStandard_InvalidInput_Async(string? input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    [DataRow("A")]
    [DataRow("a")]
    [DataRow("Á")]
    [DataRow("á")]
    [DataRow("À")]
    [DataRow("à")]
    [DataRow("Ã")]
    [DataRow("ã")]
    [DataRow("Â")]
    [DataRow("â")]
    [DataRow(" A")]
    [DataRow("A ")]
    public async Task TestStandard_1Char_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(4, result);
        Assert.IsNotNull(result.Single(l => l is City city && city.Name == "Anchorage"));
        Assert.IsNotNull(result.Single(l => l is City city && city.Name == "Argel"));
        Assert.IsNotNull(result.Single(l => l is State state && state.Name == "Alaska"));
        Assert.IsNotNull(result.Single(l => l is Country country && country.Code == "DZ"));
    }

    [TestMethod]
    [DataRow("CH")]
    [DataRow("ch")]
    [DataRow("Ch")]
    [DataRow("cH")]
    [DataRow(" ch")]
    [DataRow("ch ")]
    public async Task TestStandard_2Chars_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(6, result);
        Assert.IsNotNull(result.Single(l => l is City city && city.Name == "Anchorage"));
        Assert.IsNotNull(result.Single(l => l is City city && city.Name == "Zurich"));
        Assert.IsNotNull(result.Single(l => l is City city && city.Name == "Chicago"));
        Assert.IsNotNull(result.Single(l => l is State state && state.Name == "Michigan"));
        Assert.IsNotNull(result.Single(l => l is Country country && country.Code == "CH"));
        Assert.IsNotNull(result.Single(l => l is Country country && country.Code == "CL"));
    }

    [TestMethod]
    [DataRow("CH#678")]
    [DataRow("ch678")]
    [DataRow("CH6")]
    [DataRow("ch#6")]
    public async Task TestStandard_Servers_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(1, result);
        Assert.IsNotNull(result.Single(l => l is Server server && server.Name == "CH#678"));
    }

    [TestMethod]
    [DataRow("US-")]
    [DataRow("us-")]
    public async Task TestStandard_ServerPrefix_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(5, result);
        Assert.IsTrue(result.All(l => l is Server server && server.Name.StartsWith("US-", StringComparison.InvariantCultureIgnoreCase)));
    }

    [TestMethod]
    [DataRow("456")]
    [DataRow("#456")]
    public async Task TestStandard_ServerNumber_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(2, result);
        Assert.IsNotNull(result.Single(l => l is Server server && server.Name == "US-FL#456"));
        Assert.IsNotNull(result.Single(l => l is Server server && server.Name == "PK#456"));
    }

    [TestMethod]
    [DataRow("FL#456")]
    [DataRow("fl456")]
    [DataRow("US#")]
    [DataRow("#")]
    public async Task TestStandard_Servers_NotStartsWith_DoesNotReturn_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.IsEmpty(result);
    }

    [TestMethod]
    [DataRow("United States", "US", 6)]
    [DataRow("Alaska", "US-AK#345", 2)]
    [DataRow("Anchorage", "US-AK#345", 2)]
    public async Task TestStandard_ExactLocation_ReturnsMatchingServers_Async(
        string input,
        string expectedServerOrCountry,
        int expectedCount)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(expectedCount, result);
        Assert.IsTrue(result.Any(l =>
            l is Server server && server.Name == expectedServerOrCountry
            || l is Country country && country.Code == expectedServerOrCountry));
    }

    [TestMethod]
    [DataRow("united")]
    [DataRow("UNITED ")]
    [DataRow("nited")]
    [DataRow(" NiTeD ")]
    public async Task TestStandard_UnitedCountries_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(2, result);
        Assert.IsNotNull(result.Single(l => l is Country country && country.Code == "AE"));
        Assert.IsNotNull(result.Single(l => l is Country country && country.Code == "US"));
    }

    [TestMethod]
    [DataRow("state")]
    [DataRow("STATES")]
    [DataRow(" states")]
    [DataRow(" sTaTeS ")]
    [DataRow("united state")]
    [DataRow("nited s")]
    [DataRow("Nited State")]
    [DataRow("D S")]
    public async Task TestStandard_UnitedStatesName_Async(string input)
    {
        List<ILocation> result = await _globalSearch!.SearchAsync(input);

        Assert.IsNotNull(result);
        Assert.HasCount(1, result);
        Assert.IsNotNull(result.Single(l => l is Country country && country.Code == "US"));
    }
}
