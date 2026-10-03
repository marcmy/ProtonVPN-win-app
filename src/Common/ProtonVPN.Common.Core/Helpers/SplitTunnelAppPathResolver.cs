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

/// <summary>Expands saved executable patterns without passing wildcards to WFP.</summary>
public static class SplitTunnelAppPathResolver
{
    public static string Normalize(string? path)
    {
        string result = path?.Trim() ?? string.Empty;
        // Windows Explorer's "Copy as path" surrounds the path with double quotes.
        return result.Length >= 2 && result[0] == '"' && result[^1] == '"'
            ? result[1..^1].Trim()
            : result;
    }

    public static string[] Resolve(IEnumerable<string>? paths)
    {
        return (paths ?? [])
            .Select(Normalize)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany<string, string>(path => HasWildcards(path)
                ? ResolvePattern(path)
                // Keep ordinary paths and package-family identifiers compatible with WFP.
                : new[] { path })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasWildcards(string path)
    {
        // The question mark in Windows' extended-length path prefix is not a wildcard.
        string candidate = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        return candidate.Contains('*') || candidate.Contains('?');
    }

    public static List<string> ResolvePattern(string path)
    {
        path = Normalize(path);
        try
        {
            if (!Path.IsPathFullyQualified(path) || !path.Contains('*') ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            string directoryPattern = Path.GetDirectoryName(path) ?? string.Empty;
            int wildcardIndex = directoryPattern.IndexOf('*');
            if (wildcardIndex < 0)
            {
                return [];
            }

            int rootEndIndex = directoryPattern.LastIndexOf(Path.DirectorySeparatorChar, wildcardIndex);
            if (rootEndIndex < 0)
            {
                return [];
            }

            // Include the separator: C:\* must search C:\, never the drive's current directory.
            string searchRoot = directoryPattern[..(rootEndIndex + 1)];
            string[] segments = directoryPattern[(rootEndIndex + 1)..]
                .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment is "." or ".."))
            {
                return [];
            }

            IEnumerable<string> directories = [searchRoot];
            foreach (string segment in segments)
            {
                // Each pattern component matches exactly one level; this is not recursive folder exclusion.
                directories = directories.SelectMany(directory => Enumerate(directory, segment, directories: true));
            }

            string fileNamePattern = Path.GetFileName(path);
            return directories.SelectMany(directory => Enumerate(directory, fileNamePattern, directories: false))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return [];
        }
    }

    private static List<string> Enumerate(string directory, string pattern, bool directories)
    {
        try
        {
            return directories
                ? Directory.EnumerateDirectories(directory, pattern, SearchOption.TopDirectoryOnly).ToList()
                : Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (IsFileSystemError(ex))
        {
            return [];
        }
    }

    private static bool IsFileSystemError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException;
}
