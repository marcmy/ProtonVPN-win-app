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

    [string] $ServiceOutputDirectory = '',

    [ValidateNotNullOrEmpty()]
    [string] $ClientOutputDirectory = 'artifacts/client-build-output',

    [ValidateNotNullOrEmpty()]
    [string] $ApplicationLauncherOutputDirectory = 'artifacts/launcher-build-output',

    [ValidateNotNullOrEmpty()]
    [string] $RuntimeDependencyOutputDirectory = 'artifacts/runtime-dependency-output',

    [ValidateNotNullOrEmpty()]
    [string] $RuntimeDependencyMetadataPath = 'artifacts/runtime-dependency-metadata.json',

    [ValidateNotNullOrEmpty()]
    [string] $PatchDirectory = 'artifacts/ProtonVPN.Client.Patch',

    [ValidateNotNullOrEmpty()]
    [string] $InstallerDirectory = 'artifacts/Installer',

    [ValidateNotNullOrEmpty()]
    [string] $BasePackagerPath = (Join-Path $PSScriptRoot 'package-patch-artifacts.ps1'),

    [ValidateNotNullOrEmpty()]
    [string] $BuilderPath = (Join-Path $PSScriptRoot '..\..\scripts\New-ProtonVPNPatchSfx.ps1'),

    [ValidateNotNullOrEmpty()]
    [string] $InstallerScriptPath = (Join-Path $PSScriptRoot '..\..\scripts\Install-ProtonVPNPatch.ps1'),

    [ValidateNotNullOrEmpty()]
    [string] $InstallerLauncherPath = (Join-Path $PSScriptRoot '..\..\scripts\Install-ProtonVPNPatch.cmd')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SafeRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $normalized = $Path.Trim().Replace('/', '\')
    $segments = $normalized.Split([char[]] @('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        [System.IO.Path]::IsPathRooted($normalized) -or
        $normalized.Contains(':') -or
        $segments -contains '..') {
        throw "Unsafe complete-runtime patch path: $Path"
    }

    if ($normalized.StartsWith('ServiceData\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Complete-runtime patch must not modify ServiceData: $Path"
    }

    return $normalized
}

function Copy-ValidatedFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourcePath,

        [Parameter(Mandatory = $true)]
        [string] $RelativePath,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedHash,

        [Parameter(Mandatory = $true)]
        [string] $PatchRoot,

        [Parameter(Mandatory = $true)]
        [string] $Label
    )

    $normalizedRelativePath = Assert-SafeRelativePath -Path $RelativePath
    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "$Label source file is missing: $SourcePath"
    }

    $actualHash = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actualHash.Equals($ExpectedHash.Trim().ToLowerInvariant(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label source hash mismatch for '$RelativePath'. Expected $ExpectedHash, found $actualHash."
    }

    $targetPath = Join-Path $PatchRoot $normalizedRelativePath
    $targetParent = Split-Path -Path $targetPath -Parent
    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null

    if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
        $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $targetHash.Equals($actualHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Complete-runtime patch collision for '$RelativePath': $Label differs from the already staged payload file."
        }
        Write-Host "Verified identical $Label file already staged: $RelativePath"
        return
    }

    Copy-Item -LiteralPath $SourcePath -Destination $targetPath -Force
    Write-Host "Added $Label file: $RelativePath"
}

$basePackager = [System.IO.Path]::GetFullPath($BasePackagerPath)
$builder = [System.IO.Path]::GetFullPath($BuilderPath)
$installerScript = [System.IO.Path]::GetFullPath($InstallerScriptPath)
$installerLauncher = [System.IO.Path]::GetFullPath($InstallerLauncherPath)
$patchDir = [System.IO.Path]::GetFullPath($PatchDirectory)
$installerDir = [System.IO.Path]::GetFullPath($InstallerDirectory)
$launcherOutputDir = [System.IO.Path]::GetFullPath($ApplicationLauncherOutputDirectory)
$runtimeOutputDir = [System.IO.Path]::GetFullPath($RuntimeDependencyOutputDirectory)
$runtimeMetadataPath = [System.IO.Path]::GetFullPath($RuntimeDependencyMetadataPath)

foreach ($requiredTool in @($basePackager, $builder, $installerScript, $installerLauncher)) {
    if (-not (Test-Path -LiteralPath $requiredTool -PathType Leaf)) {
        throw "Required complete-runtime packaging tool was not found: $requiredTool"
    }
}

& $basePackager `
    -BuildMode $BuildMode `
    -TargetVersion $TargetVersion `
    -SourceCommit $SourceCommit `
    -SourceRef $SourceRef `
    -WorkflowRunId $WorkflowRunId `
    -BinDirectory $BinDirectory `
    -ServiceOutputDirectory $ServiceOutputDirectory `
    -ClientOutputDirectory $ClientOutputDirectory `
    -PatchDirectory $PatchDirectory `
    -InstallerDirectory $InstallerDirectory `
    -BuilderPath $BuilderPath `
    -InstallerScriptPath $InstallerScriptPath `
    -LauncherPath $InstallerLauncherPath

$manifestPath = Join-Path $patchDir 'patch-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Base FastPatch manifest was not created: $manifestPath"
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}
catch {
    throw "Base FastPatch manifest is not valid JSON: $($_.Exception.Message)"
}

if ([string] $manifest.targetVersion -ne $TargetVersion -or
    [string] $manifest.buildMode -ne $BuildMode -or
    [string] $manifest.sourceCommit -ne $SourceCommit) {
    throw 'Base FastPatch manifest provenance does not match the complete-runtime packaging request.'
}

$launcherIncluded = $false
if ($BuildMode -in @('client', 'both')) {
    $launcherPath = Join-Path $launcherOutputDir 'ProtonVPN.Launcher.exe'
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        throw "Complete FastPatch requires the published ProtonVPN.Launcher.exe, but it was not found: $launcherPath"
    }

    $launcherHash = (Get-FileHash -LiteralPath $launcherPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-ValidatedFile `
        -SourcePath $launcherPath `
        -RelativePath 'ProtonVPN.Launcher.exe' `
        -ExpectedHash $launcherHash `
        -PatchRoot $patchDir `
        -Label 'application launcher'
    $launcherIncluded = $true
}

if (-not (Test-Path -LiteralPath $runtimeMetadataPath -PathType Leaf)) {
    throw "Runtime dependency metadata was not produced: $runtimeMetadataPath"
}
if (-not (Test-Path -LiteralPath $runtimeOutputDir -PathType Container)) {
    throw "Runtime dependency stage directory was not produced: $runtimeOutputDir"
}

try {
    $runtimeMetadata = Get-Content -LiteralPath $runtimeMetadataPath -Raw | ConvertFrom-Json
}
catch {
    throw "Runtime dependency metadata is not valid JSON: $runtimeMetadataPath. $($_.Exception.Message)"
}

if ([int] $runtimeMetadata.schemaVersion -ne 1) {
    throw "Unsupported runtime dependency metadata schema: $($runtimeMetadata.schemaVersion)"
}
if ([string] $runtimeMetadata.buildMode -ne $BuildMode) {
    throw "Runtime dependency metadata build mode '$($runtimeMetadata.buildMode)' does not match '$BuildMode'."
}
if ([string]::IsNullOrWhiteSpace([string] $runtimeMetadata.upstreamBaseCommit)) {
    throw 'Runtime dependency metadata does not record the upstream base commit.'
}

$forbiddenRuntimeNames = @(
    'ProtonVPN.Client.exe',
    'ProtonVPNService.exe',
    'ProtonVPN.Launcher.exe',
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll'
)

$declaredRuntimePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in @($runtimeMetadata.files)) {
    $relativePath = [string] $file.path
    $normalizedRelativePath = Assert-SafeRelativePath -Path $relativePath
    $leaf = [System.IO.Path]::GetFileName($normalizedRelativePath)
    if ($forbiddenRuntimeNames -contains $leaf -or
        $leaf -like 'ProtonVPN*.dll' -or
        $leaf -like '*Tests*.dll' -or
        $leaf -like '*Test*.dll') {
        throw "Runtime dependency metadata declares forbidden file '$relativePath'."
    }
    if (-not $declaredRuntimePaths.Add($normalizedRelativePath)) {
        throw "Runtime dependency metadata declares '$relativePath' more than once."
    }

    $sourcePath = Join-Path $runtimeOutputDir $normalizedRelativePath
    Copy-ValidatedFile `
        -SourcePath $sourcePath `
        -RelativePath $normalizedRelativePath `
        -ExpectedHash ([string] $file.sha256) `
        -PatchRoot $patchDir `
        -Label 'runtime dependency'
}

$actualRuntimeFiles = @(Get-ChildItem -LiteralPath $runtimeOutputDir -Recurse -File)
if ($actualRuntimeFiles.Count -ne $declaredRuntimePaths.Count) {
    throw "Runtime dependency stage file count differs from metadata. Declared $($declaredRuntimePaths.Count), found $($actualRuntimeFiles.Count)."
}
foreach ($actualFile in $actualRuntimeFiles) {
    $relativePath = [System.IO.Path]::GetRelativePath($runtimeOutputDir, $actualFile.FullName)
    if (-not $declaredRuntimePaths.Contains($relativePath)) {
        throw "Runtime dependency stage contains undeclared file: $relativePath"
    }
}

$payloadFiles = @(Get-ChildItem -LiteralPath $patchDir -Recurse -File | Where-Object {
    -not $_.FullName.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase)
})

$manifestFiles = @(
    foreach ($file in $payloadFiles | Sort-Object FullName) {
        [ordered]@{
            path = [System.IO.Path]::GetRelativePath($patchDir, $file.FullName).Replace('\', '/')
            size = $file.Length
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)

$completeManifest = [ordered]@{}
foreach ($property in $manifest.PSObject.Properties) {
    if ($property.Name -ne 'files') {
        $completeManifest[$property.Name] = $property.Value
    }
}
$completeManifest['completeRuntimeCoverage'] = $true
$completeManifest['launcherIncluded'] = $launcherIncluded
$completeManifest['upstreamBaseCommit'] = [string] $runtimeMetadata.upstreamBaseCommit
$completeManifest['changedDirectPackages'] = @($runtimeMetadata.changedDirectPackages)
$completeManifest['runtimeDependencyPackageCount'] = @($runtimeMetadata.runtimePackages).Count
$completeManifest['runtimeDependencyFileCount'] = @($runtimeMetadata.files).Count
$completeManifest['runtimeDependencyPackages'] = @($runtimeMetadata.runtimePackages)
$completeManifest['files'] = $manifestFiles

$completeManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Complete FastPatch manifest now contains $($manifestFiles.Count) payload files."
Write-Host "Launcher included: $launcherIncluded"
Write-Host "Runtime dependency files: $(@($runtimeMetadata.files).Count)"

$installerName = "ProtonVPN-Custom-Patch-$TargetVersion.exe"
$installerPath = Join-Path $installerDir $installerName
Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue

& $builder `
    -PatchPath $patchDir `
    -OutputPath $installerPath `
    -InstallerScriptPath $installerScript `
    -LauncherPath $installerLauncher

if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Complete FastPatch installer was not rebuilt: $installerPath"
}
$installer = Get-Item -LiteralPath $installerPath
if ($installer.Length -le 0) {
    throw "Complete FastPatch installer is empty: $installerPath"
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    "patch_directory=$patchDir" >> $env:GITHUB_OUTPUT
    "installer_directory=$installerDir" >> $env:GITHUB_OUTPUT
    "installer_name=$installerName" >> $env:GITHUB_OUTPUT
    "installer_path=$installerPath" >> $env:GITHUB_OUTPUT
}
