[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateNotNullOrEmpty()]
    [string] $ServiceName = 'ProtonVPN Service',

    [ValidateNotNullOrEmpty()]
    [string] $OfficialRoot = 'C:\Program Files\Proton\VPN',

    [ValidateNotNullOrEmpty()]
    [string] $OfficialServiceExePath,

    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-FullPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    return [System.IO.Path]::GetFullPath($Path)
}

function Test-IsPathUnderRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $fullPath = ConvertTo-FullPath $Path
    $fullRoot = (ConvertTo-FullPath $Root).TrimEnd('\')
    return $fullPath.StartsWith("$fullRoot\", [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-CandidateVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string] $CandidatePath,

        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $rootPath = (ConvertTo-FullPath $Root).TrimEnd('\')
    $directory = Split-Path -Path (ConvertTo-FullPath $CandidatePath) -Parent

    while ($directory.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        $leaf = Split-Path -Path $directory -Leaf
        if ($leaf -match '^v(?<version>\d+(\.\d+){1,3})$') {
            return [version] $matches['version']
        }

        if ($directory -eq $rootPath) {
            break
        }

        $directory = Split-Path -Path $directory -Parent
    }

    return [version] '0.0.0.0'
}

function Resolve-OfficialServiceExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedOfficialRoot,

        [string] $ExplicitServiceExePath
    )

    if ($ExplicitServiceExePath) {
        $resolvedExplicitPath = (Resolve-Path -LiteralPath $ExplicitServiceExePath -ErrorAction Stop).Path
        if ((Split-Path -Leaf $resolvedExplicitPath) -ne 'ProtonVPNService.exe') {
            throw "OfficialServiceExePath must point to ProtonVPNService.exe: $resolvedExplicitPath"
        }

        if (-not (Test-IsPathUnderRoot -Path $resolvedExplicitPath -Root $ResolvedOfficialRoot)) {
            throw "OfficialServiceExePath must be under OfficialRoot '$ResolvedOfficialRoot': $resolvedExplicitPath"
        }

        return $resolvedExplicitPath
    }

    $serviceExecutables = @(
        Get-ChildItem -LiteralPath $ResolvedOfficialRoot -Recurse -File -Filter 'ProtonVPNService.exe' |
            ForEach-Object {
                [pscustomobject]@{
                    Path = $_.FullName
                    Version = Get-CandidateVersion -CandidatePath $_.FullName -Root $ResolvedOfficialRoot
                    LastWriteTimeUtc = $_.LastWriteTimeUtc
                }
            } |
            Sort-Object Version, LastWriteTimeUtc, Path -Descending
    )

    if ($serviceExecutables.Count -eq 0) {
        throw "No official ProtonVPNService.exe was found under: $ResolvedOfficialRoot"
    }

    return $serviceExecutables[0].Path
}

function Get-InstalledServiceConfig {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RequestedServiceName
    )

    $escapedServiceName = $RequestedServiceName.Replace("'", "''")
    $services = @(
        Get-CimInstance -ClassName Win32_Service -Filter "Name = '$escapedServiceName'" -ErrorAction Stop
    )

    if ($services.Count -eq 0) {
        $services = @(
            Get-CimInstance -ClassName Win32_Service -Filter "DisplayName = '$escapedServiceName'" -ErrorAction Stop
        )
    }

    if ($services.Count -eq 0) {
        throw "The Proton VPN service was not found. This script only retargets an existing service binPath; it will not install the service."
    }

    if ($services.Count -gt 1) {
        throw "More than one service matched '$RequestedServiceName'. Pass the exact service name."
    }

    return $services[0]
}

function ConvertTo-ServiceBinaryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ExecutablePath
    )

    if ($ExecutablePath.Contains('"')) {
        throw "Service executable path cannot contain a quote character: $ExecutablePath"
    }

    return '"' + $ExecutablePath + '"'
}

$resolvedOfficialRoot = (Resolve-Path -LiteralPath $OfficialRoot -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedOfficialRoot -PathType Container)) {
    throw "OfficialRoot is not a directory: $resolvedOfficialRoot"
}

$resolvedServiceExePath = Resolve-OfficialServiceExecutable -ResolvedOfficialRoot $resolvedOfficialRoot -ExplicitServiceExePath $OfficialServiceExePath
$newBinaryPath = ConvertTo-ServiceBinaryPath -ExecutablePath $resolvedServiceExePath
$service = Get-InstalledServiceConfig -RequestedServiceName $ServiceName
$currentBinaryPath = [string] $service.PathName

$plan = [pscustomobject]@{
    ServiceName = [string] $service.Name
    DisplayName = [string] $service.DisplayName
    PreviousBinPath = $currentBinaryPath
    NewBinPath = $newBinaryPath
    ServiceExePath = $resolvedServiceExePath
    Command = "sc.exe config `"$($service.Name)`" binPath= $newBinaryPath"
    Changed = $false
}

if ($currentBinaryPath -eq $newBinaryPath) {
    Write-Host "Service binPath is already pointed at the official runtime: $newBinaryPath"
} else {
    if (-not $WhatIfPreference -and -not (Test-IsAdministrator)) {
        throw "Restoring the service binPath requires an elevated PowerShell session."
    }

    if ($PSCmdlet.ShouldProcess($service.Name, "Retarget service binPath to $newBinaryPath")) {
        & sc.exe config $service.Name binPath= $newBinaryPath | Write-Host
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe config failed with exit code $LASTEXITCODE"
        }

        $plan.Changed = $true
        Write-Host "Service binPath restored. Restart the service manually when you are ready to run the official runtime."
    }
}

if ($PassThru) {
    $plan
}
