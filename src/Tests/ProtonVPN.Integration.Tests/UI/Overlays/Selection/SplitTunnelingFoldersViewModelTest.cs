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
using System.ComponentModel;
using NSubstitute;
using ProtonVPN.Client.Contracts.Services.Browsing;
using ProtonVPN.Client.Contracts.Profiles;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Services.Activation;
using ProtonVPN.Client.Core.Services.Navigation;
using ProtonVPN.Client.Core.Services.Selection;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Services.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Enums;
using ProtonVPN.Client.Settings.Contracts.Models;
using ProtonVPN.Client.Settings.Contracts.Messages;
using ProtonVPN.Client.Settings.Contracts.Conflicts.Bases;
using ProtonVPN.Client.Settings.Contracts.RequiredReconnections;
using ProtonVPN.Client.UI.Main.Settings.Connection;
using ProtonVPN.Client.UI.Main.Features.SplitTunneling;
using ProtonVPN.Client.UI.Overlays.Selection.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;
using ProtonVPN.Common.Core.Helpers;
using ProtonVPN.Common.Legacy.Abstract;
using ProtonVPN.Client.Logic.Services;
using ProtonVPN.Client.Contracts.ProcessCommunication;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.ProcessCommunication.Contracts;
using ProtonVPN.ProcessCommunication.Contracts.Controllers;

namespace ProtonVPN.Integration.Tests.UI.Overlays.Selection;

[TestClass]
public class SplitTunnelingFoldersViewModelTest
{
    [TestMethod]
    public void HoverFolders_CountActiveRulesInBothModesAndRefreshOnSettingsChanges()
    {
        CreateModel(out IViewModelHelper helper);
        helper.Localizer.GetFormat(Arg.Any<string>(), Arg.Any<object>())
            .Returns(call => $"{call.ArgAt<string>(0)}:{call.ArgAt<object>(1)}");
        ISettings settings = Substitute.For<ISettings>();
        settings.SplitTunnelingStandardFoldersList.Returns(new List<SplitTunnelingFolder> { new("exclude", true), new("disabled", false) });
        settings.SplitTunnelingInverseFoldersList.Returns(new List<SplitTunnelingFolder> { new("include1", true), new("include2", true) });
        SplitTunnelingWidgetViewModel widget = new(helper, Substitute.For<IApplicationThemeSelector>(), settings,
            Substitute.For<IMainViewNavigator>(), Substitute.For<ISettingsViewNavigator>(), Substitute.For<IConnectionManager>(),
            Substitute.For<IUpsellCarouselWindowActivator>(), Substitute.For<IMainWindowOverlayActivator>(),
            Substitute.For<IRequiredReconnectionSettings>(), Substitute.For<ISettingsConflictResolver>(),
            Substitute.For<IProfileEditor>(), Substitute.For<IAppSelector>(), Substitute.For<IIpSelector>());
        settings.SplitTunnelingMode.Returns(SplitTunnelingMode.Standard);
        Assert.AreEqual(1, widget.SelectedFolderCount);
        Assert.AreEqual("SplitTunneling_Folders_Excluded_FormattedHeader:1", widget.FoldersHeader);
        settings.SplitTunnelingMode.Returns(SplitTunnelingMode.Inverse);
        Assert.AreEqual(2, widget.SelectedFolderCount);
        Assert.AreEqual("SplitTunneling_Folders_Included_FormattedHeader:2", widget.FoldersHeader);
        List<string?> notifications = [];
        widget.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        widget.Receive(new SettingChangedMessage(nameof(ISettings.SplitTunnelingInverseFoldersList), typeof(List<SplitTunnelingFolder>), null, null));
        Assert.IsTrue(notifications.Contains(nameof(widget.FoldersHeader)));
        Assert.IsTrue(notifications.Contains(nameof(widget.SelectedFolderCount)));
    }

    private static HeadlessFolderPage CreateModel(out IViewModelHelper helper)
    {
        helper = Substitute.For<IViewModelHelper>();
        helper.Localizer.Get(Arg.Any<string>()).Returns(call => call.Arg<string>());
        helper.Localizer.GetFormat(Arg.Any<string>(), Arg.Any<object[]>()).Returns(call => call.Arg<string>());
        helper.Localizer.GetFormat(Arg.Any<string>(), Arg.Any<object>()).Returns(call => call.Arg<string>());
        helper.Localizer.GetFormat(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<object>()).Returns(call => call.Arg<string>());
        helper.UIThreadDispatcher.TryEnqueue(Arg.Any<Action>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(call => { call.Arg<Action>()(); return true; });
        ISettingsConflictResolver conflicts = Substitute.For<ISettingsConflictResolver>();
        conflicts.GetConflict(Arg.Any<string>(), Arg.Any<object>()).Returns((ISettingsConflict?)null);
        return new(helper, conflicts);
    }

    [TestMethod]
    public async Task Add_WaitsForServiceDiscoveryAndDoesNotImportAppsOrApplyUnsavedRules()
    {
        string root = Directory.CreateTempSubdirectory("proton-ui-prepare-").FullName;
        try
        {
            CreateModel(out IViewModelHelper helper);
            IVpnServiceCaller service = Substitute.For<IVpnServiceCaller>();
            TaskCompletionSource<Result<FolderScanStatusIpcEntity>> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            service.PrepareFolderRuleAsync(root, Arg.Any<CancellationToken>()).Returns(reply.Task);
            HeadlessFolderPage model = new(helper, Substitute.For<ISettingsConflictResolver>(), service);
            model.CustomFolderPath = $"\"{root}\"";
            Task addition = model.AddFolderAsync();
            Assert.IsTrue(model.IsFolderScanRunning);
            Assert.AreEqual(0, model.Folders.Count);
            reply.SetResult(Result.Ok(new FolderScanStatusIpcEntity { Executables = 7 }));
            await addition;
            Assert.AreEqual(1, model.Folders.Count);
            Assert.AreEqual(0, model.Apps.Count);
            await service.Received(1).PrepareFolderRuleAsync(root, Arg.Any<CancellationToken>());
            await service.DidNotReceive().ApplySettingsAsync(Arg.Any<MainSettingsIpcEntity>());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CancelledServiceDiscovery_IsNotRetriedOrTreatedAsCommunicationFailure()
    {
        IVpnController controller = Substitute.For<IVpnController>();
        IGrpcClient grpc = Substitute.For<IGrpcClient>();
        grpc.GetServiceControllerOrThrowAsync<IVpnController>(Arg.Any<TimeSpan>()).Returns(Task.FromResult(controller));
        IServiceCommunicationErrorHandler errors = Substitute.For<IServiceCommunicationErrorHandler>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.PrepareFolderRule(Arg.Any<FolderScanRequestIpcEntity>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            return new FolderScanStatusIpcEntity();
        });
        VpnServiceCaller caller = new(Substitute.For<ILogger>(), grpc, new(() => errors));
        using CancellationTokenSource cancel = new();
        Task pending = caller.PrepareFolderRuleAsync(@"C:\tools", cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => pending);
        await errors.DidNotReceive().HandleAsync();
        await controller.Received(1).PrepareFolderRule(Arg.Any<FolderScanRequestIpcEntity>(), Arg.Any<CancellationToken>());
    }

    [TestMethod]
    public async Task MoreThanTwentyFoldersCanBeAddedInBothModes()
    {
        string root = Directory.CreateTempSubdirectory("proton-unlimited-folders-").FullName;
        try
        {
            var model = CreateModel(out _);
            foreach (SplitTunnelingMode mode in new[] { SplitTunnelingMode.Standard, SplitTunnelingMode.Inverse })
            {
                model.CurrentSplitTunnelingMode = mode;
                for (int i = 0; i < 25; i++)
                {
                    model.CustomFolderPath = Directory.CreateDirectory(Path.Combine(root, $"{i}")).FullName;
                    await model.AddFolderAsync();
                }
                Assert.AreEqual(25, model.Folders.Count);
            }
            Assert.AreEqual(25, model.ExcludedFolders.Count);
            Assert.AreEqual(25, model.IncludedFolders.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CancelDuringValidation_DoesNotAddPartialFolderRule()
    {
        string root = Directory.CreateTempSubdirectory("proton-cancel-folder-ui-").FullName;
        try
        {
            for (int i = 0; i < 1024; i++) { File.WriteAllText(Path.Combine(root, $"{i}.txt"), ""); }
            var model = CreateModel(out _);
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.FolderScanProgress) && model.FolderScanProgress == "SplitTunneling_Folders_Scanning")
                {
                    model.CancelFolderScan();
                }
            };
            model.CustomFolderPath = root;
            await model.AddFolderAsync();
            Assert.AreEqual(0, model.Folders.Count);
            Assert.IsFalse(model.IsFolderScanRunning);
            Assert.AreEqual(string.Empty, model.FolderScanProgress);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void ServiceProgress_DistinguishesInactiveScanningFailedAndComplete()
    {
        var model = CreateModel(out _);
        Assert.AreEqual("SplitTunneling_Folders_Inactive", model.FormatFolderServiceStatus(new()));
        Assert.AreEqual("SplitTunneling_Folders_ServiceScanning", model.FormatFolderServiceStatus(new() { IsActive = true, IsScanning = true }));
        Assert.AreEqual("SplitTunneling_Folders_ServiceFailed", model.FormatFolderServiceStatus(new() { IsActive = true, Error = "inaccessible" }));
        Assert.AreEqual("SplitTunneling_Folders_ServiceComplete", model.FormatFolderServiceStatus(new() { IsActive = true, Executables = 12 }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FolderControls_QuotedPathsDedupToggleRemoveAndModeIndependence(bool wildcard)
    {
        string root = Directory.CreateTempSubdirectory("proton-folder-ui-").FullName;
        try
        {
            string rule = wildcard ? Path.Combine(root, "version*", "Tools") : root;
            IViewModelHelper helper = Substitute.For<IViewModelHelper>();
            helper.Localizer.Get(Arg.Any<string>()).Returns(call => call.Arg<string>());
            ISettingsConflictResolver conflicts = Substitute.For<ISettingsConflictResolver>();
            conflicts.GetConflict(Arg.Any<string>(), Arg.Any<object>()).Returns((ISettingsConflict?)null);
            SplitTunnelingPageViewModel model = new HeadlessFolderPage(helper, conflicts);
            model.CurrentSplitTunnelingMode = SplitTunnelingMode.Standard;
            model.CustomFolderPath = $" \"{rule}\" ";
            await model.AddFolderAsync();
            Assert.AreEqual(1, model.ExcludedFolders.Count);
            Assert.AreEqual(rule, model.ExcludedFolders[0].FolderPath);
            Assert.AreEqual(0, model.Apps.Count); // No executable import into the app list.
            model.ExcludedFolders[0].IsSelected = false;
            model.CustomFolderPath = rule;
            await model.AddFolderAsync();
            Assert.AreEqual(1, model.ExcludedFolders.Count);
            Assert.IsTrue(model.ExcludedFolders[0].IsSelected);

            model.CurrentSplitTunnelingMode = SplitTunnelingMode.Inverse;
            model.CustomFolderPath = rule;
            await model.AddFolderAsync();
            Assert.AreEqual(1, model.IncludedFolders.Count);
            Assert.AreEqual(rule, model.IncludedFolders[0].FolderPath);
            model.RemoveFolder(model.IncludedFolders[0]);
            Assert.AreEqual(0, model.IncludedFolders.Count);
            Assert.AreEqual(1, model.ExcludedFolders.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public sealed class HeadlessFolderPage(IViewModelHelper helper, ISettingsConflictResolver conflicts, IVpnServiceCaller? service = null) : SplitTunnelingPageViewModel(
        Substitute.For<IUrlsBrowser>(), Substitute.For<IRequiredReconnectionSettings>(),
        Substitute.For<IMainViewNavigator>(), Substitute.For<ISettingsViewNavigator>(),
        Substitute.For<IMainWindowOverlayActivator>(), Substitute.For<ISettings>(),
        conflicts, Substitute.For<IConnectionManager>(), Substitute.For<IIpSelector>(),
        Substitute.For<IAppSelector>(), Substitute.For<IMainWindowActivator>(), service ?? CreateFolderService(), helper)
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs args)
        {
            // SettingsPageViewModelBase reflects notified getters for conflict checks.
            // This component test has no WinUI resource dictionary for the unrelated illustration.
            if (args.PropertyName != nameof(SplitTunnelingFeatureIconSource)) { base.OnPropertyChanged(args); }
        }
    }

    private static IVpnServiceCaller CreateFolderService()
    {
        IVpnServiceCaller service = Substitute.For<IVpnServiceCaller>();
        service.PrepareFolderRuleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            FolderScanResult result = await SplitTunnelFolderScanner.ScanAsync(call.Arg<string>(), call.Arg<CancellationToken>());
            return Result.Ok(new FolderScanStatusIpcEntity { Executables = result.AppPaths.Length, Error = result.Error ?? string.Empty });
        });
        return service;
    }
}
