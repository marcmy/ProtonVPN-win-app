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

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public static class SystemDnsCacheParser
{
    public static IReadOnlyCollection<SystemDnsCacheEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) ||
            json.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            List<SystemDnsCacheEntry> entries = [];

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in document.RootElement.EnumerateArray())
                {
                    TryAddEntry(element, entries);
                }
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                TryAddEntry(document.RootElement, entries);
            }

            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void TryAddEntry(JsonElement element, List<SystemDnsCacheEntry> entries)
    {
        if (!TryGetString(element, "Entry", out string hostname) ||
            !TryGetString(element, "Data", out string data) ||
            !IPAddress.TryParse(data, out IPAddress? ipAddress))
        {
            return;
        }

        int timeToLive = TryGetInt32(element, "TimeToLive", out int parsedTimeToLive)
            ? Math.Max(0, parsedTimeToLive)
            : 0;

        entries.Add(new(
            hostname.Trim().TrimEnd('.').ToLowerInvariant(),
            ipAddress,
            timeToLive));
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out JsonElement property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt32(out value);
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(property.GetString(), out value);
    }
}
