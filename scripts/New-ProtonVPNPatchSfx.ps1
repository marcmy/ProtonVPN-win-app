[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Alias('PatchDirectory')]
    [ValidateNotNullOrEmpty()]
    [string] $PatchPath,

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

function Get-PatchManifestJson {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedPatchPath,

        [Parameter(Mandatory = $true)]
        [bool] $IsPatchZip
    )

    if (-not $IsPatchZip) {
        $manifestFiles = @(
            Get-ChildItem -LiteralPath $ResolvedPatchPath -Recurse -File -Filter 'patch-manifest.json'
        )

        if ($manifestFiles.Count -eq 0) {
            throw "Patch manifest was not found below: $ResolvedPatchPath"
        }

        if ($manifestFiles.Count -ne 1) {
            $paths = ($manifestFiles | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
            throw "Patch payload contains multiple patch-manifest.json files:$([Environment]::NewLine)$paths"
        }

        return Get-Content -LiteralPath $manifestFiles[0].FullName -Raw
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ResolvedPatchPath)
    try {
        $manifestEntries = @(
            $archive.Entries | Where-Object {
                $normalizedName = $_.FullName.Replace('\', '/').Trim('/')
                $normalizedName -eq 'patch-manifest.json' -or
                    $normalizedName.EndsWith('/patch-manifest.json', [StringComparison]::OrdinalIgnoreCase)
            }
        )

        if ($manifestEntries.Count -eq 0) {
            throw "Patch ZIP does not contain patch-manifest.json: $ResolvedPatchPath"
        }

        if ($manifestEntries.Count -ne 1) {
            $paths = ($manifestEntries | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
            throw "Patch ZIP contains multiple patch-manifest.json entries:$([Environment]::NewLine)$paths"
        }

        $stream = $manifestEntries[0].Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
            try {
                return $reader.ReadToEnd()
            } finally {
                $reader.Dispose()
            }
        } finally {
            $stream.Dispose()
        }
    } finally {
        $archive.Dispose()
    }
}

if ($env:OS -ne 'Windows_NT') {
    throw 'The self-extractor builder requires Windows because it uses IExpress.'
}

$resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path
$resolvedInstallerScriptPath = (Resolve-Path -LiteralPath $InstallerScriptPath -ErrorAction Stop).Path
$resolvedLauncherPath = (Resolve-Path -LiteralPath $LauncherPath -ErrorAction Stop).Path
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Path $resolvedOutputPath -Parent

if ([System.IO.Path]::GetExtension($resolvedOutputPath) -ne '.exe') {
    throw "OutputPath must end in .exe: $resolvedOutputPath"
}

$isPatchZip = Test-Path -LiteralPath $resolvedPatchPath -PathType Leaf
$isPatchDirectory = Test-Path -LiteralPath $resolvedPatchPath -PathType Container
if (-not $isPatchZip -and -not $isPatchDirectory) {
    throw "PatchPath must be a .zip archive or directory: $resolvedPatchPath"
}

if ($isPatchZip -and [System.IO.Path]::GetExtension($resolvedPatchPath) -ne '.zip') {
    throw "PatchPath must be a .zip archive or directory: $resolvedPatchPath"
}

if ($isPatchDirectory) {
    $patchFiles = @(Get-ChildItem -LiteralPath $resolvedPatchPath -Recurse -File)
    if ($patchFiles.Count -eq 0) {
        throw "Patch directory does not contain any files: $resolvedPatchPath"
    }
}

$manifestJson = Get-PatchManifestJson -ResolvedPatchPath $resolvedPatchPath -IsPatchZip $isPatchZip
try {
    $manifest = $manifestJson | ConvertFrom-Json -ErrorAction Stop
} catch {
    throw "Patch manifest is not valid JSON: $($_.Exception.Message)"
}

foreach ($requiredProperty in @('schemaVersion', 'targetVersion', 'buildMode', 'sourceCommit', 'files')) {
    if ($null -eq $manifest.PSObject.Properties[$requiredProperty]) {
        throw "Patch manifest is missing required property '$requiredProperty'."
    }
}

if ([int] $manifest.schemaVersion -ne 1) {
    throw "Unsupported patch manifest schema version: $($manifest.schemaVersion)"
}

$manifestTargetVersion = ([string] $manifest.targetVersion).Trim()
if ($manifestTargetVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Patch manifest targetVersion must be a numeric three-part release version. Received '$manifestTargetVersion'."
}

Write-Host "Packaging patch for Proton VPN $manifestTargetVersion ($($manifest.buildMode))."

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

if (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) {
    Remove-Item -LiteralPath $resolvedOutputPath -Force
}

$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNSfx-{0}" -f [Guid]::NewGuid().ToString('N'))
$payloadPath = Join-Path $workingDirectory 'payload.zip'
$installerFileName = 'Install-ProtonVPNPatch.ps1'
$launcherFileName = 'Install-ProtonVPNPatch.cmd'
$packagedInstallerScriptPath = Join-Path $workingDirectory $installerFileName
$packagedLauncherPath = Join-Path $workingDirectory $launcherFileName
$iexpressConfigPath = Join-Path $workingDirectory 'ProtonVPNPatch.sed'
$diagnosticConfigPath = [System.IO.Path]::ChangeExtension($resolvedOutputPath, '.sed')
$buildSucceeded = $false
$existingIExpressIds = @()

try {
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null

    Copy-Item -LiteralPath $resolvedInstallerScriptPath -Destination $packagedInstallerScriptPath -Force
    Copy-Item -LiteralPath $resolvedLauncherPath -Destination $packagedLauncherPath -Force

    $removedElevationTreeWait = $false
    $insertedSingleProcessWait = $false
    $installerLines = @(
        foreach ($line in Get-Content -LiteralPath $packagedInstallerScriptPath) {
            if (-not $removedElevationTreeWait -and $line.Trim() -eq '-Wait `') {
                $removedElevationTreeWait = $true
                continue
            }

            if ($removedElevationTreeWait -and -not $insertedSingleProcessWait -and $line.Trim() -eq 'exit $process.ExitCode') {
                '    $process.WaitForExit()'
                $insertedSingleProcessWait = $true
            }

            $line
        }
    )

    if (-not $removedElevationTreeWait -or -not $insertedSingleProcessWait) {
        throw 'Could not update the packaged installer elevation wait behavior.'
    }

    Set-Content -LiteralPath $packagedInstallerScriptPath -Value $installerLines -Encoding UTF8

    $payloadInvocation = '-PatchPath "%PAYLOAD%" -TargetVersion "{0}" -RestartClient -PauseBeforeExit' -f $manifestTargetVersion
    $fallbackInvocation = '-File "%SCRIPT%" -TargetVersion "{0}" -RestartClient -PauseBeforeExit' -f $manifestTargetVersion
    $launcherLines = @(
        foreach ($line in Get-Content -LiteralPath $packagedLauncherPath) {
            if ($line.Trim() -ieq 'pause') {
                continue
            }

            if ($line.Contains('-PatchPath "%PAYLOAD%"')) {
                $line = $line.Replace('-PatchPath "%PAYLOAD%"', $payloadInvocation)
            } elseif ($line.Contains('-File "%SCRIPT%"')) {
                $line = $line.Replace('-File "%SCRIPT%"', $fallbackInvocation)
            }

            $line
        }
    )
    Set-Content -LiteralPath $packagedLauncherPath -Value $launcherLines -Encoding Ascii

    if ($isPatchZip) {
        Copy-Item -LiteralPath $resolvedPatchPath -Destination $payloadPath -Force
    } else {
        Compress-Archive `
            -Path (Join-Path $resolvedPatchPath '*') `
            -DestinationPath $payloadPath `
            -CompressionLevel Optimal `
            -Force
    }

    $sourceDirectoryForSed = $workingDirectory.TrimEnd('\') + '\'
    $escapedFriendlyName = ("$FriendlyName $manifestTargetVersion").Replace('"', '')

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
TargetName="$resolvedOutputPath"
FriendlyName=$escapedFriendlyName
AppLaunched=$launcherFileName
PostInstallCmd=<None>
AdminQuietInstCmd=$launcherFileName
UserQuietInstCmd=$launcherFileName
SourceFiles=SourceFiles

[SourceFiles]
SourceFiles0="$sourceDirectoryForSed"

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

    $existingIExpressIds = @(
        Get-Process -Name 'iexpress' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Id }
    )

    Write-Host 'Starting IExpress package build...'
    & $iexpressPath /N /Q $iexpressConfigPath
    Write-Host 'IExpress invocation returned; waiting for the installer file...'

    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $lastObservedLength = -1L
    $stableLengthChecks = 0

    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) {
            $currentLength = (Get-Item -LiteralPath $resolvedOutputPath).Length
            if ($currentLength -gt 0 -and $currentLength -eq $lastObservedLength) {
                $stableLengthChecks++
            } else {
                $lastObservedLength = $currentLength
                $stableLengthChecks = 0
            }

            if ($stableLengthChecks -ge 3) {
                break
            }
        }

        Start-Sleep -Milliseconds 250
    }

    $outputExists = Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf
    $outputLength = if ($outputExists) {
        (Get-Item -LiteralPath $resolvedOutputPath).Length
    } else {
        0L
    }

    if (-not $outputExists -or $outputLength -le 0 -or $stableLengthChecks -lt 3) {
        $newIExpressProcesses = @(
            Get-Process -Name 'iexpress' -ErrorAction SilentlyContinue |
                Where-Object { $existingIExpressIds -notcontains $_.Id }
        )
        $processStatus = if ($newIExpressProcesses.Count -gt 0) {
            "$($newIExpressProcesses.Count) newly started IExpress process(es) are still running"
        } else {
            'No newly started IExpress process is still running'
        }

        throw "IExpress did not create a stable installer within 120 seconds. $processStatus. Expected output: $resolvedOutputPath"
    }

    $buildSucceeded = $true
    if (Test-Path -LiteralPath $diagnosticConfigPath -PathType Leaf) {
        Remove-Item -LiteralPath $diagnosticConfigPath -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Created self-extracting patch installer: $resolvedOutputPath" -ForegroundColor Green
} finally {
    $newIExpressProcesses = @(
        Get-Process -Name 'iexpress' -ErrorAction SilentlyContinue |
            Where-Object { $existingIExpressIds -notcontains $_.Id }
    )
    foreach ($process in $newIExpressProcesses) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    if (-not $buildSucceeded -and (Test-Path -LiteralPath $iexpressConfigPath -PathType Leaf)) {
        Copy-Item -LiteralPath $iexpressConfigPath -Destination $diagnosticConfigPath -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
