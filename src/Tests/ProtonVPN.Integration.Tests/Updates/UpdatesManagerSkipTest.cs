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
using ProtonVPN.Client.Contracts.Services.Lifecycle;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.Client.Logic.Updates;
using ProtonVPN.Client.Logic.Updates.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Messages;
using ProtonVPN.Common.Legacy.OS.Processes;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.EntityMapping.Contracts;
using ProtonVPN.IssueReporting.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Update;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;
using ProtonVPN.Update.Contracts;

namespace ProtonVPN.Integration.Tests.Updates;

[TestClass]
public class UpdatesManagerSkipTest
{
    private ISettings _settings = null!;
    private IUpdateServiceCaller _updateServiceCaller = null!;
    private IEntityMapper _entityMapper = null!;
    private IOsProcesses _osProcesses = null!;
    private IAppExitInvoker _appExitInvoker = null!;
    private UpdatesManager _updatesManager = null!;

    [TestInitialize]
    public void Initialize()
    {
        _settings = Substitute.For<ISettings>();
        _settings.AreAutomaticUpdatesEnabled.Returns(false);
        _settings.IsBetaAccessEnabled.Returns(true);

        _updateServiceCaller = Substitute.For<IUpdateServiceCaller>();
        _updateServiceCaller.StartAutoUpdateAsync().Returns(Task.CompletedTask);

        _entityMapper = Substitute.For<IEntityMapper>();
        _osProcesses = Substitute.For<IOsProcesses>();
        _appExitInvoker = Substitute.For<IAppExitInvoker>();
        _appExitInvoker.ForceExitAsync().Returns(Task.CompletedTask);
        _appExitInvoker.RestartAsync(Arg.Any<bool>()).Returns(Task.CompletedTask);

        IConfiguration configuration = Substitute.For<IConfiguration>();
        configuration.UpdateCheckInterval.Returns(TimeSpan.FromHours(1));
        configuration.ClientVersion.Returns("4.4.1");

        _updatesManager = new UpdatesManager(
            Substitute.For<ILogger>(),
            Substitute.For<IIssueReporter>(),
            Substitute.For<IConnectionManager>(),
            configuration,
            _entityMapper,
            _settings,
            _updateServiceCaller,
            Substitute.For<IEventMessageSender>(),
            Substitute.For<IVpnServiceSettingsUpdater>(),
            _osProcesses,
            _appExitInvoker);
    }

    [TestMethod]
    public void SkipCurrentUpdate_WhenReadyUpdateExists_PersistsExactVersionBuildIdentifierAndHidesIt()
    {
        // Arrange
        UpdateStateIpcEntity message = new();
        AppUpdateStateContract state = CreateReadyState("4.4.2", "ProtonVPN_win_v4.4.2_beta1.exe", "/qb BUILD=beta1", isEarlyAccess: true);
        _entityMapper.Map<UpdateStateIpcEntity, AppUpdateStateContract>(message).Returns(state);
        _updatesManager.Receive(message);

        // Act
        _updatesManager.SkipCurrentUpdate();

        // Assert
        Assert.IsFalse(_updatesManager.IsUpdateAvailable);
        Assert.IsFalse(_updatesManager.CanSkipCurrentUpdate);
        Assert.AreEqual("4.4.2|ProtonVPN_win_v4.4.2_beta1.exe|/qb BUILD=beta1", _settings.SkippedUpdateVersion);
    }

    [TestMethod]
    public void Receive_WhenSameVersionHasDifferentBuildIdentifier_DoesNotTreatItAsSkipped()
    {
        // Arrange
        _settings.SkippedUpdateVersion.Returns("4.4.2|ProtonVPN_win_v4.4.2_beta1.exe|/qb BUILD=beta1");

        UpdateStateIpcEntity message = new();
        AppUpdateStateContract state = CreateReadyState("4.4.2", "ProtonVPN_win_v4.4.2_beta2.exe", "/qb BUILD=beta2", isEarlyAccess: true);
        _entityMapper.Map<UpdateStateIpcEntity, AppUpdateStateContract>(message).Returns(state);

        // Act
        _updatesManager.Receive(message);

        // Assert
        Assert.IsTrue(_updatesManager.IsUpdateAvailable);
        Assert.IsTrue(_updatesManager.CanSkipCurrentUpdate);
    }

    [TestMethod]
    public async Task Receive_WhenBetaUpdateIsReadyAndAutomaticUpdatesAreEnabled_DoesNotStartAutoUpdate()
    {
        // Arrange
        _settings.AreAutomaticUpdatesEnabled.Returns(true);
        _settings.IsBetaAccessEnabled.Returns(true);

        UpdateStateIpcEntity message = new();
        AppUpdateStateContract state = CreateReadyState("4.4.2", "ProtonVPN_win_v4.4.2_beta1.exe", "/qb BUILD=beta1", isEarlyAccess: true);
        _entityMapper.Map<UpdateStateIpcEntity, AppUpdateStateContract>(message).Returns(state);

        // Act
        _updatesManager.Receive(message);

        // Assert
        Assert.IsTrue(_updatesManager.IsUpdateAvailable);
        await _updateServiceCaller.DidNotReceive().StartAutoUpdateAsync();
    }

    [TestMethod]
    public async Task Receive_WhenStableUpdateIsReadyAndAutomaticUpdatesAreEnabled_StartsAutoUpdate()
    {
        // Arrange
        _settings.AreAutomaticUpdatesEnabled.Returns(true);
        _settings.IsBetaAccessEnabled.Returns(false);

        UpdateStateIpcEntity message = new();
        AppUpdateStateContract state = CreateReadyState("4.4.2", "ProtonVPN_win_v4.4.2.exe", "/qb", isEarlyAccess: false);
        _entityMapper.Map<UpdateStateIpcEntity, AppUpdateStateContract>(message).Returns(state);

        // Act
        _updatesManager.Receive(message);

        // Assert
        await _updateServiceCaller.Received(1).StartAutoUpdateAsync();
    }

    [TestMethod]
    public void Receive_WhenBetaAccessIsDisabledWithReadyBetaUpdate_SkipsCurrentBetaOffer()
    {
        // Arrange
        _settings.IsBetaAccessEnabled.Returns(true);

        UpdateStateIpcEntity message = new();
        AppUpdateStateContract state = CreateReadyState("4.4.2", "ProtonVPN_win_v4.4.2_beta1.exe", "/qb BUILD=beta1", isEarlyAccess: true);
        _entityMapper.Map<UpdateStateIpcEntity, AppUpdateStateContract>(message).Returns(state);
        _updatesManager.Receive(message);

        _settings.IsBetaAccessEnabled.Returns(false);

        // Act
        _updatesManager.Receive(new SettingChangedMessage(nameof(ISettings.IsBetaAccessEnabled), typeof(bool), true, false));

        // Assert
        Assert.IsFalse(_updatesManager.IsUpdateAvailable);
        Assert.AreEqual("4.4.2|ProtonVPN_win_v4.4.2_beta1.exe|/qb BUILD=beta1", _settings.SkippedUpdateVersion);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UpdateAsync_WhenCurrentUpdateIsSuppressed_DoesNotLaunchInstallerOrRestart(bool isSkippedByStoredIdentifier)
    {
        // Arrange
        const string fileName = "ProtonVPN_win_v4.4.2_beta1.exe";
        const string arguments = "/qb BUILD=beta1";
        UpdateStateIpcEntity message = new();
        AppUpdateStateContract state = CreateReadyState("4.4.2", fileName, arguments, isEarlyAccess: true);
        _entityMapper.Map<UpdateStateIpcEntity, AppUpdateStateContract>(message).Returns(state);

        if (isSkippedByStoredIdentifier)
        {
            _settings.IsBetaAccessEnabled.Returns(true);
            _settings.SkippedUpdateVersion.Returns($"4.4.2|{fileName}|{arguments}");
        }
        else
        {
            _settings.IsBetaAccessEnabled.Returns(false);
        }

        _updatesManager.Receive(message);

        // Act
        await _updatesManager.UpdateAsync(isToOpenOnDesktop: false);

        // Assert
        Assert.IsFalse(_updatesManager.IsUpdateAvailable);
        _osProcesses.DidNotReceive().ElevatedProcess(Arg.Any<string>(), Arg.Any<string>());
        await _appExitInvoker.DidNotReceive().ForceExitAsync();
        await _appExitInvoker.DidNotReceive().RestartAsync(Arg.Any<bool>());
    }

    private static AppUpdateStateContract CreateReadyState(string version, string fileName, string arguments, bool isEarlyAccess)
    {
        Version parsedVersion = Version.Parse(version);

        return new AppUpdateStateContract
        {
            IsAvailable = true,
            IsReady = true,
            Status = AppUpdateStatus.Ready,
            FilePath = Path.Combine("C:\\Updates", fileName),
            FileArguments = arguments,
            Version = parsedVersion,
            ReleaseHistory =
            [
                new ReleaseContract
                {
                    Version = parsedVersion,
                    IsEarlyAccess = isEarlyAccess,
                    IsNew = true,
                }
            ],
        };
    }
}
