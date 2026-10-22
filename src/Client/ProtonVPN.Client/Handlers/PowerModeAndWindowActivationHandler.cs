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

using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using ProtonVPN.Client.Contracts.Messages;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Handlers.Bases;
using ProtonVPN.Client.Logic.Auth.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.Client.Logic.Users.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Common.Core.Extensions;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.AppLogs;
using ProtonVPN.OperatingSystems.PowerEvents.Contracts;
using ProtonVPN.StatisticalEvents.Contracts.Dimensions;

namespace ProtonVPN.Client.Handlers;

public class PowerModeAndWindowActivationHandler : IHandler, IEventMessageReceiver<MainWindowVisibilityChangedMessage>, IEventMessageReceiver, IRecipient<MainWindowVisibilityChangedMessage>
{
    private readonly ILogger _logger;

    private readonly ISettings _settings;

    private readonly IVpnServiceCaller _vpnServiceCaller;

    private readonly IConnectionCertificateManager _connectionCertificateManager;

    private readonly IUserAuthenticator _userAuthenticator;

    private readonly IConnectionManager _connectionManager;

    private readonly IVpnPlanUpdater _vpnPlanUpdater;

    private readonly IServersUpdater _serversUpdater;

    private readonly IConfiguration _config;

    public PowerModeAndWindowActivationHandler(ILogger logger, ISettings settings, IVpnServiceCaller vpnServiceCaller, IConnectionCertificateManager connectionCertificateManager, IUserAuthenticator userAuthenticator, IPowerEventNotifier powerEventNotifier, IConnectionManager connectionManager, IVpnPlanUpdater vpnPlanUpdater, IServersUpdater serversUpdater, IConfiguration config)
    {
        _logger = logger;
        _settings = settings;
        _vpnServiceCaller = vpnServiceCaller;
        _connectionCertificateManager = connectionCertificateManager;
        _userAuthenticator = userAuthenticator;
        _connectionManager = connectionManager;
        _vpnPlanUpdater = vpnPlanUpdater;
        _serversUpdater = serversUpdater;
        _config = config;
        powerEventNotifier.OnResume += OnResume;
    }

    public void Receive(MainWindowVisibilityChangedMessage message)
    {
        if (_userAuthenticator.IsLoggedIn && message.IsMainWindowVisible)
        {
            _logger.Debug<AppLog>("Main window activated while logged in");
            OnWindowActivationAsync().FireAndForget();
        }
    }

    private void OnResume(object? sender, EventArgs e)
    {
        if (!_userAuthenticator.IsLoggedIn)
        {
            _logger.Info<AppLog>("Resuming from sleep while logged out");
            return;
        }
        _logger.Info<AppLog>("Resuming from sleep while logged in");
        OnResumeAsync().FireAndForget();
    }

    private async Task OnWindowActivationAsync()
    {
        await RepeatServiceStateAsync();
        await RefreshPlanAndCertificateAsync();
    }

    private async Task OnResumeAsync()
    {
        await RepeatServiceStateAsync();
        await RefreshWithinTimeoutAsync();
        if (_connectionManager.IsConnected)
        {
            await ReconnectAsync();
        }
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await _connectionManager.ReconnectAsync(VpnTriggerDimension.Auto);
        }
        catch (Exception exception)
        {
            _logger.Error<AppLog>("Failed to reconnect after resuming from sleep.", exception);
        }
    }

    private async Task RepeatServiceStateAsync()
    {
        await _vpnServiceCaller.RepeatStateAsync();
        if (_settings.IsPortForwardingEnabled)
        {
            await _vpnServiceCaller.RepeatPortForwardingStateAsync();
        }
    }

    private async Task RefreshWithinTimeoutAsync()
    {
        try
        {
            if (!(await Task.WhenAll(RefreshPlanAndCertificateAsync(), _serversUpdater.UpdateAsync()).WaitOrFireAndForgetAsync(_config.ResumeRefreshTimeout)))
            {
                _logger.Warn<AppLog>("The plan, the certificate and the servers were not refreshed in time, reconnecting with the cached ones.");
            }
        }
        catch (Exception exception)
        {
            _logger.Error<AppLog>("Failed to refresh the plan, the certificate and the servers after resuming from sleep.", exception);
        }
    }

    private async Task RefreshPlanAndCertificateAsync()
    {
        await Task.WhenAll(_vpnPlanUpdater.UpdateAsync(), _connectionCertificateManager.RequestNewCertificateAsync());
    }
}
