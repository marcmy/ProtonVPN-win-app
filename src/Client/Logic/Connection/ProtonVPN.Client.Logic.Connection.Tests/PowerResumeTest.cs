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


using NSubstitute;
using ProtonVPN.Client.Handlers;
using ProtonVPN.Client.Logic.Auth.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.Client.Logic.Users.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.OperatingSystems.PowerEvents.Contracts;
using ProtonVPN.StatisticalEvents.Contracts.Dimensions;

namespace ProtonVPN.Client.Logic.Connection.Tests;

[TestClass]
public class PowerResumeTest
{
    [TestMethod]
    public async Task Resume_RepeatsServiceStateAndRefreshesBeforeReconnecting()
    {
        IPowerEventNotifier power = Substitute.For<IPowerEventNotifier>();
        IVpnServiceCaller service = Substitute.For<IVpnServiceCaller>();
        IUserAuthenticator auth = Substitute.For<IUserAuthenticator>();
        auth.IsLoggedIn.Returns(true);
        IConnectionManager connection = Substitute.For<IConnectionManager>();
        connection.IsConnected.Returns(true);
        IServersUpdater servers = Substitute.For<IServersUpdater>();
        IConfiguration config = Substitute.For<IConfiguration>();
        config.ResumeRefreshTimeout.Returns(TimeSpan.FromSeconds(5));
        TaskCompletionSource state = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource refreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reconnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.RepeatStateAsync().Returns(state.Task);
        servers.UpdateAsync(Arg.Any<CancellationToken>()).Returns(_ => { refreshStarted.TrySetResult(); return refresh.Task; });
        connection.ReconnectAsync(Arg.Any<VpnTriggerDimension>()).Returns(_ => { reconnectStarted.TrySetResult(); return Task.CompletedTask; });
        _ = new PowerModeAndWindowActivationHandler(Substitute.For<ILogger>(), Substitute.For<ISettings>(), service,
            Substitute.For<IConnectionCertificateManager>(), auth, power, connection, Substitute.For<IVpnPlanUpdater>(), servers, config);

        power.OnResume += Raise.EventWith(EventArgs.Empty);
        try
        {
            _ = servers.DidNotReceive().UpdateAsync(Arg.Any<CancellationToken>());
            state.SetResult();
            await refreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _ = connection.DidNotReceive().ReconnectAsync(Arg.Any<VpnTriggerDimension>());
            refresh.SetResult();
            await reconnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            state.TrySetResult();
            refresh.TrySetResult();
        }
    }
}
