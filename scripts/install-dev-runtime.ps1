[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $RuntimePath,

    [ValidateNotNullOrEmpty()]
    [string] $ServiceName = 'ProtonVPN Service',

    [ValidateNotNullOrEmpty()]
    [string] $ServiceExePath,

    [ValidateNotNullOrEmpty()]
    [string] $OfficialRoot = 'C:\Program Files\Proton\VPN',

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

function Resolve-ProtonVpnServiceExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedRuntimePath,

        [string] $ExplicitServiceExePath
    )

    if ($ExplicitServiceExePath) {
        $resolvedExplicitPath = (Resolve-Path -LiteralPath $ExplicitServiceExePath -ErrorAction Stop).Path
        if ((Split-Path -Leaf $resolvedExplicitPath) -ne 'ProtonVPNService.exe') {
            throw "ServiceExePath must point to ProtonVPNService.exe: $resolvedExplicitPath"
        }

        if (-not (Test-IsPathUnderRoot -Path $resolvedExplicitPath -Root $ResolvedRuntimePath)) {
            throw "ServiceExePath must be under RuntimePath '$ResolvedRuntimePath': $resolvedExplicitPath"
        }

        return $resolvedExplicitPath
    }

    $preferredPaths = @(
        (Join-Path $ResolvedRuntimePath 'ProtonVPNService.exe'),
        (Join-Path $ResolvedRuntimePath 'win-x64\ProtonVPNService.exe')
    )

    foreach ($path in $preferredPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    $serviceExecutables = @(
        Get-ChildItem -LiteralPath $ResolvedRuntimePath -Recurse -File -Filter 'ProtonVPNService.exe' |
            Sort-Object FullName
    )

    if ($serviceExecutables.Count -eq 0) {
        throw "No ProtonVPNService.exe was found under runtime path: $ResolvedRuntimePath"
    }

    if ($serviceExecutables.Count -gt 1) {
        $paths = ($serviceExecutables | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
        throw "Multiple ProtonVPNService.exe files were found. Pass -ServiceExePath explicitly:$([Environment]::NewLine)$paths"
    }

    return $serviceExecutables[0].FullName
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

$resolvedRuntimePath = (Resolve-Path -LiteralPath $RuntimePath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedRuntimePath -PathType Container)) {
    throw "RuntimePath is not a directory: $resolvedRuntimePath"
}

$resolvedServiceExePath = Resolve-ProtonVpnServiceExecutable -ResolvedRuntimePath $resolvedRuntimePath -ExplicitServiceExePath $ServiceExePath

if (Test-Path -LiteralPath $OfficialRoot -PathType Container) {
    if (Test-IsPathUnderRoot -Path $resolvedServiceExePath -Root $OfficialRoot) {
        throw "Refusing to retarget the dev runtime to the official install folder: $resolvedServiceExePath"
    }
}

$serviceExeDirectory = Split-Path -Path $resolvedServiceExePath -Parent
$nativeDllPath = Join-Path $serviceExeDirectory 'ProtonVPN.Native.dll'
if (-not (Test-Path -LiteralPath $nativeDllPath -PathType Leaf)) {
    throw "ProtonVPN.Native.dll must be next to ProtonVPNService.exe in the dev runtime folder: $nativeDllPath"
}

$service = Get-InstalledServiceConfig -RequestedServiceName $ServiceName
$newBinaryPath = ConvertTo-ServiceBinaryPath -ExecutablePath $resolvedServiceExePath
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
    Write-Host "Service binPath is already pointed at the dev runtime: $newBinaryPath"
} else {
    if (-not $WhatIfPreference -and -not (Test-IsAdministrator)) {
        throw "Retargeting the service binPath requires an elevated PowerShell session."
    }

    if ($PSCmdlet.ShouldProcess($service.Name, "Retarget service binPath to $newBinaryPath")) {
        & sc.exe config $service.Name binPath= $newBinaryPath | Write-Host
        if ($LASTEXITCODE -ne 0) {
            throw "sc.exe config failed with exit code $LASTEXITCODE"
        }

        $plan.Changed = $true
        Write-Host "Service binPath retargeted. Restart the service manually when you are ready to run the dev runtime."
    }
}

if ($PassThru) {
    $plan
}
