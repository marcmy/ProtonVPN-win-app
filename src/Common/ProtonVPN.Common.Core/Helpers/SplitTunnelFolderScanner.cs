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

using System.IO;
using System.Security;

namespace ProtonVPN.Common.Core.Helpers;

public sealed record FolderScanResult(string[] AppPaths, string? Error);

/// <summary>Local, bounded, no-link traversal for explicitly selected exclusion folders.</summary>
public static class SplitTunnelFolderScanner
{
    public const int MaximumFolders = 20;
    public const int MaximumEntries = 10000;
    public const int MaximumDepth = 32;

    public static bool TryNormalize(string? input, out string path)
    {
        path = string.Empty;
        try
        {
            string candidate = SplitTunnelAppPathResolver.Normalize(input);
            if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
                candidate.Contains('*') || candidate.Contains('?'))
            {
                return false;
            }
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            string root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(candidate)!);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] broadRoots = [root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Path.Combine(root, "Users")];
            string usersRoot = Path.Combine(root, "Users");
            if (broadRoots.Any(value => value.Length > 0 && string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(Path.GetDirectoryName(candidate), usersRoot, StringComparison.OrdinalIgnoreCase) ||
                (windows.Length > 0 && IsWithin(windows, candidate)))
            {
                return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception ex) when (IsIoError(ex)) { return false; }
    }

    public static bool IsWithin(string root, string candidate) =>
        string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(candidate), StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static FolderScanResult Scan(string input)
    {
        if (!TryNormalize(input, out string root))
        {
            return new([], "Choose a specific local folder, not a drive, system folder, or broad profile/program root.");
        }
        try
        {
            if (!Directory.Exists(root)) { return new([], "Folder does not exist or cannot be accessed."); }
            // Reject linked ancestors too: a privileged service must not walk a redirected user path.
            for (DirectoryInfo? directory = new(root); directory != null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return new([], "Folder rules cannot traverse junctions or symbolic links.");
                }
            }
            List<string> apps = [];
            Stack<(string Path, int Depth)> pending = new();
            pending.Push((root, 0));
            int entries = 0;
            while (pending.TryPop(out var item))
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    if (++entries > MaximumEntries)
                    {
                        return new([], $"Folder exceeds the {MaximumEntries} entry scan limit. Choose a smaller folder.");
                    }
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (item.Depth >= MaximumDepth)
                        {
                            return new([], $"Folder exceeds the {MaximumDepth} level scan limit.");
                        }
                        pending.Push((entry, item.Depth + 1));
                    }
                    else if (string.Equals(Path.GetExtension(entry), ".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        apps.Add(entry);
                    }
                }
            }
            return new(apps.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), null);
        }
        catch (Exception ex) when (IsIoError(ex))
        {
            // Never present a partial scan as successful coverage.
            return new([], $"Folder unavailable or changed during its scan: {ex.Message}");
        }
    }

    private static bool IsIoError(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;
}
