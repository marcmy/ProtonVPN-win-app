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
    [string] $PatchDirectory = 'artifacts/ProtonVPN.Client.Patch',

    [ValidateNotNullOrEmpty()]
    [string] $InstallerDirectory = 'artifacts/Installer',

    [ValidateNotNullOrEmpty()]
    [string] $BuilderPath = (Join-Path $PSScriptRoot '..\..\scripts\New-ProtonVPNPatchSfx.ps1'),

    [ValidateNotNullOrEmpty()]
    [string] $InstallerScriptPath = (Join-Path $PSScriptRoot '..\..\scripts\Install-ProtonVPNPatch.ps1'),

    [ValidateNotNullOrEmpty()]
    [string] $LauncherPath = (Join-Path $PSScriptRoot '..\..\scripts\Install-ProtonVPNPatch.cmd')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-PathIntersection {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FirstPath,

        [Parameter(Mandatory = $true)]
        [string] $SecondPath
    )

    $first = [System.IO.Path]::GetFullPath($FirstPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $second = [System.IO.Path]::GetFullPath($SecondPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $separator = [System.IO.Path]::DirectorySeparatorChar

    return $first.Equals($second, [System.StringComparison]::OrdinalIgnoreCase) -or
        $first.StartsWith("$second$separator", [System.StringComparison]::OrdinalIgnoreCase) -or
        $second.StartsWith("$first$separator", [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-SafeCleanDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Label,

        [string[]] $ProtectedPaths = @()
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)

    if ($fullPath.Equals($pathRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label must not be a filesystem root: $fullPath"
    }

    $allowedRoots = @(
        [System.Environment]::CurrentDirectory,
        [System.IO.Path]::GetTempPath(),
        $env:GITHUB_WORKSPACE,
        $env:RUNNER_TEMP
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        [System.IO.Path]::GetFullPath($_).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    } | Sort-Object -Unique

    $isAllowed = $false
    foreach ($allowedRoot in $allowedRoots) {
        $allowedPrefix = "$allowedRoot$([System.IO.Path]::DirectorySeparatorChar)"
        if ($fullPath.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $isAllowed = $true
            break
        }
    }

    if (-not $isAllowed) {
        throw "$Label must be below the repository workspace or the runner temporary directory: $fullPath"
    }

    foreach ($protectedPath in $ProtectedPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-PathIntersection -FirstPath $fullPath -SecondPath $protectedPath) {
            throw "$Label must not overlap protected input path '$protectedPath': $fullPath"
        }
    }

    return $fullPath
}

$binDir = [System.IO.Path]::GetFullPath($BinDirectory)
$serviceOutputDir = if ([string]::IsNullOrWhiteSpace($ServiceOutputDirectory)) {
    Join-Path $binDir 'win-x64'
} else {
    [System.IO.Path]::GetFullPath($ServiceOutputDirectory)
}
$clientOutputDir = [System.IO.Path]::GetFullPath($ClientOutputDirectory)
$resolvedBuilderPath = [System.IO.Path]::GetFullPath($BuilderPath)
$resolvedInstallerScriptPath = [System.IO.Path]::GetFullPath($InstallerScriptPath)
$resolvedLauncherPath = [System.IO.Path]::GetFullPath($LauncherPath)
$protectedInputPaths = @(
    $binDir,
    $serviceOutputDir,
    $clientOutputDir,
    $resolvedBuilderPath,
    $resolvedInstallerScriptPath,
    $resolvedLauncherPath
)
$patchDir = Resolve-SafeCleanDirectory `
    -Path $PatchDirectory `
    -Label 'Patch output directory' `
    -ProtectedPaths $protectedInputPaths
$installerDir = Resolve-SafeCleanDirectory `
    -Path $InstallerDirectory `
    -Label 'Installer output directory' `
    -ProtectedPaths $protectedInputPaths

if (Test-PathIntersection -FirstPath $patchDir -SecondPath $installerDir) {
    throw "Patch and installer output directories must not overlap: '$patchDir' and '$installerDir'."
}

$manifestPath = Join-Path $patchDir 'patch-manifest.json'
$installerName = "ProtonVPN-Custom-Patch-$TargetVersion.exe"
$installerPath = Join-Path $installerDir $installerName

foreach ($requiredTool in @($resolvedBuilderPath, $resolvedInstallerScriptPath, $resolvedLauncherPath)) {
    if (-not (Test-Path -LiteralPath $requiredTool -PathType Leaf)) {
        throw "Required installer tool was not found: $requiredTool"
    }
}

Remove-Item -LiteralPath $patchDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $installerDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $patchDir | Out-Null
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null

function Copy-ToPatch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourcePath,

        [string] $RelativePath = '',

        [string] $SourceLabel = 'build output'
    )

    if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
        throw "Required patch file missing: $SourcePath"
    }

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        $RelativePath = [System.IO.Path]::GetFileName($SourcePath)
    }

    $normalizedRelativePath = $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $pathSegments = $normalizedRelativePath.Split(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.StringSplitOptions]::RemoveEmptyEntries)
    if ([System.IO.Path]::IsPathRooted($normalizedRelativePath) -or $pathSegments -contains '..') {
        throw "Unsafe relative patch path '$RelativePath' from $SourceLabel."
    }

    $targetPath = Join-Path $patchDir $normalizedRelativePath
    $targetParent = Split-Path -Path $targetPath -Parent
    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null

    if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
        $sourceHash = (Get-FileHash -LiteralPath $SourcePath -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            throw "Patch payload collision for '$RelativePath': $SourceLabel differs from the file already staged (source SHA-256 $sourceHash; staged SHA-256 $targetHash)."
        }

        Write-Host "Verified identical $SourceLabel file: $RelativePath"
        return
    }

    Copy-Item -LiteralPath $SourcePath -Destination $targetPath -Force
    Write-Host "Copied $SourceLabel file: $RelativePath"
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

    Copy-ToPatch -SourcePath $sourcePath -RelativePath $RelativePath -SourceLabel 'client'
}

function Get-FirstPartyServiceRuntimeAssets {
    param(
        [Parameter(Mandatory = $true)]
        [string] $DependenciesPath
    )

    try {
        $dependencies = Get-Content -LiteralPath $DependenciesPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Service dependency manifest is not valid JSON: $DependenciesPath. $($_.Exception.Message)"
    }

    $runtimeTargetName = [string] $dependencies.runtimeTarget.name
    if ([string]::IsNullOrWhiteSpace($runtimeTargetName)) {
        throw "Service dependency manifest does not declare runtimeTarget.name: $DependenciesPath"
    }

    $targetProperty = $dependencies.targets.PSObject.Properties[$runtimeTargetName]
    if ($null -eq $targetProperty) {
        throw "Service dependency manifest does not contain runtime target '$runtimeTargetName': $DependenciesPath"
    }

    $runtimeAssets = @(
        foreach ($library in $targetProperty.Value.PSObject.Properties) {
            $runtimeProperty = $library.Value.PSObject.Properties['runtime']
            if ($null -eq $runtimeProperty) {
                continue
            }

            foreach ($asset in $runtimeProperty.Value.PSObject.Properties) {
                if ([System.IO.Path]::GetFileName($asset.Name) -like 'ProtonVPN*.dll') {
                    $asset.Name.Replace('\', '/')
                }
            }
        }
    ) | Sort-Object -Unique

    if ($runtimeAssets.Count -eq 0) {
        throw "No first-party ProtonVPN runtime assemblies were found in $DependenciesPath"
    }

    return $runtimeAssets
}

$clientDlls = @()
$serviceRuntimeAssets = @()

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
        Copy-ToPatch -SourcePath $dll.FullName -SourceLabel 'client'
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
    if (-not (Test-Path -LiteralPath $serviceOutputDir -PathType Container)) {
        throw "Service build output directory missing: $serviceOutputDir"
    }

    $serviceDependenciesPath = Join-Path $serviceOutputDir 'ProtonVPNService.deps.json'
    if (-not (Test-Path -LiteralPath $serviceDependenciesPath -PathType Leaf)) {
        throw "Service build output missing ProtonVPNService.deps.json in $serviceOutputDir"
    }

    $serviceRuntimeAssets = @(Get-FirstPartyServiceRuntimeAssets -DependenciesPath $serviceDependenciesPath)
    foreach ($relativePath in $serviceRuntimeAssets) {
        $normalizedRelativePath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $sourcePath = Join-Path $serviceOutputDir $normalizedRelativePath
        Copy-ToPatch -SourcePath $sourcePath -RelativePath $relativePath -SourceLabel 'service'
    }

    $criticalServiceAssemblies = @(
        'ProtonVPNService.dll',
        'ProtonVPN.Vpn.dll',
        'ProtonVPN.Update.dll',
        'ProtonVPN.ProcessCommunication.Service.dll',
        'ProtonVPN.ProTun.dll',
        'ProtonVPN.Native.dll',
        'ProtonVPN.NetworkFilter.dll'
    )

    foreach ($assemblyName in $criticalServiceAssemblies) {
        if (-not (Test-Path -LiteralPath (Join-Path $patchDir $assemblyName) -PathType Leaf)) {
            throw "Service patch payload is incomplete: required assembly '$assemblyName' was not declared and copied from ProtonVPNService.deps.json."
        }
    }
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
    clientAssemblyCount = $clientDlls.Count
    serviceRuntimeAssemblyCount = $serviceRuntimeAssets.Count
    files = $manifestFiles
}

$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Created patch manifest for Proton VPN $TargetVersion with $($manifestFiles.Count) payload files."

& $resolvedBuilderPath `
    -PatchPath $patchDir `
    -OutputPath $installerPath `
    -InstallerScriptPath $resolvedInstallerScriptPath `
    -LauncherPath $resolvedLauncherPath

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
