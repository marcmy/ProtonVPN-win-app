[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('client', 'service', 'both')]
    [string] $BuildMode,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $TargetVersion,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $SourceCommit,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $SourceRef,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $WorkflowRunId,

    [ValidateNotNullOrEmpty()]
    [string] $BinDirectory = 'src/bin',

    [ValidateNotNullOrEmpty()]
    [string] $ClientOutputDirectory = 'artifacts/client-build-output',

    [ValidateNotNullOrEmpty()]
    [string] $PatchDirectory = 'artifacts/ProtonVPN.Client.Patch',

    [ValidateNotNullOrEmpty()]
    [string] $InstallerDirectory = 'artifacts/Installer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$binDir = [System.IO.Path]::GetFullPath($BinDirectory)
$clientOutputDir = [System.IO.Path]::GetFullPath($ClientOutputDirectory)
$patchDir = [System.IO.Path]::GetFullPath($PatchDirectory)
$installerDir = [System.IO.Path]::GetFullPath($InstallerDirectory)
$manifestPath = Join-Path $patchDir 'patch-manifest.json'
$installerName = "ProtonVPN-Custom-Patch-$TargetVersion.exe"
$installerPath = Join-Path $installerDir $installerName

if (-not (Test-Path -LiteralPath $binDir -PathType Container)) {
    throw "Build output directory missing: $binDir"
}

Remove-Item -LiteralPath $patchDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installerDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $patchDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

function Copy-ToPatchRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourcePath
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Required patch file missing: $SourcePath"
    }

    $targetPath = Join-Path $patchDir ([System.IO.Path]::GetFileName($SourcePath))
    Copy-Item -LiteralPath $SourcePath -Destination $targetPath -Force
    Write-Host "Copied $SourcePath -> $targetPath"
}

function Copy-RelativeClientFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $sourcePath = Join-Path $clientOutputDir $RelativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required client patch file missing: $RelativePath"
    }

    $targetPath = Join-Path $patchDir $RelativePath
    $targetParent = Split-Path -Path $targetPath -Parent
    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
    Write-Host "Copied $RelativePath"
}

if ($BuildMode -in @('client', 'both')) {
    if (-not (Test-Path -LiteralPath (Join-Path $clientOutputDir 'ProtonVPN.Client.dll') -PathType Leaf)) {
        throw "Staged client patch output missing ProtonVPN.Client.dll in $clientOutputDir"
    }

    $clientDlls = @(
        Get-ChildItem -LiteralPath $clientOutputDir -File -Filter 'ProtonVPN*.dll' |
            Where-Object {
                $_.Name -ne 'ProtonVPNService.dll' -and
                $_.Name -notlike 'ProtonVPN.*Tests*.dll' -and
                $_.Name -notlike 'ProtonVPN.Tests*.dll'
            } |
            Sort-Object Name
    )

    if ($clientDlls.Count -eq 0) {
        throw "No first-party ProtonVPN*.dll files found in $clientOutputDir"
    }

    foreach ($dll in $clientDlls) {
        Copy-ToPatchRoot -SourcePath $dll.FullName
    }

    Copy-RelativeClientFile -RelativePath 'ProtonVPN.Client.pri'
    Copy-RelativeClientFile -RelativePath 'App.xbf'
    Copy-RelativeClientFile -RelativePath 'MainWindow.xbf'

    $uiDir = Join-Path $clientOutputDir 'UI'
    if (-not (Test-Path -LiteralPath $uiDir -PathType Container)) {
        throw "Client UI resource directory missing: $uiDir"
    }

    $uiXbfFiles = @(Get-ChildItem -LiteralPath $uiDir -Recurse -File -Filter '*.xbf' | Sort-Object FullName)
    if ($uiXbfFiles.Count -eq 0) {
        throw "No UI/**/*.xbf resources found in $uiDir"
    }

    foreach ($xbf in $uiXbfFiles) {
        $relativePath = [System.IO.Path]::GetRelativePath($clientOutputDir, $xbf.FullName)
        Copy-RelativeClientFile -RelativePath $relativePath
    }
}

if ($BuildMode -in @('service', 'both')) {
    $serviceCandidates = @(
        (Join-Path $binDir 'win-x64/ProtonVPNService.dll'),
        (Join-Path $binDir 'ProtonVPNService.dll')
    )

    $servicePath = $serviceCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1

    if ($null -eq $servicePath) {
        throw 'Service build output missing ProtonVPNService.dll.'
    }

    Copy-ToPatchRoot -SourcePath $servicePath
}

$patchFiles = @(Get-ChildItem -LiteralPath $patchDir -Recurse -File)
if ($patchFiles.Count -eq 0) {
    throw 'Patch artifact is empty.'
}

$forbiddenExactNames = @(
    'ProtonVPN.Client.exe',
    'ProtonVPN.Client.deps.json',
    'ProtonVPN.Client.runtimeconfig.json',
    'ProtonVPNService.exe',
    'ProtonVPNService.deps.json',
    'ProtonVPNService.runtimeconfig.json',
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll'
)

$forbiddenFiles = @(
    $patchFiles | Where-Object {
        $forbiddenExactNames -contains $_.Name -or
        $_.Name -like 'System*.dll' -or
        $_.Name -like 'Microsoft*.dll' -or
        $_.Name -like 'ProtonVPN.*Tests*.dll' -or
        $_.Name -like 'ProtonVPN.Tests*.dll'
    }
)

if ($forbiddenFiles.Count -gt 0) {
    $names = ($forbiddenFiles | ForEach-Object { [System.IO.Path]::GetRelativePath($patchDir, $_.FullName) }) -join ', '
    throw "Patch artifact contains forbidden install-unsafe files: $names"
}

$unexpectedFiles = @(
    $patchFiles | Where-Object {
        $relativePath = [System.IO.Path]::GetRelativePath($patchDir, $_.FullName)
        $isFirstPartyDll = $_.Name -like 'ProtonVPN*.dll'
        $isClientPri = $relativePath -eq 'ProtonVPN.Client.pri'
        $isRootXbf = $relativePath -in @('App.xbf', 'MainWindow.xbf')
        $isUiXbf = $relativePath -like 'UI/*.xbf' -or $relativePath -like 'UI\*.xbf'

        -not ($isFirstPartyDll -or $isClientPri -or $isRootXbf -or $isUiXbf)
    }
)

if ($unexpectedFiles.Count -gt 0) {
    $names = ($unexpectedFiles | ForEach-Object { [System.IO.Path]::GetRelativePath($patchDir, $_.FullName) }) -join ', '
    throw "Patch artifact contains unexpected files: $names"
}

$manifestFiles = @(
    foreach ($file in $patchFiles | Sort-Object FullName) {
        $relativePath = [System.IO.Path]::GetRelativePath($patchDir, $file.FullName).Replace('\', '/')
        [ordered]@{
            path = $relativePath
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)

$manifest = [ordered]@{
    schemaVersion = 1
    targetVersion = $TargetVersion
    buildMode = $BuildMode
    sourceCommit = $SourceCommit
    sourceRef = $SourceRef
    workflowRunId = $WorkflowRunId
    builtAtUtc = [DateTime]::UtcNow.ToString('o')
    files = $manifestFiles
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Created patch manifest for Proton VPN $TargetVersion with $($manifestFiles.Count) payload files."

$builderPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\scripts\New-ProtonVPNPatchSfx.ps1'))
if (-not (Test-Path -LiteralPath $builderPath -PathType Leaf)) {
    throw "Self-extracting installer builder was not found: $builderPath"
}

& $builderPath -PatchPath $patchDir -OutputPath $installerPath

if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Patch installer was not created: $installerPath"
}

$installer = Get-Item -LiteralPath $installerPath
if ($installer.Length -le 0) {
    throw "Patch installer is empty: $installerPath"
}

Write-Host "Patch artifact file count: $($patchFiles.Count + 1)"
Write-Host "Patch installer: $installerPath"
Write-Host "Patch installer size: $($installer.Length) bytes"

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "patch_directory=$patchDir" >> $env:GITHUB_OUTPUT
    "installer_directory=$installerDir" >> $env:GITHUB_OUTPUT
    "installer_name=$installerName" >> $env:GITHUB_OUTPUT
    "installer_path=$installerPath" >> $env:GITHUB_OUTPUT
}
