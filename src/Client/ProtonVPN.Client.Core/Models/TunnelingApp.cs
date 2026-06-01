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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using ProtonVPN.Client.Common.UI.Extensions;

namespace ProtonVPN.Client.Core.Models;

public class TunnelingApp : ExternalApp
{
    private const char DIRECTORY_SEPARATOR_CHAR = '\\';
    private const string WILDCARD_SEARCH_PATTERN = "*";

    public static TunnelingApp NotFound(string appPath, string appName, List<string>? alternateAppPaths = null) => new(appPath, appName, alternateAppPaths);

    public List<string> AlternateAppPaths { get; }

    public override bool IsValid => base.IsValid || IsAppPathPatternValid(AppPath);

    protected TunnelingApp(
        string appPath,
        string appName,
        ImageSource? appIcon,
        List<string>? alternateAppPaths)
        : base(appPath, appName, appIcon)
    {
        AlternateAppPaths = alternateAppPaths ?? [];
    }

    protected TunnelingApp(
        string appPath,
        string appName,
        List<string>? alternateAppPaths)
        : this(appPath, appName, null, alternateAppPaths)
    { }

    public static async Task<TunnelingApp?> TryCreateAsync(string appPath, List<string>? alternateAppPaths = null)
    {
        appPath = appPath?.Trim() ?? string.Empty;

        ExternalApp? externalApp = await ExternalApp.TryCreateAsync(appPath);
        if (externalApp != null)
        {
            return new TunnelingApp(externalApp.AppPath, externalApp.AppName, externalApp.AppIcon, alternateAppPaths);
        }

        if (TryResolveAppPathPattern(appPath, out string appName, out List<string> resolvedAppPaths))
        {
            ImageSource? appIcon = null;
            string? iconPath = resolvedAppPaths.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                appIcon = await iconPath.GetAppIconAsync();
            }

            return new TunnelingApp(appPath, appName, appIcon, resolvedAppPaths);
        }

        return null;
    }

    private static bool IsAppPathPatternValid(string appPath)
    {
        return TryResolveAppPathPattern(appPath, out _, out _);
    }

    private static bool TryResolveAppPathPattern(string appPath, out string appName, out List<string> resolvedAppPaths)
    {
        appName = string.Empty;
        resolvedAppPaths = [];

        try
        {
            if (string.IsNullOrWhiteSpace(appPath) || !Path.IsPathRooted(appPath) || !appPath.Contains(WILDCARD_SEARCH_PATTERN))
            {
                return false;
            }

            string? fileNamePattern = Path.GetFileName(appPath);
            if (string.IsNullOrWhiteSpace(fileNamePattern) ||
                !string.Equals(Path.GetExtension(fileNamePattern), EXE_FILE_EXTENSION, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string directoryPattern = Path.GetDirectoryName(appPath) ?? string.Empty;
            int wildcardIndex = directoryPattern.IndexOf(WILDCARD_SEARCH_PATTERN, System.StringComparison.Ordinal);
            if (wildcardIndex < 0)
            {
                return false;
            }

            int searchRootEndIndex = directoryPattern.LastIndexOf(DIRECTORY_SEPARATOR_CHAR, wildcardIndex);
            if (searchRootEndIndex < 0)
            {
                return false;
            }

            string searchRoot = directoryPattern[..searchRootEndIndex];
            string relativeDirectoryPattern = directoryPattern[(searchRootEndIndex + 1)..];

            if (!Directory.Exists(searchRoot))
            {
                return false;
            }

            resolvedAppPaths = Directory
                .EnumerateFiles(searchRoot, fileNamePattern, SearchOption.AllDirectories)
                .Where(path => IsPathMatch(path, directoryPattern, fileNamePattern))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            if (!resolvedAppPaths.Any())
            {
                return false;
            }

            appName = $"{fileNamePattern} ({relativeDirectoryPattern})";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathMatch(string path, string directoryPattern, string fileNamePattern)
    {
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        return MatchesWildcard(directory, directoryPattern)
            && MatchesWildcard(Path.GetFileName(path), fileNamePattern);
    }

    private static bool MatchesWildcard(string input, string pattern)
    {
        string regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
