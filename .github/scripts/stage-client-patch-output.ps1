[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $ClientOutputDirectory = 'src/bin',

    [ValidateNotNullOrEmpty()]
    [string] $StageDirectory = 'artifacts/client-build-output'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$clientOutputDir = [System.IO.Path]::GetFullPath($ClientOutputDirectory)
$stageDir = [System.IO.Path]::GetFullPath($StageDirectory)

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
