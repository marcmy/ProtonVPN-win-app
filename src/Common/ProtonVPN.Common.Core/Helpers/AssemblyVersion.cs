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

using System.Reflection;

namespace ProtonVPN.Common.Core.Helpers;

public static class AssemblyVersion
{
    private static readonly Lazy<string> _version = new(CreateVersion);

    private static string CreateVersion()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        AssemblyInformationalVersionAttribute? informationalVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        string? version = informationalVersion?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        return (assembly.GetName().Version ?? new()).ToString(3);
    }

    public static string Get()
    {
        return _version.Value;
    }
}
