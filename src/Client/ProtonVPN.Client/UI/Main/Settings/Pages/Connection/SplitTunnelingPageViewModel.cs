/*
 * Copyright (c) 2025 Proton AG
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

using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using ProtonVPN.Client.Common.Attributes;
using ProtonVPN.Client.Common.Collections;
using ProtonVPN.Client.Contracts.Services.Browsing;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Helpers;
using ProtonVPN.Client.Core.Extensions;
using ProtonVPN.Common.Core.Helpers;
using ProtonVPN.Client.Core.Models;
using ProtonVPN.Client.Core.Services.Activation;
using ProtonVPN.Client.Core.Services.Navigation;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Enums;
using ProtonVPN.Client.Settings.Contracts.Models;
using ProtonVPN.Client.Settings.Contracts.RequiredReconnections;
using ProtonVPN.Client.UI.Main.Settings.Bases;
using ProtonVPN.Client.UI.Overlays.Selection.Contracts;

namespace ProtonVPN.Client.UI.Main.Settings.Connection;

public partial class SplitTunnelingPageViewModel : SettingsPageViewModelBase
{
    private readonly IUrlsBrowser _urlsBrowser;
    private readonly IIpSelector _ipSelector;
    private readonly IAppSelector _appSelector;
    private readonly IMainWindowActivator _mainWindowActivator;

    private bool _wasIpv6WarningDisplayed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIpv6AddressesWhileIpv6Disabled))]
    private bool _isIpv6Enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SplitTunnelingFeatureIconSource))]
    [NotifyPropertyChangedFor(nameof(HasIpv6AddressesWhileIpv6Disabled))]
    private bool _isSplitTunnelingEnabled;

    [ObservableProperty]
    [property: SettingName(nameof(ISettings.SplitTunnelingMode))]
    [NotifyPropertyChangedFor(nameof(IsStandardSplitTunneling))]
    [NotifyPropertyChangedFor(nameof(IsInverseSplitTunneling))]
    [NotifyPropertyChangedFor(nameof(SplitTunnelingFeatureIconSource))]
    [NotifyPropertyChangedFor(nameof(IpAddresses))]
    [NotifyPropertyChangedFor(nameof(IpAddressesHeader))]
    [NotifyPropertyChangedFor(nameof(SelectedIpAddresses))]
    [NotifyPropertyChangedFor(nameof(HasSelectedIpAddresses))]
    [NotifyPropertyChangedFor(nameof(Apps))]
    [NotifyPropertyChangedFor(nameof(AppsHeader))]
    [NotifyPropertyChangedFor(nameof(SelectedApps))]
    [NotifyPropertyChangedFor(nameof(HasSelectedApps))]
    [NotifyPropertyChangedFor(nameof(Folders))]
    [NotifyPropertyChangedFor(nameof(FoldersHeader))]
    [NotifyPropertyChangedFor(nameof(HasIpv6AddressesWhileIpv6Disabled))]
    private SplitTunnelingMode _currentSplitTunnelingMode;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _customFolderPath = string.Empty;

    [ObservableProperty]
    private string _folderError = string.Empty;

    [property: SettingName(nameof(ISettings.SplitTunnelingStandardFoldersList))]
    public SmartNotifyObservableCollection<SelectableTunnelingFolder> ExcludedFolders { get; } = [];

    [property: SettingName(nameof(ISettings.SplitTunnelingInverseFoldersList))]
    public SmartNotifyObservableCollection<SelectableTunnelingFolder> IncludedFolders { get; } = [];

    public SmartNotifyObservableCollection<SelectableTunnelingFolder> Folders => IsStandardSplitTunneling ? ExcludedFolders : IncludedFolders;

    public string FoldersHeader => Localizer.Get(IsStandardSplitTunneling ? "SplitTunneling_Folders_Excluded" : "SplitTunneling_Folders_Included");

    public override string Title => Localizer.Get("Settings_Connection_SplitTunneling");

    public ImageSource SplitTunnelingFeatureIconSource => GetFeatureIconSource(IsSplitTunnelingEnabled, CurrentSplitTunnelingMode);

    public string LearnMoreUrl => _urlsBrowser.SplitTunnelingLearnMore;

    public bool IsStandardSplitTunneling
    {
        get => IsSplitTunnelingMode(SplitTunnelingMode.Standard);
        set => SetSplitTunnelingMode(value, SplitTunnelingMode.Standard);
    }

    public bool IsInverseSplitTunneling
    {
        get => IsSplitTunnelingMode(SplitTunnelingMode.Inverse);
        set => SetSplitTunnelingMode(value, SplitTunnelingMode.Inverse);
    }

    [property: SettingName(nameof(ISettings.SplitTunnelingInverseIpAddressesList))]
    public SmartObservableCollection<SelectableSplitTunnelingAddress> IncludedIpAddresses { get; } = [];

    [property: SettingName(nameof(ISettings.SplitTunnelingStandardIpAddressesList))]
    public SmartObservableCollection<SelectableSplitTunnelingAddress> ExcludedIpAddresses { get; } = [];

    public SmartObservableCollection<SelectableSplitTunnelingAddress> IpAddresses
        => IsStandardSplitTunneling ? ExcludedIpAddresses : IncludedIpAddresses;

    public IEnumerable<SelectableSplitTunnelingAddress> SelectedIpAddresses
        => IpAddresses.Where(ip => ip.IsSelected);

    public bool HasSelectedIpAddresses => SelectedIpAddresses.Any();

    public bool HasIpv6AddressesWhileIpv6Disabled
        => IsSplitTunnelingEnabled
        && IsInverseSplitTunneling
        && !IsIpv6Enabled
        && SelectedIpAddresses.Any(ip => ip.IsIpV6);

    public string IpAddressesHeader => Localizer.GetFormat(IsStandardSplitTunneling
        ? "Settings_Connection_SplitTunneling_IpAddresses_Excluded_FormattedHeader"
        : "Settings_Connection_SplitTunneling_IpAddresses_Included_FormattedHeader", SelectedIpAddresses.Count());

    [property: SettingName(nameof(ISettings.SplitTunnelingInverseAppsList))]
    public SmartObservableCollection<SelectableTunnelingApp> IncludedApps { get; } = [];

    [property: SettingName(nameof(ISettings.SplitTunnelingStandardAppsList))]
    public SmartObservableCollection<SelectableTunnelingApp> ExcludedApps { get; } = [];

    public SmartObservableCollection<SelectableTunnelingApp> Apps
        => IsStandardSplitTunneling ? ExcludedApps : IncludedApps;

    public IEnumerable<TunnelingApp> SelectedApps
        => Apps.Where(app => app.IsSelected && app.Value.IsValid).Select(app => app.Value);

    public bool HasSelectedApps => SelectedApps.Any();

    public string AppsHeader => Localizer.GetFormat(IsStandardSplitTunneling
        ? "Settings_Connection_SplitTunneling_Apps_Excluded_FormattedHeader"
        : "Settings_Connection_SplitTunneling_Apps_Included_FormattedHeader", SelectedApps.Count());

    public SplitTunnelingPageViewModel(
        IUrlsBrowser urlsBrowser,
        IRequiredReconnectionSettings requiredReconnectionSettings,
        IMainViewNavigator mainViewNavigator,
        ISettingsViewNavigator settingsViewNavigator,
        IMainWindowOverlayActivator mainWindowOverlayActivator,
        ISettings settings,
        ISettingsConflictResolver settingsConflictResolver,
        IConnectionManager connectionManager,
        IIpSelector ipSelector,
        IAppSelector appSelector,
        IMainWindowActivator mainWindowActivator,
        IViewModelHelper viewModelHelper)
        : base(requiredReconnectionSettings,
               mainViewNavigator,
               settingsViewNavigator,
               mainWindowOverlayActivator,
               settings,
               settingsConflictResolver,
               connectionManager,
               viewModelHelper)
    {
        _urlsBrowser = urlsBrowser;
        _ipSelector = ipSelector;
        _appSelector = appSelector;
        _mainWindowActivator = mainWindowActivator;
        ExcludedFolders.CollectionChanged += OnFoldersChanged;
        IncludedFolders.CollectionChanged += OnFoldersChanged;
        ExcludedFolders.ItemPropertyChanged += OnFolderChanged;
        IncludedFolders.ItemPropertyChanged += OnFolderChanged;

        ExcludedIpAddresses.CollectionChanged += OnIpAddressesCollectionChanged;
        IncludedIpAddresses.CollectionChanged += OnIpAddressesCollectionChanged;
        ExcludedApps.CollectionChanged += OnAppsCollectionChanged;
        IncludedApps.CollectionChanged += OnAppsCollectionChanged;

        PageSettings =
        [
            ChangedSettingArgs.Create(() => Settings.SplitTunnelingStandardAppsList, () => GetSettingsApps(ExcludedApps)),
            ChangedSettingArgs.Create(() => Settings.SplitTunnelingStandardFoldersList, () => GetSettingsFolders(ExcludedFolders)),
            ChangedSettingArgs.Create(() => Settings.SplitTunnelingInverseFoldersList, () => GetSettingsFolders(IncludedFolders)),
            ChangedSettingArgs.Create(() => Settings.SplitTunnelingStandardIpAddressesList, () => GetSettingsIpAddresses(ExcludedIpAddresses)),
            ChangedSettingArgs.Create(() => Settings.SplitTunnelingInverseAppsList, () => GetSettingsApps(IncludedApps)),
            ChangedSettingArgs.Create(() => Settings.SplitTunnelingInverseIpAddressesList, () => GetSettingsIpAddresses(IncludedIpAddresses)),
            ChangedSettingArgs.Create(() => Settings.SplitTunnelingMode, () => CurrentSplitTunnelingMode),
            ChangedSettingArgs.Create(() => Settings.IsSplitTunnelingEnabled, () => IsSplitTunnelingEnabled),
            ChangedSettingArgs.Create(() => Settings.IsIpv6Enabled, () => IsIpv6Enabled)
        ];
    }

    public static ImageSource GetFeatureIconSource(bool isEnabled, SplitTunnelingMode mode)
    {
        if (!isEnabled)
        {
            return ResourceHelper.GetIllustration("SplitTunnelingOffIllustrationSource");
        }

        return mode switch
        {
            SplitTunnelingMode.Standard => ResourceHelper.GetIllustration("SplitTunnelingStandardIllustrationSource"),
            SplitTunnelingMode.Inverse => ResourceHelper.GetIllustration("SplitTunnelingInverseIllustrationSource"),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    [RelayCommand]
    public async Task TriggerIpv6DisabledWarningAsync()
    {
        if (await ShowIpv6DisabledWarningAsync())
        {
            IsIpv6Enabled = true;
        }

        // Show this warning only once per app launch
        _wasIpv6WarningDisplayed = true;
    }

    [RelayCommand]
    public async Task SelectIpsAsync()
    {
        _ipSelector.Title = Localizer.Get(IsStandardSplitTunneling
            ? "Settings_Connection_SplitTunneling_IpAddresses_Excluded_Header"
            : "Settings_Connection_SplitTunneling_IpAddresses_Included_Header");
        _ipSelector.Description = Localizer.Get(IsStandardSplitTunneling
            ? "Settings_Connection_SplitTunneling_IpAddresses_Excluded_Description"
            : "Settings_Connection_SplitTunneling_IpAddresses_Included_Description");
        _ipSelector.Caption = Localizer.Get("Settings_Connection_SplitTunneling_IpAddresses_AddNew");
        _ipSelector.CanReorder = false;
        _ipSelector.IsAddressRangeAuthorized = true;

        List<SelectableSplitTunnelingAddress>? result = await _ipSelector.SelectSplitTunnelingAddressesAsync(IpAddresses.Select(ip => ip.Clone()).ToList());
        if (result != null)
        {
            IpAddresses.Reset(result);

            if (HasIpv6AddressesWhileIpv6Disabled && !_wasIpv6WarningDisplayed)
            {
                await TriggerIpv6DisabledWarningAsync();
            }
        }
    }

    [RelayCommand]
    public async Task SelectAppsAsync()
    {
        _appSelector.Title = Localizer.Get(IsStandardSplitTunneling
            ? "Settings_Connection_SplitTunneling_Apps_Excluded_Header"
            : "Settings_Connection_SplitTunneling_Apps_Included_Header");
        _appSelector.Description = Localizer.Get(IsStandardSplitTunneling
            ? "Settings_Connection_SplitTunneling_Apps_Excluded_Description"
            : "Settings_Connection_SplitTunneling_Apps_Included_Description");

        List<SelectableTunnelingApp>? result = await _appSelector.SelectAsync(Apps.Select(app => app.Clone()).ToList());
        if (result != null)
        {
            Apps.Reset(result);
        }
    }

    [RelayCommand]
    public async Task BrowseFolderAsync()
    {
        if (_mainWindowActivator.Window == null) { return; }
        try
        {
            string path = await _mainWindowActivator.Window.PickFolderAsync();
            if (path.Length > 0) { CustomFolderPath = path; await AddFolderAsync(); }
        }
        catch (Exception)
        {
            FolderError = Localizer.Get("SplitTunneling_Folders_PickerError");
        }
    }

    [RelayCommand]
    public async Task AddFolderAsync()
    {
        FolderError = string.Empty;
        SmartNotifyObservableCollection<SelectableTunnelingFolder> folders = Folders;
        if (!SplitTunnelFolderScanner.TryNormalize(CustomFolderPath, out string path))
        {
            FolderError = Localizer.Get("SplitTunneling_Folders_Invalid");
            return;
        }
        SelectableTunnelingFolder? existing = Folders.FirstOrDefault(folder => string.Equals(folder.FolderPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { existing.IsSelected = true; CustomFolderPath = string.Empty; return; }
        if (Folders.Count >= SplitTunnelFolderScanner.MaximumFolders)
        {
            FolderError = Localizer.Get("SplitTunneling_Folders_Limit");
            return;
        }
        FolderScanResult scan = await Task.Run(() => SplitTunnelFolderScanner.Scan(path));
        if (scan.Error != null) { FolderError = scan.Error; return; }
        if (!ReferenceEquals(folders, Folders)) { FolderError = Localizer.Get("SplitTunneling_Folders_ModeChanged"); return; }
        // Browse and manual Add can finish concurrently; recheck the collection after the scan.
        if (folders.Count >= SplitTunnelFolderScanner.MaximumFolders) { FolderError = Localizer.Get("SplitTunneling_Folders_Limit"); return; }
        if (!folders.Any(folder => string.Equals(folder.FolderPath, path, StringComparison.OrdinalIgnoreCase))) { folders.Add(new(path)); }
        CustomFolderPath = string.Empty;
    }

    [RelayCommand]
    public void RemoveFolder(SelectableTunnelingFolder folder) => Folders.Remove(folder);

    private static List<SplitTunnelingFolder> GetSettingsFolders(IEnumerable<SelectableTunnelingFolder> folders) =>
        folders.Select(folder => new SplitTunnelingFolder(folder.FolderPath, folder.IsSelected)).ToList();

    private void OnFoldersChanged(object? sender, NotifyCollectionChangedEventArgs args) => OnFolderChanged(sender, new(nameof(Folders)));
    private void OnFolderChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(Folders));
        OnPropertyChanged(nameof(ExcludedFolders));
        OnPropertyChanged(nameof(IncludedFolders));
    }

    protected override async Task OnRetrieveSettingsAsync()
    {
        try
        {
            IsLoading = true;

            IsIpv6Enabled = Settings.IsIpv6Enabled;
            IsSplitTunnelingEnabled = Settings.IsSplitTunnelingEnabled;
            CurrentSplitTunnelingMode = Settings.SplitTunnelingMode;
            CustomFolderPath = string.Empty;
            FolderError = string.Empty;
            ExcludedFolders.Reset(Settings.SplitTunnelingStandardFoldersList.Select(folder => new SelectableTunnelingFolder(folder.FolderPath, folder.IsActive)));
            IncludedFolders.Reset(Settings.SplitTunnelingInverseFoldersList.Select(folder => new SelectableTunnelingFolder(folder.FolderPath, folder.IsActive)));

            ExcludedIpAddresses.Reset(GetObservableIpAddresses(Settings.SplitTunnelingStandardIpAddressesList));
            IncludedIpAddresses.Reset(GetObservableIpAddresses(Settings.SplitTunnelingInverseIpAddressesList));

            ExcludedApps.Reset(await GetObservableAppsAsync(Settings.SplitTunnelingStandardAppsList));
            IncludedApps.Reset(await GetObservableAppsAsync(Settings.SplitTunnelingInverseAppsList));
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected override bool IsReconnectionRequiredDueToChanges(IEnumerable<ChangedSettingArgs> changedSettings)
    {
        bool isReconnectionRequired = base.IsReconnectionRequiredDueToChanges(changedSettings);
        if (isReconnectionRequired)
        {
            // Check if there was any active apps or IP adresses from the settings
            // then check if there is any active apps or IP adresses now.
            // If there is none in both case, no need to reconnect.
            bool isSameSplitTunnelingMode = CurrentSplitTunnelingMode == Settings.SplitTunnelingMode;
            bool hadAnyActiveAppsOrIps =
                Settings.IsSplitTunnelingEnabled &&
                Settings.SplitTunnelingMode switch
                {
                    SplitTunnelingMode.Standard => Settings.SplitTunnelingStandardAppsList.Any(app => app.IsActive) || Settings.SplitTunnelingStandardIpAddressesList.Any(ip => ip.IsActive) || Settings.SplitTunnelingStandardFoldersList.Any(folder => folder.IsActive),
                    SplitTunnelingMode.Inverse => Settings.SplitTunnelingInverseAppsList.Any(app => app.IsActive) || Settings.SplitTunnelingInverseIpAddressesList.Any(ip => ip.IsActive) || Settings.SplitTunnelingInverseFoldersList.Any(folder => folder.IsActive),
                    _ => false
                };
            bool hasAnyActiveAppsOrIps =
                IsSplitTunnelingEnabled &&
                CurrentSplitTunnelingMode switch
                {
                    SplitTunnelingMode.Standard => ExcludedApps.Any(app => app.IsSelected) || ExcludedIpAddresses.Any(ip => ip.IsSelected) || ExcludedFolders.Any(folder => folder.IsSelected),
                    SplitTunnelingMode.Inverse => IncludedApps.Any(app => app.IsSelected) || IncludedIpAddresses.Any(ip => ip.IsSelected) || IncludedFolders.Any(folder => folder.IsSelected),
                    _ => false
                };
            if (isSameSplitTunnelingMode && !hadAnyActiveAppsOrIps && !hasAnyActiveAppsOrIps)
            {
                return false;
            }
        }

        return isReconnectionRequired;
    }

    private List<SplitTunnelingIpAddress> GetSettingsIpAddresses(IEnumerable<SelectableSplitTunnelingAddress> ipAddresses)
    {
        return ipAddresses.Select(ip => new SplitTunnelingIpAddress(ip.Value, ip.IsSelected)).ToList();
    }

    private List<SelectableSplitTunnelingAddress> GetObservableIpAddresses(List<SplitTunnelingIpAddress> settingsIpAddresses)
    {
        List<SelectableSplitTunnelingAddress> addresses = [];

        foreach (SplitTunnelingIpAddress ip in settingsIpAddresses)
        {
            if (SelectableSplitTunnelingAddress.TryCreate(ip.IpAddress, ip.IsActive, out SelectableSplitTunnelingAddress? address) && address != null)
            {
                addresses.Add(address);
            }
        }

        return addresses;
    }

    private List<SplitTunnelingApp> GetSettingsApps(IEnumerable<SelectableTunnelingApp> apps)
    {
        return apps.Select(ip => new SplitTunnelingApp(ip.Value.AppPath, ip.Value.AlternateAppPaths, ip.IsSelected)).ToList();
    }

    private async Task<List<SelectableTunnelingApp>> GetObservableAppsAsync(List<SplitTunnelingApp> settingsApps)
    {
        List<SelectableTunnelingApp> apps = [];

        foreach (SplitTunnelingApp app in settingsApps)
        {
            TunnelingApp tunnelingApp = await TunnelingApp.TryCreateAsync(app.AppFilePath, app.AlternateAppFilePaths)
                ?? TunnelingApp.NotFound(app.AppFilePath, Localizer.Get("Common_Message_AppNotFound"), app.AlternateAppFilePaths);

            apps.Add(new SelectableTunnelingApp(tunnelingApp, app.IsActive));
        }

        return apps;
    }

    private bool IsSplitTunnelingMode(SplitTunnelingMode splitTunnelingMode)
    {
        return CurrentSplitTunnelingMode == splitTunnelingMode;
    }

    private void SetSplitTunnelingMode(bool value, SplitTunnelingMode splitTunnelingMode)
    {
        if (value)
        {
            CurrentSplitTunnelingMode = splitTunnelingMode;
        }
    }

    private void OnAppsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Apps));
        OnPropertyChanged(nameof(AppsHeader));
        OnPropertyChanged(nameof(SelectedApps));
        OnPropertyChanged(nameof(HasSelectedApps));
    }

    private void OnIpAddressesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IpAddresses));
        OnPropertyChanged(nameof(IpAddressesHeader));
        OnPropertyChanged(nameof(SelectedIpAddresses));
        OnPropertyChanged(nameof(HasSelectedIpAddresses));
        OnPropertyChanged(nameof(HasIpv6AddressesWhileIpv6Disabled));
    }
}
