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
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class DomainResolvedAddressTracker
{
    private static readonly TimeSpan _minimumTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _gracePeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _maximumLifetime = TimeSpan.FromHours(1);

    private readonly Func<DateTime> _utcNow;
    private readonly Dictionary<(string OwnerDomain, string IpAddress), DomainResolvedAddress> _entries = [];

    public DomainResolvedAddressTracker()
        : this(() => DateTime.UtcNow)
    {
    }

    public DomainResolvedAddressTracker(Func<DateTime> utcNow)
    {
        _utcNow = utcNow;
    }

    public void AddOrRefresh(string ownerDomain, IPAddress ipAddress, int ttlSeconds)
    {
        string normalizedOwner = ownerDomain.Trim().TrimEnd('.').ToLowerInvariant();
        TimeSpan ttl = TimeSpan.FromSeconds(Math.Max(0, ttlSeconds));
        TimeSpan lifetime = (ttl < _minimumTtl ? _minimumTtl : ttl) + _gracePeriod;
        if (lifetime > _maximumLifetime)
        {
            lifetime = _maximumLifetime;
        }

        _entries[(normalizedOwner, ipAddress.ToString())] = new(
            normalizedOwner,
            ipAddress,
            _utcNow().Add(lifetime));
    }

    public void RetainOwners(IEnumerable<string> ownerDomains)
    {
        HashSet<string> retainedOwners = new(ownerDomains, StringComparer.OrdinalIgnoreCase);
        foreach ((string ownerDomain, string ipAddress) in _entries.Keys
                     .Where(key => !retainedOwners.Contains(key.OwnerDomain))
                     .ToArray())
        {
            _entries.Remove((ownerDomain, ipAddress));
        }
    }

    public IReadOnlyCollection<DomainResolvedAddress> GetActive()
    {
        PruneExpired();
        return _entries.Values.ToArray();
    }

    public string[] GetActiveIpv4Addresses()
    {
        return GetActive()
            .Where(entry => entry.IpAddress.AddressFamily == AddressFamily.InterNetwork)
            .Select(entry => entry.IpAddress.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void PruneExpired()
    {
        DateTime now = _utcNow();
        foreach ((string ownerDomain, string ipAddress) in _entries
                     .Where(pair => pair.Value.ExpiresAtUtc <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove((ownerDomain, ipAddress));
        }
    }

    public void Clear()
    {
        _entries.Clear();
    }
}
