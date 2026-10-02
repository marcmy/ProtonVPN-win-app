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
using System.IO.Enumeration;
using System.Security;

namespace ProtonVPN.Common.Core.Helpers;

public sealed record FolderScanResult(string[] AppPaths, string? Error);

/// <summary>Local, bounded, no-link traversal for explicit and wildcard folder rules in either mode.</summary>
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
                candidate.Contains("**", StringComparison.Ordinal))
            {
                return false;
            }
            candidate = Path.TrimEndingDirectorySeparator(candidate.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
            int wildcard = candidate.IndexOfAny(['*', '?']);
            if (wildcard >= 0)
            {
                int separator = candidate.LastIndexOf(Path.DirectorySeparatorChar, wildcard);
                string anchor = candidate[..(separator + 1)];
                string[] segments = candidate[(separator + 1)..].Split(Path.DirectorySeparatorChar);
                char[] invalid = Path.GetInvalidFileNameChars().Where(character => character is not '*' and not '?').ToArray();
                if (segments.Length > MaximumDepth || segments.Any(segment => segment is "" or "." or ".." || segment.IndexOfAny(invalid) >= 0) ||
                    !TryNormalize(anchor, out string normalizedAnchor)) { return false; }
                path = Path.Combine(normalizedAnchor, Path.Combine(segments));
                return true;
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
        int entries = 0;
        if (root.IndexOfAny(['*', '?']) >= 0) { return ScanPattern(root, ref entries); }
        return ScanDirectory(root, ref entries);
    }

    // Watch the fixed prefix, not only currently matching roots, to discover new versions.
    public static string GetWatchRoot(string rule)
    {
        if (!TryNormalize(rule, out string normalized)) { return string.Empty; }
        int wildcard = normalized.IndexOfAny(['*', '?']);
        return wildcard < 0 ? normalized : Path.TrimEndingDirectorySeparator(
            normalized[..(normalized.LastIndexOf(Path.DirectorySeparatorChar, wildcard) + 1)]);
    }

    private static FolderScanResult ScanPattern(string pattern, ref int entries)
    {
        try
        {
            string anchor = GetWatchRoot(pattern);
            ValidateNoLinks(anchor);
            List<string> directories = [anchor];
            string[] segments = pattern[(anchor.Length + 1)..].Split(Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                List<string> matches = [];
                foreach (string directory in directories)
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (++entries > MaximumEntries) { return new([], "Folder pattern exceeds the entry scan limit. Choose a narrower pattern."); }
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) { continue; }
                        if (FileSystemName.MatchesSimpleExpression(segment, Path.GetFileName(entry), ignoreCase: true)) { matches.Add(entry); }
                    }
                }
                directories = matches;
            }
            List<string> apps = [];
            foreach (string directory in directories)
            {
                FolderScanResult scan = ScanDirectory(directory, ref entries);
                if (scan.Error != null) { return new([], scan.Error); }
                apps.AddRange(scan.AppPaths);
            }
            // A valid pattern with no matches remains saved and watched for future installations.
            return new(apps.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(), null);
        }
        catch (Exception ex) when (IsIoError(ex)) { return new([], $"Folder pattern unavailable or changed during its scan: {ex.Message}"); }
    }

    private static void ValidateNoLinks(string root)
    {
        for (DirectoryInfo? directory = new(root); directory != null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Folder rules cannot traverse junctions or symbolic links.");
            }
        }
    }

    private static FolderScanResult ScanDirectory(string root, ref int entries)
    {
        if (!TryNormalize(root, out root)) { return new([], "A matched folder is a protected or broad root."); }
        try
        {
            if (!Directory.Exists(root)) { return new([], "Folder does not exist or cannot be accessed."); }
            // Reject linked ancestors too: a privileged service must not walk a redirected user path.
            ValidateNoLinks(root);
            List<string> apps = [];
            Stack<(string Path, int Depth)> pending = new();
            pending.Push((root, 0));
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
