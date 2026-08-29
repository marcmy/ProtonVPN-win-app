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

using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Localization.Tests.Helpers;

namespace ProtonVPN.Client.Localization.Tests;

[TestClass]
public class SmartRoutingPresentationTests
{
    private const string SMART_ROUTING_FORMAT_KEY = "Countries_SmartRouting_RoutedThrough";

    private static readonly string _connectionCardDirectory = Path.Combine(
        SourcePathResolver.SourceRoot,
        "Client",
        "ProtonVPN.Client",
        "UI",
        "Main",
        "Home",
        "Card");

    [TestMethod]
    public void ConnectionCardViewModel_ShouldExposeConnectedVirtualServerHostCountry()
    {
        string content = File.ReadAllText(Path.Combine(_connectionCardDirectory, "ConnectionCardComponentViewModel.cs"));

        content.Should().Contain("[NotifyPropertyChangedFor(nameof(HostCountry))]");
        content.Should().Contain("[NotifyPropertyChangedFor(nameof(IsVirtual))]");
        content.Should().Contain("[NotifyPropertyChangedFor(nameof(SmartRoutingLabel))]");
        content.Should().Contain("CurrentConnectionDetails?.Server.HostCountry");
        content.Should().Contain("CurrentConnectionDetails?.Server.IsVirtual == true");
        content.Should().Contain("ShowSmartRoutingInfoOverlayAsync()");
        content.Should().Contain("_mainWindowOverlayActivator.ShowSmartRoutingInfoOverlayAsync()");
    }

    [TestMethod]
    public void ConnectionCardView_ShouldBindSmartRoutingPresentationToVirtualServerState()
    {
        string content = File.ReadAllText(Path.Combine(_connectionCardDirectory, "ConnectionCardComponentView.xaml"));

        content.Should().Contain("AutomationProperties.AutomationId=\"ConnectionCardSmartRoutingTag\"");
        content.Should().Contain("Command=\"{x:Bind ViewModel.ShowSmartRoutingInfoOverlayCommand, Mode=OneTime}\"");
        content.Should().Contain("Content=\"{x:Bind ViewModel.Localizer.Get('Countries_SmartRouting')}\"");
        content.Should().Contain("ToolTipService.ToolTip=\"{x:Bind ViewModel.SmartRoutingLabel}\"");
        content.Should().Contain("Visibility=\"{x:Bind ViewModel.IsVirtual, Converter={StaticResource BooleanToVisibilityConverter}}\"");
        content.Should().Contain("<pathicons:Globe Size=\"Pixels16\" />");
    }

    [TestMethod]
    public void LocalizedResources_ShouldContainRecoveredSmartRoutingFormat()
    {
        string[] resourcePaths = Directory.GetFiles(
            SourcePathResolver.LocalizationStringsRoot,
            "Resources.resw",
            SearchOption.AllDirectories);

        resourcePaths.Should().HaveCount(37);
        foreach (string resourcePath in resourcePaths)
        {
            XDocument document = XDocument.Load(resourcePath);
            XElement? resource = document.Root?
                .Elements("data")
                .SingleOrDefault(element => (string?)element.Attribute("name") == SMART_ROUTING_FORMAT_KEY);

            resource.Should().NotBeNull($"{resourcePath} should define {SMART_ROUTING_FORMAT_KEY}");
            resource!.Element("value")?.Value.Should().Contain("{0}",
                $"{SMART_ROUTING_FORMAT_KEY} in {resourcePath} should format the physical host country");
        }

        string viewModel = File.ReadAllText(Path.Combine(_connectionCardDirectory, "ConnectionCardComponentViewModel.cs"));
        viewModel.Should().Contain("Localizer.GetFormat(");
        viewModel.Should().Contain("\"Countries_SmartRouting_RoutedThrough\",");
        viewModel.Should().NotContain("Localizer.Get(\"Countries_SmartRouting\")}: ");
    }
}
