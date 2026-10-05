/*
 * Copyright (c) 2023 Proton AG
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

using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using ProtonVPN.Client.Common.UI.Assets.Icons.Base;
using ProtonVPN.Client.Common.UI.Assets.Icons.PathIcons;
using ProtonVPN.Client.Common.UI.ServerHealth;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Enums;
using ProtonVPN.Client.Core.Services.Navigation;
using ProtonVPN.Client.Factories;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Preferences;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Extensions;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;
using ProtonVPN.Client.Models.Connections;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.UI.Main.Sidebar.Connections.Bases.Contracts;
using ProtonVPN.Client.UI.Main.Sidebar.Connections.Bases.ViewModels;

namespace ProtonVPN.Client.UI.Main.Sidebar.Connections.Countries;

public partial class CountriesPageViewModel : ConnectionPageViewModelBase
{
    private readonly IExclusionChecker _exclusionChecker;
    private ServerHealthRefreshScheduler.Discovery? _pingDiscovery;

    [ObservableProperty]
    private ICountriesComponent _selectedCountriesComponent;

    public override string Header => Localizer.Get("Countries");

    public override IconElement Icon => new Earth() { Size = PathIconSize.Pixels16 };

    public override int SortIndex { get; } = 2;

    public List<ICountriesComponent> CountriesComponents { get; }

    public ServerPingFilterSession PingFilter { get; } = ServerPingFilterSession.Current;

    public override bool IsAvailable => ParentViewNavigator.CanNavigateToCountriesView();

    public CountriesPageViewModel(
        IConnectionsViewNavigator parentViewNavigator,
        ISettings settings,
        IServersLoader serversLoader,
        IConnectionManager connectionManager,
        IConnectionGroupFactory connectionGroupFactory,
        IEnumerable<ICountriesComponent> countriesComponents,
        IExclusionChecker exclusionChecker,
        IViewModelHelper viewModelHelper)
        : base(parentViewNavigator,
               settings,
               serversLoader,
               connectionManager,
               connectionGroupFactory,
               viewModelHelper)
    {
        CountriesComponents = new(countriesComponents.OrderBy(p => p.SortIndex));
        _exclusionChecker = exclusionChecker;

        _selectedCountriesComponent = CountriesComponents.First();
        PingFilter.PropertyChanged += OnPingFilterPropertyChanged;
    }

    protected override void OnLoggedIn()
    {
        base.OnLoggedIn();

        GoToCountryFeature(CountriesConnectionType.All);
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        ServerHealthHistorySession.Current.SnapshotChanged += OnPingCacheChanged;
        StartPingDiscovery();
        if (PingFilter.IsActive)
        {
            RefreshCachedFilter();
        }
    }

    protected override void OnDeactivated()
    {
        ServerHealthHistorySession.Current.SnapshotChanged -= OnPingCacheChanged;
        _pingDiscovery?.Dispose();
        _pingDiscovery = null;
        foreach (IHostLocationItem host in Items.OfType<IHostLocationItem>()) { host.StopPingDiscovery(); }
        base.OnDeactivated();
    }

    private void OnPingCacheChanged(object? sender, ServerHealthSnapshotChangedEventArgs args)
    {
        if (!args.Snapshot.IsChecking && !args.Snapshot.IsRechecking)
        {
            ExecuteOnUIThread(() =>
            {
                if (IsActive && PingFilter.IsActive)
                {
                    RefreshCachedFilter();
                }
            });
        }
    }

    private void RefreshCachedFilter()
    {
        foreach (IHostLocationItem host in Items.OfType<IHostLocationItem>())
        {
            host.RefreshPingFilter();
        }
    }

    protected override IEnumerable<ConnectionItemBase> GetItems()
    {
        return SelectedCountriesComponent.GetItems();
    }

    private void GoToCountryFeature(CountriesConnectionType connectionType)
    {
        SelectedCountriesComponent = CountriesComponents.FirstOrDefault(c => c.ConnectionType == connectionType)
                                  ?? CountriesComponents.First();
    }

    partial void OnSelectedCountriesComponentChanged(ICountriesComponent value)
    {
        foreach (IHostLocationItem host in Items.OfType<IHostLocationItem>()) { host.StopPingDiscovery(); }
        FetchItems();
        StartPingDiscovery();
    }

    private void StartPingDiscovery()
    {
        _pingDiscovery?.Dispose();
        _pingDiscovery = null;
        if (!IsActive) { return; }
        ServerFeatures? features = SelectedCountriesComponent.ConnectionType switch
        {
            CountriesConnectionType.SecureCore => ServerFeatures.SecureCore,
            CountriesConnectionType.P2P => ServerFeatures.P2P,
            CountriesConnectionType.Tor => ServerFeatures.Tor,
            _ => null,
        };
        IEnumerable<Server> servers = features is null ? ServersLoader.GetServers() : ServersLoader.GetServersByFeatures(features.Value);
        _pingDiscovery = ServerHealthHistorySession.Refresh.StartDiscovery(servers
            .Where(server => !server.IsUnderMaintenance() && !_exclusionChecker.IsServerExcluded(server))
            // The same ascending score used by Proton's fastest-server selection.
            .OrderBy(server => server.Score).ThenBy(server => server.Load)
            .Select(server => new ServerPingSource(server)));
    }

    private void OnPingFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ServerPingFilterSession.SelectedOption))
        {
            return;
        }

        RefreshCachedFilter();
    }
}
