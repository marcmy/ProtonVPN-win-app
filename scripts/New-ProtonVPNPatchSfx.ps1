[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $PatchDirectory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputPath,

    [string] $InstallerScriptPath = (Join-Path $PSScriptRoot 'Install-ProtonVPNPatch.ps1'),

    [string] $LauncherPath = (Join-Path $PSScriptRoot 'Install-ProtonVPNPatch.cmd'),

    [ValidateNotNullOrEmpty()]
    [string] $FriendlyName = 'Proton VPN Custom Patch Installer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'The self-extractor builder requires Windows because it uses IExpress.'
}

$resolvedPatchDirectory = (Resolve-Path -LiteralPath $PatchDirectory -ErrorAction Stop).Path
$resolvedInstallerScriptPath = (Resolve-Path -LiteralPath $InstallerScriptPath -ErrorAction Stop).Path
$resolvedLauncherPath = (Resolve-Path -LiteralPath $LauncherPath -ErrorAction Stop).Path
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Path $resolvedOutputPath -Parent

if ([System.IO.Path]::GetExtension($resolvedOutputPath) -ne '.exe') {
    throw "OutputPath must end in .exe: $resolvedOutputPath"
}

if (-not (Test-Path -LiteralPath $resolvedPatchDirectory -PathType Container)) {
    throw "PatchDirectory is not a directory: $resolvedPatchDirectory"
}

$patchFiles = @(Get-ChildItem -LiteralPath $resolvedPatchDirectory -Recurse -File)
if ($patchFiles.Count -eq 0) {
    throw "PatchDirectory does not contain any files: $resolvedPatchDirectory"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNSfx-{0}" -f [Guid]::NewGuid().ToString('N'))
$payloadPath = Join-Path $workingDirectory 'payload.zip'
$installerFileName = 'Install-ProtonVPNPatch.ps1'
$launcherFileName = 'Install-ProtonVPNPatch.cmd'
$iexpressConfigPath = Join-Path $workingDirectory 'ProtonVPNPatch.sed'

try {
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null

    Copy-Item -LiteralPath $resolvedInstallerScriptPath -Destination (Join-Path $workingDirectory $installerFileName) -Force
    Copy-Item -LiteralPath $resolvedLauncherPath -Destination (Join-Path $workingDirectory $launcherFileName) -Force

    Compress-Archive `
        -Path (Join-Path $resolvedPatchDirectory '*') `
        -DestinationPath $payloadPath `
        -CompressionLevel Optimal `
        -Force

    $sourceDirectoryForSed = $workingDirectory.TrimEnd('\') + '\'
    $escapedFriendlyName = $FriendlyName.Replace('"', '')

    $sedContent = @"
[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$resolvedOutputPath
FriendlyName=$escapedFriendlyName
AppLaunched=$launcherFileName
PostInstallCmd=<None>
AdminQuietInstCmd=$launcherFileName
UserQuietInstCmd=$launcherFileName
SourceFiles=SourceFiles

[SourceFiles]
SourceFiles0=$sourceDirectoryForSed

[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=

[Strings]
FILE0="payload.zip"
FILE1="$installerFileName"
FILE2="$launcherFileName"
"@

    Set-Content -LiteralPath $iexpressConfigPath -Value $sedContent -Encoding Ascii

    $iexpressPath = Join-Path $env:SystemRoot 'System32\iexpress.exe'
    if (-not (Test-Path -LiteralPath $iexpressPath -PathType Leaf)) {
        throw "IExpress was not found: $iexpressPath"
    }

    & $iexpressPath /N /Q $iexpressConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "IExpress failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
        throw "IExpress completed without creating the expected output: $resolvedOutputPath"
    }

    Write-Host "Created self-extracting patch installer: $resolvedOutputPath" -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
