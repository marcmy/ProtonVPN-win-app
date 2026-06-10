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

using System;
using ProtonVPN.Common.Core.Networking;

namespace ProtonVPN.Client.Core.Models;

public class SelectableSplitTunnelingAddress : Selectable<string>
{
    public NetworkAddress? ParsedNetworkAddress
        => NetworkAddress.TryParse(Value, out NetworkAddress address) ? address : null;

    public bool IsIpAddress => ParsedNetworkAddress.HasValue;

    public bool IsHostname => !IsIpAddress;

    public bool IsIpV6 => ParsedNetworkAddress?.IsIpV6 == true;

    public string FormattedAddress => Value;

    public SelectableSplitTunnelingAddress(string value)
        : base(Normalize(value))
    { }

    public SelectableSplitTunnelingAddress(string value, bool isSelected)
        : base(Normalize(value), isSelected)
    { }

    public SelectableSplitTunnelingAddress Clone()
    {
        return new SelectableSplitTunnelingAddress(Value, IsSelected);
    }

    public static bool TryCreate(string? value, bool isSelected, out SelectableSplitTunnelingAddress? address)
    {
        address = null;
        string normalized = Normalize(value);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (NetworkAddress.TryParse(normalized, out NetworkAddress networkAddress))
        {
            address = new SelectableSplitTunnelingAddress(networkAddress.ToString(), isSelected);
            return true;
        }

        if (IsValidHostname(normalized))
        {
            address = new SelectableSplitTunnelingAddress(normalized.ToLowerInvariant(), isSelected);
            return true;
        }

        return false;
    }

    public static bool IsValidHostname(string hostname)
    {
        return !string.IsNullOrWhiteSpace(hostname)
            && !hostname.Contains('/')
            && !hostname.Contains('*')
            && Uri.CheckHostName(hostname) == UriHostNameType.Dns;
    }

    private static string Normalize(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }
}
