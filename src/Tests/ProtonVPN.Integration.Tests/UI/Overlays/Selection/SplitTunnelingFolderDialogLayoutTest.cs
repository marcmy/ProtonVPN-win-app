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

using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ProtonVPN.Integration.Tests.UI.Overlays.Selection;

// Source-layout contracts run without starting a WinUI window. Runtime visual acceptance is separate.
[TestClass]
public class SplitTunnelingFolderDialogLayoutTest
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XElement LoadDialog()
    {
        XDocument page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "TestData", "SplitTunnelingPageView.xaml"));
        return page.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "FolderRulesDialog");
    }

    [TestMethod]
    public void WidthOverridesAreScopedToFolderDialogAndShrinkWithHost()
    {
        XElement dialog = LoadDialog();
        XElement resources = dialog.Element(Presentation + "ContentDialog.Resources")!;
        Assert.AreEqual("NaN", resources.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == "ContentDialogWidth").Value);
        Assert.AreEqual("760", resources.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == "MaximumContentDialogWidth").Value);
        Assert.AreEqual("24,16", resources.Elements().Single(e => (string?)e.Attribute(Xaml + "Key") == "ContentDialogMargin").Value);
        Assert.IsFalse(dialog.Descendants().Any(e => e.Attribute("MinWidth") != null));
    }

    [TestMethod]
    public void OnlyFolderListScrollsAndFooterStaysOutsideIt()
    {
        XElement dialog = LoadDialog();
        Assert.AreEqual("Disabled", (string?)dialog.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
        XElement content = dialog.Element(Presentation + "Grid")!;
        Assert.AreEqual("560", (string?)content.Attribute("Height"));
        CollectionAssert.AreEqual(new[] { "Auto", "*", "Auto" },
            content.Element(Presentation + "Grid.RowDefinitions")!.Elements().Select(e => (string)e.Attribute("Height")!).ToArray());
        XElement list = dialog.Descendants(Presentation + "ScrollViewer").Single();
        Assert.AreEqual("FolderListScrollViewer", (string?)list.Attribute(Xaml + "Name"));
        Assert.AreEqual("1", (string?)list.Attribute("Grid.Row"));
        Assert.AreEqual(content, list.Parent);
        Assert.AreEqual("Auto", (string?)list.Attribute("VerticalScrollBarVisibility"));
        XElement footer = content.Elements(Presentation + "StackPanel").Single(e => (string?)e.Attribute("Grid.Row") == "2");
        Assert.AreEqual(3, footer.Descendants(Presentation + "Button").Count()); // Add, Browse, cancel validation.
        Assert.IsTrue(footer.Descendants(Presentation + "TextBox").Any());
        Assert.IsTrue(footer.Descendants(Presentation + "TextBlock").Any(e => (string?)e.Attribute("Text") == "{x:Bind ViewModel.FolderServiceProgress}"));
    }

    [TestMethod]
    public void CloseCommandIsWiredAndLongPathsDoNotCrowdRemoveButton()
    {
        XElement dialog = LoadDialog();
        Assert.AreEqual("{x:Bind CloseFolderDialogCommand, Mode=OneTime}", (string?)dialog.Attribute("CloseButtonCommand"));
        XElement toggle = dialog.Descendants(Presentation + "CheckBox").Single();
        Assert.AreEqual("{x:Bind FolderPath}", (string?)toggle.Attribute("ToolTipService.ToolTip"));
        Assert.AreEqual("CharacterEllipsis", (string?)toggle.Element(Presentation + "TextBlock")!.Attribute("TextTrimming"));
        XElement remove = toggle.Parent!.Elements(Presentation + "Button").Single();
        Assert.AreEqual("1", (string?)remove.Attribute("Grid.Column"));
    }
}
