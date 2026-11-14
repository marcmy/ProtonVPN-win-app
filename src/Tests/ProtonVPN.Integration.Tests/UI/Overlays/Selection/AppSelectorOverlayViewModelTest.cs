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
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Services.Activation;
using ProtonVPN.Client.UI.Overlays.Selection;

namespace ProtonVPN.Integration.Tests.UI.Overlays.Selection;

[TestClass]
public class AppSelectorOverlayViewModelTest
{
    [TestMethod]
    public async Task AddCustomApp_QuotedExecutableAddsOnceAndClearsInput()
    {
        string root = Directory.CreateTempSubdirectory("proton-app-selector-").FullName;
        try
        {
            string path = Path.Combine(root, "OOSU10.exe");
            File.WriteAllText(path, string.Empty);
            AppSelectorOverlayViewModel viewModel = new(
                Substitute.For<IMainWindowActivator>(),
                Substitute.For<IMainWindowOverlayActivator>(),
                Substitute.For<IViewModelHelper>());

            viewModel.CustomAppPath = $"  \"{path}\"  ";
            Assert.IsTrue(viewModel.AddCustomAppCommand.CanExecute(null));
            await viewModel.AddCustomAppCommand.ExecuteAsync(null);

            Assert.AreEqual(1, viewModel.Apps.Count);
            Assert.AreEqual(path, viewModel.Apps[0].Value.AppPath);
            Assert.IsTrue(viewModel.Apps[0].IsSelected);
            Assert.AreEqual(string.Empty, viewModel.CustomAppPath);

            viewModel.CustomAppPath = path;
            await viewModel.AddCustomAppCommand.ExecuteAsync(null);
            Assert.AreEqual(1, viewModel.Apps.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
