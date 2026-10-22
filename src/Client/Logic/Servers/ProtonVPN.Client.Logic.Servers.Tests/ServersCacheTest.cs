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
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN. If not, see <https://www.gnu.org/licenses/>.
 */


using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Api.Contracts;
using ProtonVPN.Api.Contracts.Servers;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Logic.Servers.Cache;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;
using ProtonVPN.Client.Logic.Servers.Files;
using ProtonVPN.Client.Logic.Servers.Loads;
using ProtonVPN.Client.Logic.Users.Contracts.Messages;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Common.Core.Geographical;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.EntityMapping.Contracts;
using ProtonVPN.Logging.Contracts;

namespace ProtonVPN.Client.Logic.Servers.Tests;

[TestClass]
public class ServersCacheTest
{
    private IApiClient _api = null!;
    private IEntityMapper _mapper = null!;
    private IServersFileReaderWriter _files = null!;
    private ISettings _settings = null!;
    private IServerLoadsCalculator _loads = null!;
    private ServersCache _cache = null!;

    private static DeviceLocation Location(string ip = "192.0.2.1", double latitude = 40) => new()
    {
        CountryCode = "US", IpAddress = ip, Latitude = latitude, Longitude = -74,
    };

    [TestInitialize]
    public void Initialize()
    {
        _api = Substitute.For<IApiClient>();
        _mapper = Substitute.For<IEntityMapper>();
        _files = Substitute.For<IServersFileReaderWriter>();
        _settings = Substitute.For<ISettings>();
        _settings.DeviceLocation.Returns(Location());
        _settings.VpnPlan.Returns(new VpnPlan("plus", "plus", 2, false));
        _settings.LastLogicalsStatusId.Returns("old-status");
        _loads = Substitute.For<IServerLoadsCalculator>();
        _loads.UpdateServerLoads(Arg.Any<IReadOnlyList<Server>>(), Arg.Any<byte[]>(), Arg.Any<DeviceLocation?>()).Returns(true);
        IConfiguration config = Substitute.For<IConfiguration>();
        config.ServerUpdateInterval.Returns(TimeSpan.FromHours(1));
        config.ServerLoadUpdateInterval.Returns(TimeSpan.FromMinutes(15));
        _cache = new ServersCache(_api, _mapper, _files, Substitute.For<IEventMessageSender>(), config,
            _settings, Substitute.For<ILogger>(), Substitute.For<IFavoriteServersStorage>(), _loads);
        _files.Read().Returns(new ServersFile
        {
            DeviceCountryLocation = "US", DeviceIpAddress = "192.0.2.1", UserMaxTier = 2,
            Servers = [Server("visible"), Server("hidden", false)],
        });
        _api.GetServerLoadsAndStatusBinaryStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ApiResponseResult<byte[]>.Ok(null!, [1, 2, 3]));
    }

    [TestMethod]
    public void SameCountryIpChange_InvalidatesFullList()
    {
        _cache.LoadFromFileIfEmpty();
        Assert.IsFalse(_cache.IsStale());

        _settings.DeviceLocation.Returns(Location("192.0.2.2"));

        Assert.IsTrue(_cache.IsStale());
    }

    [TestMethod]
    public async Task LoadRefresh_PreservesHiddenServersAndOriginalLocationIdentity()
    {
        _cache.LoadFromFileIfEmpty();
        _settings.DeviceLocation.Returns(Location("192.0.2.2", 41));

        await _cache.UpdateLoadsAsync(CancellationToken.None);

        _loads.Received(1).UpdateServerLoads(Arg.Is<IReadOnlyList<Server>>(servers => servers.Count == 2),
            Arg.Any<byte[]>(), Arg.Is<DeviceLocation?>(location => location!.Value.IpAddress == "192.0.2.2"));
        _files.Received(1).Save(Arg.Is<ServersFile>(file => file.Servers.Count == 2 && file.DeviceIpAddress == "192.0.2.1"));
        Assert.AreEqual(1, _cache.Servers.Count);
        Assert.IsTrue(_cache.IsStale(), "A loads-only refresh cannot validate the old IP-specific logical list.");
        Assert.IsFalse(_cache.IsLoadStale());
        Assert.IsFalse(_cache.IsLoadOverdue());
    }

    [TestMethod]
    public async Task FailedBinaryDownload_DoesNotCommitStatusIdListOrFreshness()
    {
        _cache.LoadFromFileIfEmpty();
        SetFullResponse();
        _api.GetServerLoadsAndStatusBinaryStringAsync("new-status", Arg.Any<CancellationToken>())
            .Returns(ApiResponseResult<byte[]>.Fail(null!, "unavailable"));

        await _cache.UpdateAsync(CancellationToken.None);

        Assert.AreEqual("old-status", _settings.LastLogicalsStatusId);
        Assert.AreEqual("visible", _cache.Servers.Single().Id);
        Assert.IsTrue(_cache.IsOutdated());
        Assert.IsTrue(_cache.IsLoadOverdue());
        _files.DidNotReceive().Save(Arg.Any<ServersFile>());
    }

    [TestMethod]
    public async Task NotModifiedFullResponse_DoesNotMarkLoadsFresh()
    {
        _cache.LoadFromFileIfEmpty();
        _api.GetServersAsync(Arg.Any<DeviceLocation?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ApiResponseResult<ServersResponse>.NotModified(null!));

        await _cache.UpdateAsync(CancellationToken.None);

        Assert.IsFalse(_cache.IsOutdated());
        Assert.IsTrue(_cache.IsLoadOutdated());
        Assert.IsTrue(_cache.IsLoadOverdue());
    }

    [TestMethod]
    public async Task CoordinateChange_ReusesRetainedBinaryWithoutApiDownload()
    {
        SetFullResponse();
        await _cache.UpdateAsync(CancellationToken.None);
        Assert.IsFalse(_cache.IsLoadStale());
        _settings.DeviceLocation.Returns(Location(latitude: 41));
        Assert.IsTrue(_cache.IsLoadStale());

        _cache.RecomputeLoads();

        Assert.IsFalse(_cache.IsStale());
        Assert.IsFalse(_cache.IsLoadStale());
        await _api.Received(1).GetServerLoadsAndStatusBinaryStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _loads.Received(2).UpdateServerLoads(Arg.Any<IReadOnlyList<Server>>(), Arg.Any<byte[]>(), Arg.Any<DeviceLocation?>());
    }

    private void SetFullResponse()
    {
        _api.GetServersAsync(Arg.Any<DeviceLocation?>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ApiResponseResult<ServersResponse>.Ok(null!, new ServersResponse { StatusId = "new-status", Servers = [] }));
        _mapper.Map<LogicalServerResponse, Server>(Arg.Any<IEnumerable<LogicalServerResponse>>())
            .Returns([Server("new")]);
    }

    private static Server Server(string id, bool visible = true) => new()
    {
        Id = id, Name = id, City = "New York", State = "NY", EntryCountry = "US", ExitCountry = "US",
        HostCountry = "US", Domain = "example.test", Servers = [], GatewayName = string.Empty,
        StatusReference = new(), EntryLocation = new(), ExitLocation = new(), Tier = ServerTiers.Plus,
        IsVisible = visible, Status = 1,
    };
}
