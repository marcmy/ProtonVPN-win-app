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
using ProtonVPN.NetworkFilter;
using ProtonVPN.Service.Firewall;
using FilterAction = ProtonVPN.NetworkFilter.Action;
using FilterNetworkAddress = ProtonVPN.NetworkFilter.NetworkAddress;
using ServiceIpFilter = ProtonVPN.Service.Firewall.IpFilter;

namespace ProtonVPN.Service.ServerHealth;

internal sealed class ServerHealthPermitManager : IServerHealthPermitManager
{
    private readonly ServiceIpFilter _ipFilter;
    private readonly IpLayer _ipLayer;

    public ServerHealthPermitManager(ServiceIpFilter ipFilter, IpLayer ipLayer)
    {
        _ipFilter = ipFilter;
        _ipLayer = ipLayer;
    }

    public IServerHealthPermitLease? TryCreate(IPAddress ipAddress)
    {
        List<Guid> filterIds = [];

        try
        {
            _ipLayer.ApplyToIpv4(layer =>
            {
                Guid filterId = _ipFilter.DynamicSublayer.CreateRemoteNetworkIPFilter(
                    new DisplayData("ProtonVPN server health direct probe", string.Empty),
                    FilterAction.HardPermit,
                    layer,
                    14,
                    FilterNetworkAddress.FromIpv4(ipAddress.ToString(), "255.255.255.255"));
                filterIds.Add(filterId);
            });

            return filterIds.Count == 0
                ? null
                : new PermitLease(_ipFilter, filterIds);
        }
        catch
        {
            DestroyFilters(_ipFilter, filterIds);
            return null;
        }
    }

    private static void DestroyFilters(ServiceIpFilter ipFilter, IEnumerable<Guid> filterIds)
    {
        foreach (Guid filterId in filterIds)
        {
            try
            {
                ipFilter.DynamicSublayer.DestroyFilter(filterId);
            }
            catch
            {
            }
        }
    }

    private sealed class PermitLease : IServerHealthPermitLease
    {
        private readonly ServiceIpFilter _ipFilter;
        private readonly IReadOnlyCollection<Guid> _filterIds;
        private bool _isDisposed;

        public PermitLease(ServiceIpFilter ipFilter, IReadOnlyCollection<Guid> filterIds)
        {
            _ipFilter = ipFilter;
            _filterIds = filterIds;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            DestroyFilters(_ipFilter, _filterIds);
        }
    }
}
