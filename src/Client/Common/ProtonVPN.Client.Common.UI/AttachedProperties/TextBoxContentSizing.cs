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


using Microsoft.UI.Xaml;

namespace ProtonVPN.Client.Common.UI.AttachedProperties;

public class TextBoxContentSizing
{
    public static readonly DependencyProperty ContentMinHeightProperty = DependencyProperty.RegisterAttached(
        "ContentMinHeight", typeof(double), typeof(TextBoxContentSizing), new PropertyMetadata(0.0));
    public static readonly DependencyProperty ContentMaxHeightProperty = DependencyProperty.RegisterAttached(
        "ContentMaxHeight", typeof(double), typeof(TextBoxContentSizing), new PropertyMetadata(double.PositiveInfinity));

    public static double GetContentMinHeight(DependencyObject obj) => (double)obj.GetValue(ContentMinHeightProperty);
    public static void SetContentMinHeight(DependencyObject obj, double value) => obj.SetValue(ContentMinHeightProperty, value);
    public static double GetContentMaxHeight(DependencyObject obj) => (double)obj.GetValue(ContentMaxHeightProperty);
    public static void SetContentMaxHeight(DependencyObject obj, double value) => obj.SetValue(ContentMaxHeightProperty, value);
}
