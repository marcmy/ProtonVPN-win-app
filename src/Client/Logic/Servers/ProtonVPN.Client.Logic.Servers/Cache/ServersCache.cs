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

using ProtonVPN.Api.Contracts;
using ProtonVPN.Api.Contracts.Servers;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Extensions;
using ProtonVPN.Client.Logic.Servers.Contracts.Messages;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;
using ProtonVPN.Client.Logic.Servers.Files;
using ProtonVPN.Client.Logic.Servers.Loads;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Common.Core.Geographical;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.EntityMapping.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.ApiLogs;
using ProtonVPN.Logging.Contracts.Events.AppLogs;

namespace ProtonVPN.Client.Logic.Servers.Cache;

public class ServersCache : IServersCache
{
    private static readonly TimeSpan _updateIntervalTolerance = TimeSpan.FromSeconds(5);

    private readonly IApiClient _apiClient;
    private readonly IEntityMapper _entityMapper;
    private readonly IServersFileReaderWriter _serversFileReaderWriter;
    private readonly IEventMessageSender _eventMessageSender;
    private readonly IConfiguration _config;
    private readonly ISettings _settings;
    private readonly ILogger _logger;
    private readonly IFavoriteServersStorage _favoriteServersStorage;
    private readonly IServerLoadsCalculator _serverLoadsCalculator;

    private readonly ReaderWriterLockSlim _lock = new();

    private DeviceLocation _serversDeviceLocation = DeviceLocation.Unknown;
    private sbyte? _serversUserMaxTier;
    private byte[]? _statusFile;

    private DeviceLocation ServersDeviceLocation => GetWithReadLock(() => _serversDeviceLocation);
    private sbyte? ServersUserMaxTier => GetWithReadLock(() => _serversUserMaxTier);
    private byte[]? StatusFile => GetWithReadLock(() => _statusFile);
    private IReadOnlyList<Server> AllServers => GetWithReadLock(() => _originalServers);

    private bool _hasServersRequestFailed;

    private DateTime _lastFullUpdateUtc = DateTime.MinValue;
    private DateTime _lastLoadsUpdateUtc = DateTime.MinValue;

    private IReadOnlyList<Server> _originalServers = [];

    private IReadOnlyList<Server> _filteredServers = [];
    public IReadOnlyList<Server> Servers => GetWithReadLock(() => _filteredServers);

    private IReadOnlyList<FreeCountry> _freeCountries = [];
    public IReadOnlyList<FreeCountry> FreeCountries => GetWithReadLock(() => _freeCountries);

    private IReadOnlyList<Country> _countries = [];
    public IReadOnlyList<Country> Countries => GetWithReadLock(() => _countries);

    private IReadOnlyList<State> _states = [];
    public IReadOnlyList<State> States => GetWithReadLock(() => _states);

    private IReadOnlyList<City> _cities = [];
    public IReadOnlyList<City> Cities => GetWithReadLock(() => _cities);

    private IReadOnlyList<Gateway> _gateways = [];
    public IReadOnlyList<Gateway> Gateways => GetWithReadLock(() => _gateways);

    private IReadOnlyList<SecureCoreCountryPair> _secureCoreCountryPairs = [];
    public IReadOnlyList<SecureCoreCountryPair> SecureCoreCountryPairs => GetWithReadLock(() => _secureCoreCountryPairs);

    public ServersCache(IApiClient apiClient,
        IEntityMapper entityMapper,
        IServersFileReaderWriter serversFileReaderWriter,
        IEventMessageSender eventMessageSender,
        IConfiguration config,
        ISettings settings,
        ILogger logger,
        IFavoriteServersStorage favoriteServersLoader,
        IServerLoadsCalculator serverLoadsCalculator)
    {
        _apiClient = apiClient;
        _entityMapper = entityMapper;
        _serversFileReaderWriter = serversFileReaderWriter;
        _eventMessageSender = eventMessageSender;
        _config = config;
        _settings = settings;
        _logger = logger;
        _favoriteServersStorage = favoriteServersLoader;
        _serverLoadsCalculator = serverLoadsCalculator;
    }

    public bool IsEmpty()
    {
        return Servers is null || Servers.Count == 0;
    }

    public bool AreAllServersUnderMaintenance()
    {
        return Servers.All(s => s.IsUnderMaintenance());
    }

    public bool IsStale()
    {
        DeviceLocation location = ServersDeviceLocation;
        return location.HasCountryChanged(_settings.DeviceLocation)
            || location.HasIpAddressChanged(_settings.DeviceLocation)
            || ServersUserMaxTier != _settings.VpnPlan.MaxTier;
    }

    public bool IsLoadStale()
    {
        return ServersDeviceLocation.HaveCoordinatesChanged(_settings.DeviceLocation);
    }

    public bool IsOutdated()
    {
        return DateTime.UtcNow - _lastFullUpdateUtc >= _config.ServerUpdateInterval - _updateIntervalTolerance;
    }

    public bool IsLoadOutdated()
    {
        return IsLoadOlderThan(_config.ServerLoadUpdateInterval - _updateIntervalTolerance);
    }

    public bool IsLoadOverdue()
    {
        return IsLoadOlderThan(_config.ServerLoadUpdateInterval * 1.5);
    }

    private bool IsLoadOlderThan(TimeSpan maxAge)
    {
        return StatusFile is null || DateTime.UtcNow - _lastLoadsUpdateUtc >= maxAge;
    }

    public bool HasServersRequestFailed()
    {
        return _hasServersRequestFailed;
    }

    public bool HasGatewaysAndNoCountries()
    {
        return Gateways.Any() && !Countries.Any();
    }

    public bool HasNoServers()
    {
#if DEBUG
        if (_settings.SkipNoConnectionsPage)
        {
            return false;
        }
#endif

        return IsEmpty() || AreAllServersUnderMaintenance();
    }

    private T GetWithReadLock<T>(Func<T> func)
    {
        _lock.EnterReadLock();
        try
        {
            return func();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void LoadFromFileIfEmpty()
    {
        if (IsEmpty())
        {
            _logger.Info<AppLog>("Cache is empty, loading servers from file.");

            ServersFile file = _serversFileReaderWriter.Read();
            DeviceLocation location = new()
            {
                CountryCode = file.DeviceCountryLocation ?? string.Empty,
                IpAddress = file.DeviceIpAddress ?? string.Empty,
            };
            ProcessServers(location, file.UserMaxTier, file.Servers, null);
        }
    }

    public void Clear()
    {
        _lock.EnterWriteLock();
        try
        {
            _lastFullUpdateUtc = DateTime.MinValue;
            _lastLoadsUpdateUtc = DateTime.MinValue;

            _serversDeviceLocation = DeviceLocation.Unknown;
            _serversUserMaxTier = null;
            _statusFile = null;

            _originalServers = [];
            _filteredServers = [];
            _freeCountries = [];
            _countries = [];
            _states = [];
            _cities = [];
            _gateways = [];
            _secureCoreCountryPairs = [];
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public async Task UpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            IEnumerable<string> favoriteServerIds = GetFavoriteServerIds();

            DeviceLocation? deviceLocation = _settings.DeviceLocation;
            DateTime utcNow = DateTime.UtcNow;

            ApiResponseResult<ServersResponse> response = await _apiClient.GetServersAsync(
                deviceLocation,
                favoriteServerIds,
                cancellationToken);

            if (response.Success)
            {
                _hasServersRequestFailed = false;

                if (response.IsNotModified)
                {
                    _logger.Info<ApiLog>("API: Get servers response was not modified since last call, using cached data.");
                    _lastFullUpdateUtc = utcNow;
                }
                else
                {
                    _logger.Info<ApiLog>("API: Get servers response was modified since last call, updating cached data.");

                    List<Server> servers = _entityMapper.Map<LogicalServerResponse, Server>(response.Value.Servers);

                    // Handle race condition when new favorite servers are added between API request and response
                    if (response.Value.ResponseMetadata is not null && response.Value.ResponseMetadata.ListIsTruncated && favoriteServerIds.Any())
                    {
                        IEnumerable<string> favoriteServerIdsUpdated = GetFavoriteServerIds();
                        IEnumerable<string> preserveIds = favoriteServerIdsUpdated.Except(favoriteServerIds);
                        if (preserveIds.Any())
                        {
                            servers.AddRange(_originalServers.Where(s => preserveIds.Contains(s.Id)));
                        }
                    }

                    byte[]? statusFile = await UpdateBinaryLoadsAsync(servers, response.Value.StatusId, deviceLocation, cancellationToken);
                    if (statusFile is null)
                    {
                        _logger.Warn<ApiLog>("Loads were not updated, the server list is discarded.");
                        return;
                    }

                    sbyte userMaxTier = _settings.VpnPlan.MaxTier;

                    SaveToFile(deviceLocation ?? DeviceLocation.Unknown, userMaxTier, servers);
                    ProcessServers(deviceLocation ?? DeviceLocation.Unknown, userMaxTier, servers, statusFile);
                    _settings.LastLogicalsStatusId = response.Value.StatusId;
                    _lastFullUpdateUtc = utcNow;
                    _lastLoadsUpdateUtc = utcNow;
                    if (response.LastModified.HasValue)
                    {
                        _settings.LogicalsLastModifiedDate = response.LastModified.Value;
                    }
                }
            }
            else
            {
                _hasServersRequestFailed = true;
            }
        }
        catch (Exception e)
        {
            _logger.Error<ApiErrorLog>("API: Get servers failed", e);

            _hasServersRequestFailed = true;

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private IEnumerable<string> GetFavoriteServerIds()
    {
        return _favoriteServersStorage.Get();
    }

    private async Task<byte[]?> GetServerStatusAndLoadFileAsync(string statusId, CancellationToken cancellationToken)
    {
        try
        {
            ApiResponseResult<byte[]> response = await _apiClient.GetServerLoadsAndStatusBinaryStringAsync(statusId, cancellationToken);
            return response.Success
                ? response.Value
                : null;
        }
        catch (Exception e)
        {
            _logger.Error<ApiErrorLog>("API: Get binary status file failed", e);
            return null;
        }
    }

    public async Task UpdateLoadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTime utcNow = DateTime.UtcNow;
            DeviceLocation? currentLocation = _settings.DeviceLocation;
            List<Server> servers = AllServers.ToList();
            byte[]? statusFile = await UpdateBinaryLoadsAsync(servers, _settings.LastLogicalsStatusId, currentLocation, cancellationToken);
            if (statusFile is null)
            {
                _logger.Warn<ApiLog>("Loads were not updated.");
                return;
            }

            DeviceLocation location = WithCoordinatesOf(ServersDeviceLocation, currentLocation);
            sbyte? userMaxTier = ServersUserMaxTier;

            SaveToFile(location, userMaxTier, servers);
            ProcessServers(location, userMaxTier, servers, statusFile);
            _lastLoadsUpdateUtc = utcNow;
        }
        catch (Exception e)
        {
            _logger.Error<ApiErrorLog>("Update server loads failed", e);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    public void RecomputeLoads()
    {
        try
        {
            byte[]? statusFile = StatusFile;
            if (statusFile is null)
            {
                _logger.Info<AppLog>("Scores were not recomputed, no status file has been retained yet.");
                return;
            }

            List<Server> servers = AllServers.ToList();
            if (servers.Count == 0)
            {
                return;
            }

            DeviceLocation? currentLocation = _settings.DeviceLocation;
            if (!_serverLoadsCalculator.UpdateServerLoads(servers, statusFile, currentLocation))
            {
                _logger.Warn<AppLog>("Scores were not recomputed for the new device coordinates.");
                return;
            }

            DeviceLocation location = WithCoordinatesOf(ServersDeviceLocation, currentLocation);
            sbyte? userMaxTier = ServersUserMaxTier;
            SaveToFile(location, userMaxTier, servers);
            ProcessServers(location, userMaxTier, servers, statusFile);
        }
        catch (Exception e)
        {
            _logger.Error<AppLog>("Failed to recompute the server scores", e);
        }
    }

    private static DeviceLocation WithCoordinatesOf(DeviceLocation location, DeviceLocation? currentLocation)
    {
        return new DeviceLocation
        {
            CountryCode = location.CountryCode,
            IpAddress = location.IpAddress,
            Isp = location.Isp,
            Latitude = currentLocation?.Latitude,
            Longitude = currentLocation?.Longitude,
        };
    }

    private async Task<byte[]?> UpdateBinaryLoadsAsync(List<Server> servers, string? statusId,
        DeviceLocation? deviceLocation, CancellationToken cancellationToken)
    {
        if (statusId is null)
        {
            _logger.Warn<AppLog>("Cannot make the API request because the logicals status id is null.");
            return null;
        }

        byte[]? statusFile = await GetServerStatusAndLoadFileAsync(statusId, cancellationToken);
        if (statusFile is null)
        {
            _logger.Warn<AppLog>("Cached server data was not updated, because status file is missing.");
            return null;
        }

        bool result = _serverLoadsCalculator.UpdateServerLoads(servers, statusFile, deviceLocation);
        if (!result)
        {
            _logger.Warn<AppLog>("Cached server data was not updated, because server status and loads were not updated.");
            return null;
        }

        return statusFile;
    }

    private void ProcessServers(DeviceLocation deviceLocation, sbyte? userMaxTier, IReadOnlyList<Server> servers, byte[]? statusFile)
    {
        SetIpv6Flags(servers);

        IReadOnlyList<FreeCountry> freeCountries = GetFreeCountries(servers);
        IReadOnlyList<Country> countries = GetCountries(servers);
        IReadOnlyList<State> states = GetStates(servers);
        IReadOnlyList<City> cities = GetCities(servers);
        IReadOnlyList<Gateway> gateways = GetGateways(servers);
        IReadOnlyList<SecureCoreCountryPair> secureCoreCountryPairs = GetSecureCoreCountryPairs(servers);
        IReadOnlyList<Server> filteredServers = GetFilteredServers(servers);

        _lock.EnterWriteLock();
        try
        {
            _serversDeviceLocation = deviceLocation;
            _serversUserMaxTier = userMaxTier;
            _statusFile = statusFile;

            _originalServers = servers;
            _filteredServers = filteredServers;
            _freeCountries = freeCountries;
            _countries = countries;
            _states = states;
            _cities = cities;
            _gateways = gateways;
            _secureCoreCountryPairs = secureCoreCountryPairs;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _eventMessageSender.Send(new ServerListChangedMessage());
    }

    private static void SetIpv6Flags(IEnumerable<Server> servers)
    {
        foreach (Server server in servers)
        {
            if (server.Servers is null)
            {
                continue;
            }

            foreach (PhysicalServer physicalServer in server.Servers)
            {
                physicalServer.IsIpv6Supported = server.Features.IsSupported(ServerFeatures.Ipv6);
            }
        }
    }

    private IReadOnlyList<FreeCountry> GetFreeCountries(IEnumerable<Server> servers)
    {
        return servers
            .Where(s => !string.IsNullOrWhiteSpace(s.ExitCountry)
                     && s.IsFreeNonB2B())
            .GroupBy(s => s.ExitCountry)
            .Select(g => new FreeCountry()
            {
                Code = g.Key,
                IsLocationUnderMaintenance = IsUnderMaintenance(g)
            })
            .ToList();
    }

    private IReadOnlyList<Country> GetCountries(IEnumerable<Server> servers)
    {
        return servers
            .Where(s => !string.IsNullOrWhiteSpace(s.ExitCountry)
                     && s.IsPaidNonB2B())
            .GroupBy(s => s.ExitCountry)
            .Select(g => new Country()
            {
                Code = g.Key,
                Features = AggregateFeatures(g),
                IsStandardUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsStandard()),
                IsP2PUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsSupported(ServerFeatures.P2P)),
                IsSecureCoreUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsSupported(ServerFeatures.SecureCore)),
                IsTorUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsSupported(ServerFeatures.Tor))
            })
            .ToList();
    }

    private ServerFeatures AggregateFeatures<T>(IGrouping<T, Server> servers)
    {
        return servers.Aggregate(default(ServerFeatures), (combinedFeatures, s) => combinedFeatures | s.Features);
    }

    private bool IsUnderMaintenance<T>(IGrouping<T, Server> servers, Func<Server, bool>? filterFunc = null)
    {
        return !servers.Any(s => (filterFunc == null || filterFunc(s))
                              && !s.IsUnderMaintenance());
    }

    private IReadOnlyList<State> GetStates(IReadOnlyList<Server> servers)
    {
        return servers
            .Where(s => !string.IsNullOrWhiteSpace(s.ExitCountry)
                     && !string.IsNullOrWhiteSpace(s.State)
                     && s.IsPaidNonB2B())
            .GroupBy(s => new { Country = s.ExitCountry, s.State })
            .Select(g => new State()
            {
                CountryCode = g.Key.Country,
                Name = g.Key.State,
                Features = AggregateFeatures(g),
                IsStandardUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsStandard()),
                IsP2PUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsSupported(ServerFeatures.P2P)),
                IsSecureCoreUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsSupported(ServerFeatures.SecureCore)),
                IsTorUnderMaintenance = IsUnderMaintenance(g, s => s.Features.IsSupported(ServerFeatures.Tor))
            })
            .ToList();
    }

    private IReadOnlyList<City> GetCities(IReadOnlyList<Server> servers)
    {
        return servers
            .Where(s => !string.IsNullOrWhiteSpace(s.ExitCountry)
                     && !string.IsNullOrWhiteSpace(s.City)
                     && s.IsPaidNonB2B())
            .GroupBy(s => new { Country = s.ExitCountry, s.City })
            .SelectMany(cityGroup =>
            {
                List<string> distinctStates = cityGroup
                    .Select(s => s.State)
                    .Where(state => !string.IsNullOrWhiteSpace(state))
                    .Distinct()
                    .ToList();

                // 0 or 1 distinct non-null states => merge everything into one state
                if (distinctStates.Count <= 1)
                {
                    return
                    [
                        CreateCity(countryCode: cityGroup.Key.Country,
                                   stateName: distinctStates.FirstOrDefault(),
                                   cityName: cityGroup.Key.City,
                                   servers: cityGroup)
                    ];
                }

                // 2+ distinct states => keep them all separate including null states
                return cityGroup
                    .GroupBy(s => string.IsNullOrWhiteSpace(s.State) ? null : s.State)
                    .Select(stateGroup => CreateCity(
                        countryCode: cityGroup.Key.Country,
                        stateName: stateGroup.Key,
                        cityName: cityGroup.Key.City,
                        servers: stateGroup));
            })
            .ToList();
    }

    private City CreateCity<T>(string countryCode, string? stateName, string cityName, IGrouping<T, Server> servers)
    {
        return new City
        {
            CountryCode = countryCode,
            StateName = stateName,
            Name = cityName,
            Features = AggregateFeatures(servers),
            IsStandardUnderMaintenance = IsUnderMaintenance(servers, s => s.Features.IsStandard()),
            IsP2PUnderMaintenance = IsUnderMaintenance(servers, s => s.Features.IsSupported(ServerFeatures.P2P)),
            IsSecureCoreUnderMaintenance = IsUnderMaintenance(servers, s => s.Features.IsSupported(ServerFeatures.SecureCore)),
            IsTorUnderMaintenance = IsUnderMaintenance(servers, s => s.Features.IsSupported(ServerFeatures.Tor))
        };
    }

    private IReadOnlyList<Gateway> GetGateways(IReadOnlyList<Server> servers)
    {
        return servers
            .Where(s => s.Features.IsB2B()
                     && !string.IsNullOrWhiteSpace(s.GatewayName))
            .GroupBy(s => s.GatewayName)
            .Select(g => new Gateway()
            {
                Name = g.Key,
                IsLocationUnderMaintenance = IsUnderMaintenance(g)
            })
            .ToList();
    }

    private IReadOnlyList<SecureCoreCountryPair> GetSecureCoreCountryPairs(IReadOnlyList<Server> servers)
    {
        return servers
            .Where(s => s.Features.IsSupported(ServerFeatures.SecureCore)
                     && !string.IsNullOrWhiteSpace(s.EntryCountry)
                     && !string.IsNullOrWhiteSpace(s.ExitCountry))
            .GroupBy(s => new { s.EntryCountry, s.ExitCountry })
            .Select(g => new SecureCoreCountryPair()
            {
                EntryCountry = g.Key.EntryCountry,
                ExitCountry = g.Key.ExitCountry,
                IsLocationUnderMaintenance = IsUnderMaintenance(g)
            })
            .ToList();
    }

    private IReadOnlyList<Server> GetFilteredServers(IReadOnlyList<Server> servers)
    {
        ServerTiers maxTier = (ServerTiers)_settings.VpnPlan.MaxTier;

        List<Server> filteredServers = [];
        foreach (Server server in servers.Where(s => s.IsVisible))
        {
            if (server.Tier <= maxTier)
            {
                // Add all the servers the user can access (based on his plan)
                filteredServers.Add(server);
            }
            else if (server.Tier <= ServerTiers.Plus)
            {
                // Include all the servers the user cannot access (but without the physical servers)
                filteredServers.Add(server.CopyWithoutPhysicalServers());
            }
        }
        return filteredServers;
    }

    private void SaveToFile(DeviceLocation deviceLocation, sbyte? userMaxTier, List<Server> servers)
    {
        ServersFile serversFile = new()
        {
            DeviceCountryLocation = deviceLocation.CountryCode,
            DeviceIpAddress = deviceLocation.IpAddress,
            UserMaxTier = userMaxTier,
            Servers = servers,
        };
        _serversFileReaderWriter.Save(serversFile);
    }

    public void ReprocessServers()
    {
        _logger.Info<AppLog>("Reprocessing servers.");
        ProcessServers(ServersDeviceLocation, ServersUserMaxTier, AllServers, StatusFile);
    }

    public async Task<ApiResponseResult<LookupServerResponse>?> LookupAsync(string input)
    {
        LoadFromFileIfEmpty();
        try
        {
            DeviceLocation? deviceLocation = _settings.DeviceLocation;
            ApiResponseResult<LookupServerResponse> response = await _apiClient.GetServerByNameAsync(input, deviceLocation);
            if (response.Success)
            {
                Server server = _entityMapper.Map<LogicalServerResponse, Server>(response.Value.LogicalServer);
                List<Server> servers = _originalServers.ToList();
                Server? alreadyExistingServer = servers.FirstOrDefault(s => s.Id == server.Id);
                if (alreadyExistingServer is not null)
                {
                    servers.Remove(alreadyExistingServer);
                }
                servers.Add(server);

                DeviceLocation cachedLocation = ServersDeviceLocation;
                sbyte? userMaxTier = ServersUserMaxTier;

                SaveToFile(cachedLocation, userMaxTier, servers);
                ProcessServers(cachedLocation, userMaxTier, servers, StatusFile);
            }
            else
            {
                _logger.Warn<ApiLog>($"API: Get server by name returned with Code: {response.ResponseMessage?.StatusCode}, Error: {response.Error}");
            }

            return response;
        }
        catch (Exception e)
        {
            _logger.Error<ApiErrorLog>("API: Get server by name failed", e);
        }

        return null;
    }

    public IEnumerable<KeyValuePair<string, dynamic?>> GetStats()
    {
        _lock.EnterReadLock();
        IReadOnlyList<Server> servers;
        IReadOnlyList<Server> filteredServers;
        IReadOnlyList<Country> countries;
        IReadOnlyList<FreeCountry> freeCountries;
        IReadOnlyList<State> states;
        IReadOnlyList<City> cities;
        IReadOnlyList<Gateway> gateways;
        IReadOnlyList<SecureCoreCountryPair> secureCoreCountryPairs;
        try
        {
            servers = _originalServers;
            filteredServers = _filteredServers;
            countries = _countries;
            freeCountries = _freeCountries;
            states = _states;
            cities = _cities;
            gateways = _gateways;
            secureCoreCountryPairs = _secureCoreCountryPairs;
        }
        finally
        {
            _lock.ExitReadLock();
        }
        List<PhysicalServer> physicalServers = servers.SelectMany((Server s) => s.Servers).ToList();
        yield return new("Logical servers", servers.Count);
        yield return new("Logical servers under maintenance", servers.Count((Server s) => s.IsUnderMaintenance()));
        yield return new("Logical servers visible", servers.Count((Server s) => s.IsVisible));
        yield return new("Logical servers auto connectable", servers.Count((Server s) => s.IsAutoconnectable));
        yield return new("Logical servers filtered", filteredServers.Count);
        yield return new("Free logical servers", servers.Count((Server s) => s.IsFree()));
        yield return new("Paid logical servers", servers.Count((Server s) => s.IsPaid()));
        yield return new("B2B logical servers", servers.Count((Server s) => s.IsB2B()));
        yield return new("Physical servers", physicalServers.Count);
        yield return new("Physical servers under maintenance", physicalServers.Count((PhysicalServer ps) => ps.IsUnderMaintenance()));
        yield return new("Physical servers with relays", physicalServers.Count((PhysicalServer ps) => ps.RelayIpByProtocol.Count > 0));
        yield return new("Countries", countries.Count);
        yield return new("Free countries", freeCountries.Count);
        yield return new("States", states.Count);
        yield return new("Cities", cities.Count);
        yield return new("Gateways", gateways.Count);
        yield return new("Secure core country pairs", secureCoreCountryPairs.Count);
    }
}
