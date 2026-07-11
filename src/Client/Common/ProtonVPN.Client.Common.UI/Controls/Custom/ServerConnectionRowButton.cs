/*
 * Copyright (c) 2024 Proton AG
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

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProtonVPN.Client.Common.UI.Controls.Bases;
using ProtonVPN.Client.Common.UI.ServerHealth;

namespace ProtonVPN.Client.Common.UI.Controls.Custom;

public class ServerConnectionRowButton : ConnectionRowButtonBase
{
    public static readonly DependencyProperty SupportsSmartRoutingProperty =
        DependencyProperty.Register(nameof(SupportsSmartRouting), typeof(bool), typeof(ServerConnectionRowButton), new PropertyMetadata(default));

    public static readonly DependencyProperty SmartRoutingLabelProperty =
        DependencyProperty.Register(nameof(SmartRoutingLabel), typeof(string), typeof(ServerConnectionRowButton), new PropertyMetadata(default));

    public static readonly DependencyProperty ServerLoadProperty =
        DependencyProperty.Register(nameof(ServerLoad), typeof(double), typeof(ServerConnectionRowButton), new PropertyMetadata(default));

    public static readonly DependencyProperty BaseLocationProperty =
        DependencyProperty.Register(nameof(BaseLocation), typeof(string), typeof(ServerConnectionRowButton), new PropertyMetadata(default));

    private ServerHealthControl? _serverHealthControl;

    public bool SupportsSmartRouting
    {
        get => (bool)GetValue(SupportsSmartRoutingProperty);
        set => SetValue(SupportsSmartRoutingProperty, value);
    }

    public string SmartRoutingLabel
    {
        get => (string)GetValue(SmartRoutingLabelProperty);
        set => SetValue(SmartRoutingLabelProperty, value);
    }

    public double ServerLoad
    {
        get => (double)GetValue(ServerLoadProperty);
        set => SetValue(ServerLoadProperty, value);
    }

    public string BaseLocation
    {
        get => (string)GetValue(BaseLocationProperty);
        set => SetValue(BaseLocationProperty, value);
    }

    public ServerConnectionRowButton()
    {
        DataContextChanged += OnDataContextChanged;
        RegisterPropertyChangedCallback(ServerLoadProperty, OnHealthPropertyChanged);
        RegisterPropertyChangedCallback(IsUnderMaintenanceProperty, OnHealthPropertyChanged);
        RegisterPropertyChangedCallback(IsRestrictedProperty, OnHealthPropertyChanged);
    }

    protected override void OnApplyTemplate()
    {
        if (_serverHealthControl?.Parent is Panel oldParent)
        {
            oldParent.Children.Remove(_serverHealthControl);
        }

        _serverHealthControl = null;

        base.OnApplyTemplate();

        if (GetTemplateChild("ConnectionRowServerLoad") is not FrameworkElement serverLoadElement ||
            serverLoadElement.Parent is not Grid indicatorsGrid)
        {
            return;
        }

        if (indicatorsGrid.ColumnDefinitions.Count < 3)
        {
            indicatorsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        indicatorsGrid.ColumnSpacing = 8;
        Grid.SetColumn(serverLoadElement, 2);

        _serverHealthControl = new ServerHealthControl();
        Grid.SetColumn(_serverHealthControl, 1);
        indicatorsGrid.Children.Add(_serverHealthControl);

        UpdateHealthControl();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        UpdateHealthControl();
    }

    private void OnHealthPropertyChanged(DependencyObject sender, DependencyProperty dependencyProperty)
    {
        UpdateHealthControl();
    }

    private void UpdateHealthControl()
    {
        if (_serverHealthControl is null)
        {
            return;
        }

        IServerHealthSource? source = DataContext as IServerHealthSource;
        bool canProbe = !IsUnderMaintenance &&
                        !string.IsNullOrWhiteSpace(source?.HealthProbeAddress);

        _serverHealthControl.ServerLoad = source?.HealthServerLoad ?? ServerLoad;
        _serverHealthControl.Opacity = IsRestricted ? 0.6 : 1;
        _serverHealthControl.Visibility = canProbe ? Visibility.Visible : Visibility.Collapsed;
        _serverHealthControl.ProbeSource = canProbe ? source : null;
    }
}
