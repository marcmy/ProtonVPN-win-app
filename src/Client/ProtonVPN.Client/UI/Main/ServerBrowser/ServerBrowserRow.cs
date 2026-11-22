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

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ProtonVPN.Client.Common.UI.ServerHealth;
using ProtonVPN.Client.Localization.Contracts;
using ProtonVPN.Client.Localization.Extensions;
using ProtonVPN.Client.Logic.Servers.Contracts.Enums;
using ProtonVPN.Client.Logic.Servers.Contracts.Extensions;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;
using ProtonVPN.Client.Models.Connections;

namespace ProtonVPN.Client.UI.Main.ServerBrowser;

public sealed partial class ServerBrowserRow : ObservableObject
{
    private readonly ILocalizationProvider _localizer;

    public Server Server { get; }
    internal ServerPingSource Source { get; }
    internal ServerHealthHistoryKey? Key { get; }
    internal double? CurrentPing { get; private set; }
    internal double? CurrentLoss { get; private set; }
    internal int LossSampleCount { get; private set; }
    internal DateTimeOffset? FreshUntil { get; private set; }

    public string Id => Server.Id;
    public string Name => Server.Name;
    public string CountryCode => Server.ExitCountry;
    public string Country { get; }
    public string City { get; }
    public string Location { get; }
    public string EntryCountry { get; }
    public bool IsSecureCore { get; }
    public bool IsUnderMaintenance => Server.IsUnderMaintenance();
    public string LoadText => (Server.Load / 100d).ToString("P0", CultureInfo.CurrentCulture);
    public string AutomationName => _localizer.GetFormat("ServerBrowser_RowAutomationName",
        Location, Name, IdentityState, PingText, LossText, LoadText, MeasurementStatus);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string PingText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string LossText { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string MeasurementStatus { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string IdentityState { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    internal ServerBrowserRow(Server server, ILocalizationProvider localizer)
    {
        Server = server;
        _localizer = localizer;
        Source = new(server);
        Key = Source.HealthProbeAddress is string address ? ServerHealthHistoryKey.Create(server.Id, address) : null;
        Country = localizer.GetCountryName(server.ExitCountry);
        City = string.IsNullOrWhiteSpace(server.City) ? string.Empty : localizer.GetCityName(server.City, server.ExitCountry);
        string exit = string.IsNullOrWhiteSpace(City)
            ? Country
            : localizer.GetFormat("ServerBrowser_CityCountry", City, Country);
        IsSecureCore = server.Features.IsSupported(ServerFeatures.SecureCore);
        EntryCountry = IsSecureCore ? localizer.GetCountryName(server.EntryCountry) : string.Empty;
        Location = IsSecureCore
            ? localizer.GetFormat("ServerBrowser_SecureCoreLocation", exit, EntryCountry)
            : exit;
        UpdateConnectionState(false, false);
    }

    internal void UpdateConnectionState(bool isConnected, bool isRestricted)
    {
        string connected = isConnected ? _localizer.Get("ServerBrowser_Connected") : string.Empty;
        string eligibility = IsUnderMaintenance
            ? _localizer.Get("ServerBrowser_Maintenance")
            : isRestricted ? _localizer.Get("Connections_Server_Restricted") : string.Empty;
        IdentityState = connected.Length > 0 && eligibility.Length > 0
            ? $"{connected} · {eligibility}"
            : connected + eligibility;
    }

    internal bool UpdateMeasurement(ServerHealthSnapshot? snapshot)
    {
        double? previousPing = CurrentPing;
        double? previousLoss = CurrentLoss;
        int previousCount = LossSampleCount;

        ServerHealthPresentation? presentation = snapshot is null ? null : ServerHealthPresentation.FromSnapshot(snapshot);
        bool isCurrent = !IsUnderMaintenance && snapshot is { IsCached: false, IsStale: false };
        CurrentPing = isCurrent ? snapshot!.CurrentLatencyMilliseconds : null;
        FreshUntil = isCurrent ? snapshot!.FreshUntil : null;
        ServerHealthAggregate? aggregate = snapshot?.Aggregate;
        CurrentLoss = isCurrent && presentation?.IsLossEstablished == true ? aggregate?.PacketLossPercent : null;
        LossSampleCount = CurrentLoss.HasValue ? aggregate!.TotalSamples : 0;
        PingText = presentation?.LatencyText ?? "—";
        LossText = CurrentLoss is double loss
            ? (loss / 100).ToString("P0", CultureInfo.CurrentCulture)
            : CurrentPing.HasValue
                ? _localizer.Get("ServerBrowser_Sampling")
                : "—";
        MeasurementStatus = GetMeasurementStatus(snapshot, aggregate);

        return previousPing != CurrentPing || previousLoss != CurrentLoss || previousCount != LossSampleCount;
    }

    private string GetMeasurementStatus(ServerHealthSnapshot? snapshot, ServerHealthAggregate? aggregate)
    {
        if (IsUnderMaintenance)
        {
            return string.Empty;
        }

        string? key = snapshot switch
        {
            null => "ServerBrowser_Unavailable",
            { IsStale: true } or { IsCached: true } when snapshot.SavedLatencyMilliseconds.HasValue
                || aggregate?.AverageLatencyMilliseconds is not null => "ServerBrowser_Saved",
            { LatestMeasurement.IsNoReply: true } => "ServerBrowser_NoReply",
            { LatestMeasurement: { IsCompleteFailure: true, UsedPhysicalRoute: false } } => "ServerBrowser_RouteUnavailable",
            { LatestMeasurement.IsCompleteFailure: true } => "ServerBrowser_Unavailable",
            { LatestMeasurement: { IsDiscovery: false, Observations: not null, IsQualifiedFixedCheck: false } } =>
                "ServerBrowser_CheckIncomplete",
            { IsChecking: true, LatestMeasurement: null } => "ServerBrowser_Checking",
            { LatestMeasurement: null } => "ServerBrowser_NotMeasured",
            _ when aggregate?.IsLatencyEstimate == true => "ServerBrowser_Estimate",
            _ when CurrentLoss is null => "ServerBrowser_Sampling",
            _ => null,
        };

        return key is null ? string.Empty : _localizer.Get(key);
    }

    internal bool Matches(string query, CompareInfo compareInfo)
    {
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return query.Length == 0
            || Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || CountryCode.Equals(query, StringComparison.OrdinalIgnoreCase)
            || compareInfo.IndexOf(Location, query, options) >= 0
            || compareInfo.IndexOf(Server.City, query, options) >= 0;
    }
}

public sealed class ServerBrowserGroup(IReadOnlyList<ServerBrowserRow> rows, string title = "") : List<ServerBrowserRow>(rows)
{
    public string Title { get; } = title;
    public bool HasTitle => Title.Length > 0;
}
