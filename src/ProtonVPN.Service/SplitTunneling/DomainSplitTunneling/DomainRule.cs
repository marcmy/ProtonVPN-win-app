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

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class DomainRule : IEquatable<DomainRule>
{
    public string Domain { get; }

    private DomainRule(string domain)
    {
        Domain = domain;
    }

    public static bool TryCreate(string? value, out DomainRule? rule)
    {
        rule = null;
        string normalized = Normalize(value);

        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }
        else if (normalized.Contains('*'))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Contains('/') ||
            normalized.Contains(':') ||
            Uri.CheckHostName(normalized) != UriHostNameType.Dns)
        {
            return false;
        }

        rule = new DomainRule(normalized);
        return true;
    }

    public bool IsMatch(string? hostname)
    {
        string normalized = Normalize(hostname);
        return normalized.Equals(Domain, StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith($".{Domain}", StringComparison.OrdinalIgnoreCase);
    }

    public bool Equals(DomainRule? other)
    {
        return other is not null &&
               Domain.Equals(other.Domain, StringComparison.OrdinalIgnoreCase);
    }

    public override bool Equals(object? obj)
    {
        return obj is DomainRule other && Equals(other);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Domain);
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
    }
}
