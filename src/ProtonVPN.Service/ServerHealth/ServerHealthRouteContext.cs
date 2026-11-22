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
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ProtonVPN.OperatingSystems.Network.Contracts.Routing;
using Vanara.PInvoke;
using static Vanara.PInvoke.IpHlpApi;
using static Vanara.PInvoke.Ws2_32;

namespace ProtonVPN.Service.ServerHealth;

internal static class ServerHealthRouteContext
{
    public static string? Get(RouteConfiguration route)
    {
        if (route.Gateway is not { } gateway)
        {
            return null;
        }

        SOCKADDR_INET destination = (SOCKADDR_INET)new IPEndPoint(route.Destination.Ip, 0);
        if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, in destination, 0, out MIB_IPFORWARD_ROW2 bestRoute,
                out SOCKADDR_INET source).Failed ||
            bestRoute.InterfaceIndex != route.InterfaceIndex ||
            bestRoute.DestinationPrefix.PrefixLength != 32 ||
            !bestRoute.DestinationPrefix.Prefix.Ipv4.sin_addr.Equals(destination.Ipv4.sin_addr) ||
            !bestRoute.NextHop.Ipv4.sin_addr.Equals(new IN_ADDR(gateway.Ip.GetAddressBytes())) ||
            source.si_family != ADDRESS_FAMILY.AF_INET ||
            source.Ipv4.sin_addr.Equals(default(IN_ADDR)))
        {
            return null;
        }

        string identity = FormattableString.Invariant(
            $"{bestRoute.InterfaceIndex}|{bestRoute.InterfaceLuid.IfType}|{bestRoute.InterfaceLuid.NetLuidIndex}|{gateway.Ip}|{source.Ipv4.sin_addr}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
