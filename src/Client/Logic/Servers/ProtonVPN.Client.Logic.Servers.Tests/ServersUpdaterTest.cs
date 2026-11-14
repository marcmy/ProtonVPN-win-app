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
using ProtonVPN.Client.Logic.Servers.Cache;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.IssueReporting.Contracts;
using ProtonVPN.Logging.Contracts;

namespace ProtonVPN.Client.Logic.Servers.Tests;

[TestClass]
public class ServersUpdaterTest
{
    private IServersCache _cache = null!;
    private IConfiguration _config = null!;
    private ServersUpdater _updater = null!;

    [TestInitialize]
    public void Initialize()
    {
        _cache = Substitute.For<IServersCache>();
        _config = Substitute.For<IConfiguration>();
        _config.OverdueServerLoadUpdateTimeout.Returns(TimeSpan.FromSeconds(1));
        _updater = new ServersUpdater(Substitute.For<ILogger>(), Substitute.For<IIssueReporter>(), _cache,
            Substitute.For<IServerCountCache>(), Substitute.For<ISettings>(), _config);
    }

    [TestMethod]
    public async Task FullUpdate_StillRefreshesOutdatedLoadsAfterNotModifiedResponse()
    {
        _cache.IsOutdated().Returns(true);
        _cache.IsLoadOutdated().Returns(true);

        await _updater.UpdateAsync(CancellationToken.None);

        await _cache.Received(1).UpdateAsync(CancellationToken.None);
        await _cache.Received(1).UpdateLoadsAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task CoordinatesOnlyChange_RecomputesWithoutNetworkRefresh()
    {
        _cache.IsLoadStale().Returns(true);

        await _updater.UpdateAsync(CancellationToken.None);

        _cache.Received(1).RecomputeLoads();
        await _cache.DidNotReceive().UpdateAsync(Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().UpdateLoadsAsync(Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task OverdueRefresh_StopsWaitingAtDeadlineButFinishesInBackground()
    {
        _cache.IsLoadOverdue().Returns(true);
        _config.OverdueServerLoadUpdateTimeout.Returns(TimeSpan.FromMilliseconds(20));
        TaskCompletionSource refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _cache.UpdateLoadsAsync(Arg.Any<CancellationToken>()).Returns(refresh.Task);

        try
        {
            await _updater.UpdateOverdueLoadsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(refresh.Task.IsCompleted);
            _ = _cache.Received(1).UpdateLoadsAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            refresh.TrySetResult();
        }
        // Waiting for this serialized operation also proves the background refresh released its semaphore.
        await _updater.ClearCacheAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task FreshOrEmptyCache_DoesNotStartOverdueRefresh()
    {
        await _updater.UpdateOverdueLoadsAsync();
        _cache.IsEmpty().Returns(true);
        _cache.IsLoadOverdue().Returns(true);
        await _updater.UpdateOverdueLoadsAsync();

        await _cache.DidNotReceive().UpdateLoadsAsync(Arg.Any<CancellationToken>());
    }
}
