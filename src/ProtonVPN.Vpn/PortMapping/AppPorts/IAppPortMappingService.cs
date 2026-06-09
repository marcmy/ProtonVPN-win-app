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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProtonVPN.Vpn.PortMapping.AppPorts;

public interface IAppPortMappingService
{
    Task<AppPortMappingResult> MapAsync(
        AppPortMappingProtocol protocol,
        ushort internalPort,
        ushort suggestedExternalPort = 0,
        uint requestedLifetimeSeconds = 7200,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppPortMappingResult>> MapTcpAndUdpAsync(
        ushort internalPort,
        ushort suggestedExternalPort = 0,
        uint requestedLifetimeSeconds = 7200,
        CancellationToken cancellationToken = default);

    Task DestroyAsync(
        AppPortMappingProtocol protocol,
        ushort internalPort,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<AppPortMappingResult> GetActiveMappings();
}
