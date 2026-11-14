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

namespace ProtonVPN.Client.Settings.Contracts.Models;

public readonly struct SplitTunnelingFolder(string folderPath, bool isActive) : IEquatable<SplitTunnelingFolder>
{
    public string FolderPath { get; init; } = folderPath;
    public bool IsActive { get; init; } = isActive;
    public bool Equals(SplitTunnelingFolder other) =>
        string.Equals(FolderPath, other.FolderPath, StringComparison.OrdinalIgnoreCase) && IsActive == other.IsActive;
    public override bool Equals(object? obj) => obj is SplitTunnelingFolder other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(FolderPath ?? string.Empty), IsActive);
}
