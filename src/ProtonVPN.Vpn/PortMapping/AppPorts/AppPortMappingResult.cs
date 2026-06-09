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

namespace ProtonVPN.Vpn.PortMapping.AppPorts;

public sealed class AppPortMappingResult
{
    public AppPortMappingProtocol Protocol { get; init; }
    public ushort InternalPort { get; init; }
    public ushort ExternalPort { get; init; }
    public uint LifetimeSeconds { get; init; }
    public DateTime ExpirationDateUtc { get; init; }
    public ushort ResultCode { get; init; }
    public uint StartOfEpochSeconds { get; init; }

    public bool IsSuccess => ResultCode == 0;

    public override string ToString()
    {
        return $"{Protocol} {InternalPort}->{ExternalPort} lifetime={LifetimeSeconds}s result={ResultCode}";
    }
}
