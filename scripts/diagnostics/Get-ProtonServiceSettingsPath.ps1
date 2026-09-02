# Copyright (c) 2026 Proton AG
#
# Read-only helper for locating the ServiceSettings.json used by the running
# Proton VPN Windows service. DefaultConfiguration places it beside the service
# executable under <version>\ServiceData\ServiceSettings.json.

[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Get-ExecutablePathFromCommandLine
{
    param([string]$CommandLine)

    if ([string]::IsNullOrWhiteSpace($CommandLine))
    {
        return $null
    }

    if ($CommandLine -match '^\s*"([^"]+\.exe)"')
    {
        return $Matches[1]
    }

    if ($CommandLine -match '^\s*(.+?\.exe)(?:\s|$)')
    {
        return $Matches[1]
    }

    return $null
}

function Test-ServiceSettingsBesideExecutable
{
    param([string]$ExecutablePath)

    if ([string]::IsNullOrWhiteSpace($ExecutablePath))
    {
        return $null
    }

    $directory = Split-Path -Path $ExecutablePath -Parent
    if ([string]::IsNullOrWhiteSpace($directory))
    {
        return $null
    }

    $candidate = Join-Path $directory 'ServiceData\ServiceSettings.json'
    if (Test-Path -LiteralPath $candidate -PathType Leaf)
    {
        return (Resolve-Path -LiteralPath $candidate).Path
    }

    return $null
}

# Prefer the registered service binary because it identifies the exact installed version
# even when several Proton VPN version directories remain on disk.
try
{
    $service = Get-CimInstance Win32_Service -Filter "Name='ProtonVPN Service'" -ErrorAction Stop
    if ($null -ne $service)
    {
        $serviceExecutable = Get-ExecutablePathFromCommandLine -CommandLine $service.PathName
        $path = Test-ServiceSettingsBesideExecutable -ExecutablePath $serviceExecutable
        if ($null -ne $path)
        {
            $path
            return
        }
    }
}
catch
{
    # Fall through to process/install-directory discovery.
}

# A running service process is another authoritative source for its version directory.
try
{
    $serviceProcess = Get-Process -Name 'ProtonVPNService' -ErrorAction Stop | Select-Object -First 1
    $path = Test-ServiceSettingsBesideExecutable -ExecutablePath $serviceProcess.Path
    if ($null -ne $path)
    {
        $path
        return
    }
}
catch
{
    # Access to Process.Path can require elevation; use installation discovery below.
}

# Last-resort discovery for ordinary installed builds.
$programFilesRoots = @(
    $env:ProgramW6432,
    $env:ProgramFiles
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

$candidates = @()
foreach ($programFilesRoot in $programFilesRoots)
{
    $vpnRoot = Join-Path $programFilesRoot 'Proton\VPN'
    if (Test-Path -LiteralPath $vpnRoot -PathType Container)
    {
        $candidates += @(
            Get-ChildItem -LiteralPath $vpnRoot -Directory -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $candidate = Join-Path $_.FullName 'ServiceData\ServiceSettings.json'
                    if (Test-Path -LiteralPath $candidate -PathType Leaf)
                    {
                        Get-Item -LiteralPath $candidate
                    }
                }
        )
    }
}

$bestCandidate = $candidates | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($null -ne $bestCandidate)
{
    $bestCandidate.FullName
    return
}

throw 'Could not locate ServiceSettings.json for the Proton VPN service. Pass its exact path to Capture-GuestHoleWindowsState.ps1 -ServiceSettingsPath.'
