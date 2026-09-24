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
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Client.Localization.Tests.Helpers;

namespace ProtonVPN.Client.Localization.Tests;

[TestClass]
public class SmartRoutingPresentationTests
{
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
        Match virtualProperty = Regex.Match(
            content,
            @"public bool IsVirtual\s*=>\s*(?<body>.*?);\s*public string SmartRoutingLabel",
            RegexOptions.Singleline);
        virtualProperty.Success.Should().BeTrue();
        virtualProperty.Groups["body"].Value.Should().Contain("ConnectionStatus.Connected");
        virtualProperty.Groups["body"].Value.Should().Contain("Server.IsVirtual");
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
    public void ConnectionCardView_ShouldPlaceSmartRoutingTagInASeparateFeatureColumn()
    {
        string content = File.ReadAllText(Path.Combine(_connectionCardDirectory, "ConnectionCardComponentView.xaml"));
        Match row = Regex.Match(content, "<Grid Margin=\"0,8,0,0\"(?<row>.*?)</Grid>", RegexOptions.Singleline);

        row.Success.Should().BeTrue();
        Regex.Matches(row.Groups["row"].Value, "<ColumnDefinition\\b").Count.Should().Be(4);

        Match smartRoutingTag = Regex.Match(
            row.Groups["row"].Value,
            "<custom:GhostButton Grid\\.Column=\"(?<column>\\d+)\"[^>]*AutomationProperties\\.AutomationId=\"ConnectionCardSmartRoutingTag\"",
            RegexOptions.Singleline);

        smartRoutingTag.Success.Should().BeTrue();
        smartRoutingTag.Groups["column"].Value.Should().Be("3");
    }

    [TestMethod]
    public void HostLocationItem_ShouldDescribeAllVisibleVirtualServerHostCountries()
    {
        string sourcePath = Path.Combine(
            SourcePathResolver.SourceRoot,
            "Client",
            "ProtonVPN.Client",
            "Models",
            "Connections",
            "HostLocationItemBase.cs");
        string content = File.ReadAllText(sourcePath);

        content.Should().Contain("List<ServerLocationItemBase> virtualServers = SubItems.OfType<ServerLocationItemBase>().Where(s => s.IsVirtual).ToList()");
        content.Should().Contain("string.Join(\", \", virtualServers.Select(s => s.Server.HostCountry)");
        content.Should().Contain(".Distinct()");
        content.Should().Contain("Localizer.GetCountryName(hostCountry)");
    }
}
