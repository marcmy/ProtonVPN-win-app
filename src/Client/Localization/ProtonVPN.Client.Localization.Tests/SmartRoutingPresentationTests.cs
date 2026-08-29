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

using System.Collections.Generic;
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

    private static readonly IReadOnlyDictionary<string, string> _recoveredSmartRoutingFormats =
        new Dictionary<string, string>
        {
            ["ar-SA"] = "Smart routed through {0}",
            ["be-BY"] = "Разумная маршрутызацыя праз краіну {0}",
            ["ca-ES"] = "Smart ha encaminat a través de {0}",
            ["cs-CZ"] = "Smart routed through {0}",
            ["da-DK"] = "Smart routet gennem {0}",
            ["de-DE"] = "Intelligent weitergeleitet über {0}",
            ["el-GR"] = "Έξυπνη δρομολόγηση μέσω {0}",
            ["en-US"] = "Smart routed through {0}",
            ["es-419"] = "Enrutamiento inteligente a través de {0}",
            ["es-ES"] = "Smart routed through {0}",
            ["fa-IR"] = "Smart routed through {0}",
            ["fi-FI"] = "Älykäs reititys {0}:n kautta",
            ["fil-PH"] = "Smart routed through {0}",
            ["fil-Tglg"] = "Smart routed through {0}",
            ["fr-FR"] = "Routé de manière intelligente à travers {0}",
            ["hu-HU"] = "Smart routed through {0}",
            ["id-ID"] = "Smart routed through {0}",
            ["it-IT"] = "Smart routed through {0}",
            ["ja-JP"] = "Smart routed through {0}",
            ["ka-GE"] = "Smart routed through {0}",
            ["ko-KR"] = "Smart routed through {0}",
            ["nb-NO"] = "Smart routed through {0}",
            ["nl-NL"] = "Slimme routering via {0}",
            ["pl-PL"] = "Smart routed through {0}",
            ["pt-BR"] = "Smart routed through {0}",
            ["pt-PT"] = "Smart routed through {0}",
            ["ro-RO"] = "Rutare inteligentă prin {0}",
            ["ru-RU"] = "Smart routed through {0}",
            ["sk-SK"] = "Inteligentné smerovanie cez {0}",
            ["sl-SI"] = "Pametno usmerjanje prek {0}",
            ["sv-SE"] = "Smart routed through {0}",
            ["th-TH"] = "Smart routed through {0}",
            ["tr-TR"] = "{0}, akıllı yönlendirme üzerinden yönlendirildi",
            ["uk-UA"] = "Smart-спрямування через {0}",
            ["vi-VN"] = "Smart routed through {0}",
            ["zh-CN"] = "通过 {0} 智能路由",
            ["zh-TW"] = "Smart routed through {0}",
        };

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
    public void LocalizedResources_ShouldMatchRecoveredSmartRoutingFormats()
    {
        string[] resourcePaths = Directory.GetFiles(
            SourcePathResolver.LocalizationStringsRoot,
            "Resources.resw",
            SearchOption.AllDirectories);

        resourcePaths.Should().HaveCount(_recoveredSmartRoutingFormats.Count);
        foreach (string resourcePath in resourcePaths)
        {
            string locale = new DirectoryInfo(Path.GetDirectoryName(resourcePath)!).Name;
            _recoveredSmartRoutingFormats.ContainsKey(locale).Should().BeTrue(
                $"{locale} should have a recovered {SMART_ROUTING_FORMAT_KEY} value");

            XDocument document = XDocument.Load(resourcePath);
            XElement? resource = document.Root?
                .Elements("data")
                .SingleOrDefault(element => (string?)element.Attribute("name") == SMART_ROUTING_FORMAT_KEY);

            resource.Should().NotBeNull($"{resourcePath} should define {SMART_ROUTING_FORMAT_KEY}");
            resource!.Element("value")?.Value.Should().Be(_recoveredSmartRoutingFormats[locale]);
        }

        string viewModel = File.ReadAllText(Path.Combine(_connectionCardDirectory, "ConnectionCardComponentViewModel.cs"));
        viewModel.Should().Contain("Localizer.GetFormat(");
        viewModel.Should().Contain("\"Countries_SmartRouting_RoutedThrough\",");
        viewModel.Should().NotContain("Localizer.Get(\"Countries_SmartRouting\")}: ");
    }
}
