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
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Services.Activation;
using ProtonVPN.Client.Core.Services.Navigation;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Enums;
using ProtonVPN.Client.Settings.Contracts.Conflicts.Bases;
using ProtonVPN.Client.Settings.Contracts.RequiredReconnections;
using ProtonVPN.Client.UI.Main.Settings.Connection;
using ProtonVPN.Client.UI.Overlays.Selection.Contracts;

namespace ProtonVPN.Integration.Tests.UI.Overlays.Selection;

[TestClass]
public class SplitTunnelingFoldersViewModelTest
{
    [TestMethod]
    public async Task FolderControls_QuotedPathsDedupToggleRemoveAndModeIndependence()
    {
        string root = Directory.CreateTempSubdirectory("proton-folder-ui-").FullName;
        try
        {
            IViewModelHelper helper = Substitute.For<IViewModelHelper>();
            helper.Localizer.Get(Arg.Any<string>()).Returns(call => call.Arg<string>());
            ISettingsConflictResolver conflicts = Substitute.For<ISettingsConflictResolver>();
            conflicts.GetConflict(Arg.Any<string>(), Arg.Any<object>()).Returns((ISettingsConflict?)null);
            SplitTunnelingPageViewModel model = new HeadlessFolderPage(helper, conflicts);
            model.CurrentSplitTunnelingMode = SplitTunnelingMode.Standard;
            model.CustomFolderPath = $" \"{root}\" ";
            await model.AddFolderAsync();
            Assert.AreEqual(1, model.ExcludedFolders.Count);
            Assert.AreEqual(root, model.ExcludedFolders[0].FolderPath);
            Assert.AreEqual(0, model.Apps.Count); // No executable import into the app list.
            model.ExcludedFolders[0].IsSelected = false;
            model.CustomFolderPath = root;
            await model.AddFolderAsync();
            Assert.AreEqual(1, model.ExcludedFolders.Count);
            Assert.IsTrue(model.ExcludedFolders[0].IsSelected);

            model.CurrentSplitTunnelingMode = SplitTunnelingMode.Inverse;
            model.CustomFolderPath = root;
            await model.AddFolderAsync();
            Assert.AreEqual(1, model.IncludedFolders.Count);
            model.RemoveFolder(model.IncludedFolders[0]);
            Assert.AreEqual(0, model.IncludedFolders.Count);
            Assert.AreEqual(1, model.ExcludedFolders.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public sealed class HeadlessFolderPage(IViewModelHelper helper, ISettingsConflictResolver conflicts) : SplitTunnelingPageViewModel(
        Substitute.For<IUrlsBrowser>(), Substitute.For<IRequiredReconnectionSettings>(),
        Substitute.For<IMainViewNavigator>(), Substitute.For<ISettingsViewNavigator>(),
        Substitute.For<IMainWindowOverlayActivator>(), Substitute.For<ISettings>(),
        conflicts, Substitute.For<IConnectionManager>(), Substitute.For<IIpSelector>(),
        Substitute.For<IAppSelector>(), Substitute.For<IMainWindowActivator>(), helper)
    {
        protected override void OnPropertyChanged(PropertyChangedEventArgs args)
        {
            // SettingsPageViewModelBase reflects notified getters for conflict checks.
            // This component test has no WinUI resource dictionary for the unrelated illustration.
            if (args.PropertyName != nameof(SplitTunnelingFeatureIconSource)) { base.OnPropertyChanged(args); }
        }
    }
}
