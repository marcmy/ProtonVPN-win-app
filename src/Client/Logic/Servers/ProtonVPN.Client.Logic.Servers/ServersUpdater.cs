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

using ProtonVPN.Client.Logic.Servers.Cache;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Common.Core.Extensions;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.IssueReporting.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.AppLogs;

namespace ProtonVPN.Client.Logic.Servers;

public class ServersUpdater : IServersUpdater
{
    private readonly ILogger _logger;

    private readonly IIssueReporter _issueReporter;

    private readonly IServersCache _serversCache;

    private readonly IServerCountCache _serverCountCache;

    private readonly ISettings _settings;

    private readonly IConfiguration _config;

    private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

    public ServersUpdater(ILogger logger, IIssueReporter issueReporter, IServersCache serversCache, IServerCountCache serverCountCache, ISettings settings, IConfiguration config)
    {
        _logger = logger;
        _issueReporter = issueReporter;
        _serversCache = serversCache;
        _serverCountCache = serverCountCache;
        _settings = settings;
        _config = config;
    }

    public async Task UpdateAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _logger.Info<AppLog>("Server update requested");
            _serversCache.LoadFromFileIfEmpty();
            bool isServersCacheInvalid = IsServersCacheInvalid();
            bool isServersUpdateNeeded = IsServersUpdateNeeded();
            if (isServersCacheInvalid)
            {
                _logger.Info<AppLog>("Server cache is invalid, forcing full update");
                await ForceUpdateServersAsync(cancellationToken);
            }
            else if (isServersUpdateNeeded)
            {
                _logger.Info<AppLog>("Server cache is outdated, updating");
                await UpdateServersAsync(cancellationToken);
            }
            bool isLoadsUpdateNeeded = IsLoadsUpdateNeeded();
            if (isLoadsUpdateNeeded)
            {
                _logger.Info<AppLog>("Load cache is outdated, updating");
                await _serversCache.UpdateLoadsAsync(cancellationToken);
            }
            bool flag = IsLoadsRecomputeNeeded();
            if (flag)
            {
                _logger.Info<AppLog>("Device coordinates changed, recomputing the server scores");
                _serversCache.RecomputeLoads();
            }
            if (!isServersCacheInvalid && !isServersUpdateNeeded && !isLoadsUpdateNeeded && !flag)
            {
                _logger.Info<AppLog>("No server update needed, using cached servers");
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task UpdateOverdueLoadsAsync()
    {
        try
        {
            if (IsLoadsOverdue())
            {
                _logger.Info<AppLog>("Server loads are overdue, updating");
                _issueReporter.CaptureMessage("Overdue server loads detected");
                if (!(await UpdateLoadsIfOverdueAsync(CancellationToken.None).WaitOrFireAndForgetAsync(_config.OverdueServerLoadUpdateTimeout)))
                {
                    _logger.Warn<AppLog>("Server loads are overdue but were not updated in time, using the cached ones.");
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Error<AppLog>("Failed to update the overdue server loads, using the cached ones.", exception);
        }
    }

    public async Task ForceUpdateAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _logger.Info<AppLog>("Force server update requested");
            _serversCache.LoadFromFileIfEmpty();
            await ForceUpdateServersAsync(cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ForceUpdateLoadsAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _logger.Info<AppLog>("Force loads update requested");
            _serversCache.LoadFromFileIfEmpty();
            if (_serversCache.IsEmpty())
            {
                _logger.Info<AppLog>("Server cache is empty, skipping the loads update");
            }
            else
            {
                await _serversCache.UpdateLoadsAsync(cancellationToken);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            _logger.Info<AppLog>("Clear server cache requested");
            _serversCache.Clear();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public bool HasServersRequestFailed()
    {
        return _serversCache.HasServersRequestFailed();
    }

    private bool IsLoadsOverdue()
    {
        if (!_serversCache.IsEmpty())
        {
            return _serversCache.IsLoadOverdue();
        }
        return false;
    }

    private async Task UpdateLoadsIfOverdueAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (IsLoadsOverdue())
            {
                await _serversCache.UpdateLoadsAsync(cancellationToken);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private bool IsServersCacheInvalid()
    {
        if (!_serversCache.IsEmpty())
        {
            return _serversCache.IsStale();
        }
        return true;
    }

    private bool IsServersUpdateNeeded()
    {
        return _serversCache.IsOutdated();
    }

    private bool IsLoadsUpdateNeeded()
    {
        if (!_serversCache.IsEmpty())
        {
            return _serversCache.IsLoadOutdated();
        }
        return false;
    }

    private bool IsLoadsRecomputeNeeded()
    {
        if (!_serversCache.IsEmpty())
        {
            return _serversCache.IsLoadStale();
        }
        return false;
    }

    private Task ForceUpdateServersAsync(CancellationToken cancellationToken)
    {
        _settings.LogicalsLastModifiedDate = DefaultSettings.LogicalsLastModifiedDate;
        return UpdateServersAsync(cancellationToken);
    }

    private Task UpdateServersAsync(CancellationToken cancellationToken)
    {
        _serverCountCache.UpdateAsync().FireAndForget();
        return _serversCache.UpdateAsync(cancellationToken);
    }
}
