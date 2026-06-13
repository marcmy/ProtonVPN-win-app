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
using System.Collections.Generic;
using System.Linq;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;
using ProtonVPN.NetworkFilter;
using Action = ProtonVPN.NetworkFilter.Action;
using CoreNetworkAddress = ProtonVPN.Common.Core.Networking.NetworkAddress;
using FilterNetworkAddress = ProtonVPN.NetworkFilter.NetworkAddress;

namespace ProtonVPN.Service.Firewall;

public class PermittedRemoteAddress : IPermittedRemoteAddress
{
    private readonly ILogger _logger;
    private readonly IpLayer _ipLayer;
    private readonly IpFilter _ipFilter;

    private readonly Dictionary<string, List<Guid>> _list = new(StringComparer.OrdinalIgnoreCase);

    public PermittedRemoteAddress(ILogger logger, IpFilter ipFilter, IpLayer ipLayer)
    {
        _logger = logger;
        _ipLayer = ipLayer;
        _ipFilter = ipFilter;
    }

    public void Add(string[] addresses, Action action)
    {
        HashSet<string> desiredAddresses = new(StringComparer.OrdinalIgnoreCase);
        bool hasFailures = false;

        foreach (string address in addresses ?? [])
        {
            if (!TryCreateAddressFilters(address, action, desiredAddresses))
            {
                hasFailures = true;
            }
        }

        if (hasFailures)
        {
            return;
        }

        foreach (string staleAddress in _list.Keys.Where(address => !desiredAddresses.Contains(address)).ToList())
        {
            Remove(staleAddress);
        }
    }

    private bool TryCreateAddressFilters(string address, Action action, HashSet<string> desiredAddresses)
    {
        if (!CoreNetworkAddress.TryParse(address, out CoreNetworkAddress networkAddress))
        {
            return false;
        }

        string normalizedAddress = networkAddress.ToString();
        desiredAddresses.Add(normalizedAddress);

        if (_list.ContainsKey(normalizedAddress))
        {
            return true;
        }

        if (!TryCreateFilters(networkAddress, action, out List<Guid> guids))
        {
            return false;
        }

        _list[normalizedAddress] = guids;
        return true;
    }

    private bool TryCreateFilters(CoreNetworkAddress networkAddress, Action action, out List<Guid> guids)
    {
        guids = [];

        try
        {
            if (networkAddress.IsIpV6)
            {
                _ipLayer.ApplyToIpv6(layer =>
                {
                    Guid guid = _ipFilter.DynamicSublayer.CreateRemoteNetworkIPFilter(
                        new DisplayData("ProtonVPN permit remote address", ""),
                        action,
                        layer,
                        14,
                        FilterNetworkAddress.FromIpv6(networkAddress.Ip.ToString(), networkAddress.Subnet));

                    guids.Add(guid);
                });
            }
            else
            {
                _ipLayer.ApplyToIpv4(layer =>
                {
                    Guid guid = _ipFilter.DynamicSublayer.CreateRemoteNetworkIPFilter(
                        new DisplayData("ProtonVPN permit remote address", ""),
                        action,
                        layer,
                        14,
                        FilterNetworkAddress.FromIpv4(networkAddress.Ip.ToString(), networkAddress.GetSubnetMaskString()));

                    guids.Add(guid);
                });
            }

            return guids.Count > 0;
        }
        catch (InvalidArgumentException)
        {
            _logger.Error<SplitTunnelLog>($"Failed to create permitted remote address filter for address {networkAddress} due to invalid argument.");
            RemoveGuids(guids);
            guids = [];
            return false;
        }
    }

    public void Remove(string address)
    {
        if (!_list.ContainsKey(address))
        {
            return;
        }

        RemoveGuids(_list[address]);
        _list.Remove(address);
    }

    private void RemoveGuids(List<Guid> guids)
    {
        foreach (Guid guid in guids)
        {
            _ipFilter.DynamicSublayer.DestroyFilter(guid);
        }
    }

    public void RemoveAll()
    {
        if (_list.Count == 0)
        {
            return;
        }

        foreach (string address in _list.Keys.ToList())
        {
            Remove(address);
        }
    }
}
