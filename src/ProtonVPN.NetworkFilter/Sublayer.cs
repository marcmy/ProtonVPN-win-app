/*
 * Copyright (c) 2023 Proton AG
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

namespace ProtonVPN.NetworkFilter
{
    public class Sublayer
    {
        private readonly IpFilter _ipFilter;
        // Native WFP owns the filter inventory. App rules and probe leases own
        // their returned IDs; a second append-only cache races across callers
        // and can throw after native creation succeeds.

        public Sublayer(IpFilter ipFilter, Guid id)
        {
            _ipFilter = ipFilter;
            Id = id;
        }

        public Guid Id { get; }

        public void DestroyAllFilters()
        {
            IpFilterNative.DestroySublayerFilters(Session.Handle, _ipFilter.ProviderId, Id);
        }

        public void DestroyFiltersByName(string name)
        {
            IpFilterNative.DestroySublayerFiltersByName(Session.Handle, _ipFilter.ProviderId, Id, name);
        }

        public List<Guid> GetFilters()
        {
            return IpFilterNative.GetSublayerFilters(Session.Handle, ProviderId, Id);
        }

        public uint GetFilterCount()
        {
            return (uint)GetFilters().Count;
        }

        public void DestroyFilter(Guid filterId)
        {
            try
            {
                IpFilterNative.DestroyFilter(
                    Session.Handle,
                    filterId);
            }
            catch (FilterNotFoundException)
            {
            }
        }

        public Guid CreateLayerFilter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateLayerFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        public Guid CreateRemoteIPv4Filter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            string address,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateRemoteIPv4Filter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                address,
                persistent,
                id);
        }

        /// <summary>
        /// Creates an application filter for the specified application.
        /// </summary>
        /// <param name="appIdentifier">Either the full path to the executable or the package family name for UWP apps.</param>
        public Guid CreateAppFilter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            string appIdentifier,
            bool isDnsPortExcluded,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateAppFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty, 
                Guid.Empty,
                appIdentifier,
                isDnsPortExcluded,
                persistent,
                id);
        }

        public Guid CreateAppCalloutFilter(DisplayData displayData,
            Layer layer,
            uint weight,
            Callout callout,
            ProviderContext providerContext,
            string appPath,
            bool isDnsPortExcluded,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateAppFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                Action.Callout,
                weight,
                callout.Id,
                providerContext.Id,
                appPath,
                isDnsPortExcluded,
                persistent,
                id);
        }

        public Guid CreateRemoteNetworkIPFilter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            NetworkAddress addr,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateRemoteNetworkIPFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                addr,
                persistent,
                id);
        }

        public Guid CreateRemoteUdpPortFilter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            uint port,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateRemoteUdpPortFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                port,
                persistent,
                id);
        }

        public Guid CreateRemoteTcpPortFilter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            uint port,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateRemoteTcpPortFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                port,
                persistent,
                id);
        }

        public Guid CreateNetInterfaceFilter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint index,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.CreateNetInterfaceFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                index,
                persistent,
                id);
        }

        public Guid CreateLoopbackFilter(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false)
        {
            return IpFilterNative.CreateLoopbackFilter(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                persistent);
        }

        public Guid BlockOutsideDns(DisplayData displayData,
            Layer layer,
            uint weight,
            Guid calloutGuid,
            uint index,
            bool persistent = false)
        {
            return IpFilterNative.BlockOutsideDns(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                Action.Callout,
                weight,
                calloutGuid,
                index,
                (uint) (persistent ? 1 : 0));
        }

        public Guid BlockOutsideOpenVpn(DisplayData displayData,
            Layer layer,
            uint weight,
            string openVpnPath,
            string serverIpAddress,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.BlockOutsideOpenVpn(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                weight,
                openVpnPath,
                serverIpAddress,
                (uint)(persistent ? 1 : 0),
                id);
        }

        public Guid PermitRouterSolicitationMessage(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.PermitRouterSolicitationMessage(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        public Guid PermitRouterAdvertisementMessage(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.PermitRouterAdvertisementMessage(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        public Guid PermitNeighborSolicitationMessage(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.PermitNeighborSolicitationMessage(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        public Guid PermitNeighborAdvertisementMessage(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.PermitNeighborAdvertisementMessage(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        public Guid PermitIcmpRedirectMessage(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.PermitIcmpRedirectMessage(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        public Guid PermitOutboundIpv6Dhcp(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.PermitOutboundIpv6Dhcp(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        public Guid PermitInboundIpv6Dhcp(
            DisplayData displayData,
            Action action,
            Layer layer,
            uint weight,
            bool persistent = false,
            Guid id = new())
        {
            return IpFilterNative.PermitInboundIpv6Dhcp(
                Session.Handle,
                ProviderId,
                Id,
                displayData,
                layer,
                action,
                weight,
                Guid.Empty,
                Guid.Empty,
                persistent,
                id);
        }

        private Session Session => _ipFilter.Session;

        private Guid ProviderId => _ipFilter.ProviderId;
    }
}
