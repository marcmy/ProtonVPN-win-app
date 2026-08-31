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
        Dictionary<string, List<Guid>> stagedAddresses = new(StringComparer.OrdinalIgnoreCase);
        List<string> staleAddresses = [];
        bool transactionStarted = false;

        try
        {
            StartTransaction();
            transactionStarted = true;

            foreach (string address in addresses ?? [])
            {
                if (!TryStageAddressFilters(address, action, desiredAddresses, stagedAddresses))
                {
                    AbortTransaction();
                    transactionStarted = false;
                    return;
                }
            }

            staleAddresses = _list.Keys
                .Where(address => !desiredAddresses.Contains(address))
                .ToList();

            foreach (string staleAddress in staleAddresses)
            {
                RemoveGuids(_list[staleAddress]);
            }

            CommitTransaction();
            transactionStarted = false;
        }
        finally
        {
            if (transactionStarted)
            {
                AbortTransaction();
            }
        }

        foreach ((string address, List<Guid> guids) in stagedAddresses)
        {
            _list[address] = guids;
        }

        foreach (string staleAddress in staleAddresses)
        {
            _list.Remove(staleAddress);
        }
    }

    private bool TryStageAddressFilters(
        string address,
        Action action,
        HashSet<string> desiredAddresses,
        Dictionary<string, List<Guid>> stagedAddresses)
    {
        if (!CoreNetworkAddress.TryParse(address, out CoreNetworkAddress networkAddress))
        {
            return false;
        }

        string normalizedAddress = networkAddress.ToString();
        desiredAddresses.Add(normalizedAddress);

        if (_list.ContainsKey(normalizedAddress) || stagedAddresses.ContainsKey(normalizedAddress))
        {
            return true;
        }

        if (!TryCreateFilters(networkAddress, action, out List<Guid> guids))
        {
            return false;
        }

        stagedAddresses[normalizedAddress] = guids;
        return true;
    }

    protected virtual void StartTransaction()
    {
        _ipFilter.DynamicInstance.Session.StartTransaction();
    }

    protected virtual void AbortTransaction()
    {
        _ipFilter.DynamicInstance.Session.AbortTransaction();
    }

    protected virtual void CommitTransaction()
    {
        _ipFilter.DynamicInstance.Session.CommitTransaction();
    }

    protected virtual bool TryCreateFilters(CoreNetworkAddress networkAddress, Action action, out List<Guid> guids)
    {
        List<Guid> createdGuids = [];

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

                    createdGuids.Add(guid);
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

                    createdGuids.Add(guid);
                });
            }

            guids = createdGuids;
            return guids.Count > 0;
        }
        catch (InvalidArgumentException)
        {
            _logger.Error<SplitTunnelLog>($"Failed to create permitted remote address filter for address {networkAddress} due to invalid argument.");
            RemoveGuids(createdGuids);
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

    protected virtual void RemoveGuids(List<Guid> guids)
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
