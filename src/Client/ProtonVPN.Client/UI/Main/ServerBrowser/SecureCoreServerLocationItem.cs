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

using ProtonVPN.Client.Contracts.Enums;
using ProtonVPN.Client.Core.Services.Activation;
using ProtonVPN.Client.Localization.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Models.Intents.Features;
using ProtonVPN.Client.Logic.Servers.Contracts;
using ProtonVPN.Client.Logic.Servers.Contracts.Models;
using ProtonVPN.Client.Models.Connections;

namespace ProtonVPN.Client.UI.Main.ServerBrowser;

internal sealed class SecureCoreServerLocationItem(
    ILocalizationProvider localizer,
    IServersLoader serversLoader,
    IConnectionManager connectionManager,
    IUpsellCarouselWindowActivator upsellActivator,
    Server server) : ServerLocationItemBase(localizer, serversLoader, connectionManager, upsellActivator, server, isSearchItem: true)
{
    public override ConnectionGroupType GroupType => ConnectionGroupType.Servers;
    public override IFeatureIntent FeatureIntent { get; } = new SecureCoreFeatureIntent(server.EntryCountry);
}
