[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $ClientOutputDirectory = 'src/bin',

    [ValidateNotNullOrEmpty()]
    [string] $StageDirectory = 'artifacts/client-build-output'
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
        [string] $ProtectedPath
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)

    if ($fullPath.Equals($pathRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Stage output directory must not be a filesystem root: $fullPath"
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
        throw "Stage output directory must be below the repository workspace or the runner temporary directory: $fullPath"
    }

    if (Test-PathIntersection -FirstPath $fullPath -SecondPath $ProtectedPath) {
        throw "Stage output directory must not overlap client build output '$ProtectedPath': $fullPath"
    }

    return $fullPath
}

$clientOutputDir = [System.IO.Path]::GetFullPath($ClientOutputDirectory)
$stageDir = Resolve-SafeCleanDirectory -Path $StageDirectory -ProtectedPath $clientOutputDir

if (-not (Test-Path -LiteralPath (Join-Path $clientOutputDir 'ProtonVPN.Client.dll') -PathType Leaf)) {
    throw "Client build output missing ProtonVPN.Client.dll in $clientOutputDir"
}

Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

function Copy-ToStageRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourcePath
    )

    $targetPath = Join-Path $stageDir ([System.IO.Path]::GetFileName($SourcePath))
    Copy-Item -LiteralPath $SourcePath -Destination $targetPath -Force
    Write-Host "Staged $SourcePath -> $targetPath"
}

function Copy-RelativeClientFileToStage {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    $sourcePath = Join-Path $clientOutputDir $RelativePath
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required client patch file missing: $RelativePath"
    }

    $targetPath = Join-Path $stageDir $RelativePath
    $targetParent = Split-Path -Path $targetPath -Parent
    New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
    Write-Host "Staged $RelativePath"
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
    Copy-ToStageRoot -SourcePath $dll.FullName
}

Copy-RelativeClientFileToStage -RelativePath 'ProtonVPN.Client.pri'
Copy-RelativeClientFileToStage -RelativePath 'App.xbf'
Copy-RelativeClientFileToStage -RelativePath 'MainWindow.xbf'

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
    Copy-RelativeClientFileToStage -RelativePath $relativePath
}

Write-Host "Staged $($clientDlls.Count) client DLLs and $($uiXbfFiles.Count) UI XBF resources."
