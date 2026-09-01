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

function Test-IExpressCabinetPayload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        return [Text.Encoding]::ASCII.GetString($bytes).Contains('MSCF')
    } catch {
        return $false
    }
}


function Get-SfxLoaderScript {
    param(
        [Parameter(Mandatory = $true)] [string] $InstallerFileName,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $InstallerHash,
        [Parameter(Mandatory = $true)] [string] $PayloadFileName,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $PayloadHash,
        [Parameter(Mandatory = $true)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $TargetVersion
    )

    $template = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Decode-FastPatchValue([string] $Value) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value)) }
function Get-FastPatchBytesSha256([byte[]] $Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Test-FastPatchAdministrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
$root = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd('\', '/')
$installer = Join-Path $root (Decode-FastPatchValue '__INSTALLER__')
$payload = Join-Path $root (Decode-FastPatchValue '__PAYLOAD__')
foreach ($path in @($root, $installer, $payload)) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "IExpress FastPatch source contains a reparse point: $path"
    }
}
$source = [IO.File]::Open($installer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$memory = New-Object IO.MemoryStream
try {
    $source.CopyTo($memory)
    $bytes = $memory.ToArray()
} finally {
    $memory.Dispose()
    $source.Dispose()
}
$actualInstallerHash = Get-FastPatchBytesSha256 $bytes
if (-not $actualInstallerHash.Equals('__INSTALLER_HASH__', [StringComparison]::OrdinalIgnoreCase)) {
    throw "IExpress FastPatch installer hash mismatch. Expected __INSTALLER_HASH__, found $actualInstallerHash."
}
$scriptText = [Text.Encoding]::UTF8.GetString($bytes)
if ($scriptText.Length -gt 0 -and $scriptText[0] -eq [char]0xFEFF) { $scriptText = $scriptText.Substring(1) }
if (-not (Test-FastPatchAdministrator)) {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FastPatchConsoleWindow { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); }'
    [FastPatchConsoleWindow]::ShowWindow([FastPatchConsoleWindow]::GetConsoleWindow(), 0) | Out-Null
}
$global:ProtonVpnFastPatchVerifiedSfxScriptText = $scriptText
& ([ScriptBlock]::Create($scriptText)) `
    -PatchPath $payload `
    -TargetVersion (Decode-FastPatchValue '__TARGET__') `
    -RestartClient `
    -PauseBeforeExit `
    -ExpectedPatchArchiveSha256 '__PAYLOAD_HASH__'
'@

    $utf8 = [Text.Encoding]::UTF8
    return $template.Replace('__INSTALLER__', [Convert]::ToBase64String($utf8.GetBytes($InstallerFileName))).Replace(
        '__INSTALLER_HASH__', $InstallerHash.Trim().ToLowerInvariant()).Replace(
        '__PAYLOAD__', [Convert]::ToBase64String($utf8.GetBytes($PayloadFileName))).Replace(
        '__PAYLOAD_HASH__', $PayloadHash.Trim().ToLowerInvariant()).Replace(
        '__TARGET__', [Convert]::ToBase64String($utf8.GetBytes($TargetVersion)))
}

$windowsPowerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $windowsPowerShellPath `
    -NoProfile `
    -NonInteractive `
    -ExecutionPolicy Bypass `
    -File $resolvedInstallerScriptPath `
    -PatchPath $resolvedPatchPath `
    -ValidateOnly
if ($LASTEXITCODE -ne 0) {
    throw "Patch payload validation failed with exit code $LASTEXITCODE."
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

$schemaVersion = [int] $manifest.schemaVersion
if ($schemaVersion -notin @(1, 2)) {
    throw "Unsupported patch manifest schema version: $schemaVersion"
}
if ($schemaVersion -eq 2) {
    $coverageProperty = $manifest.PSObject.Properties['completeRuntimeCoverage']
    if ($null -eq $coverageProperty -or -not [bool] $coverageProperty.Value) {
        throw 'Schema-v2 patch manifest must declare completeRuntimeCoverage=true.'
    }
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
$payloadFileName = 'payload.zip'
$payloadPath = Join-Path $workingDirectory $payloadFileName
$installerFileName = 'Install-ProtonVPNPatch.ps1'
$packagedInstallerScriptPath = Join-Path $workingDirectory $installerFileName
$iexpressConfigPath = Join-Path $workingDirectory 'ProtonVPNPatch.sed'
$diagnosticConfigPath = [System.IO.Path]::ChangeExtension($resolvedOutputPath, '.sed')
$buildSucceeded = $false
$existingIExpressIds = @()

try {
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null

    Copy-Item -LiteralPath $resolvedInstallerScriptPath -Destination $packagedInstallerScriptPath -Force

    $removedElevationTreeWait = $false
    $insertedSingleProcessWait = $false
    $installerLines = @(
        foreach ($line in Get-Content -LiteralPath $packagedInstallerScriptPath) {
            if (-not $removedElevationTreeWait -and $line.Trim() -eq '-Wait `') {
                $removedElevationTreeWait = $true
                continue
            }

            if ($removedElevationTreeWait -and -not $insertedSingleProcessWait -and
                $line.Trim() -in @('exit $process.ExitCode', 'return $process.ExitCode')) {
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

    if ($isPatchZip) {
        Copy-Item -LiteralPath $resolvedPatchPath -Destination $payloadPath -Force
    } else {
        Compress-Archive `
            -Path (Join-Path $resolvedPatchPath '*') `
            -DestinationPath $payloadPath `
            -CompressionLevel Optimal `
            -Force
    }
    $payloadLength = (Get-Item -LiteralPath $payloadPath).Length
    $installerHash = (Get-FileHash -LiteralPath $packagedInstallerScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $loaderScript = Get-SfxLoaderScript `
        -InstallerFileName $installerFileName `
        -InstallerHash $installerHash `
        -PayloadFileName $payloadFileName `
        -PayloadHash $payloadHash `
        -TargetVersion $manifestTargetVersion
    $encodedLoader = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($loaderScript))
    # Resolve through the system object-manager root rather than a user-overridable
    # environment variable such as %SystemRoot%. GLOBALROOT is a Win32 namespace
    # alias for the true system-wide object-manager root.
    $sfxLaunchCommand = '"\\?\GLOBALROOT\SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader
    if ($sfxLaunchCommand.Length -gt 30000) {
        throw "IExpress FastPatch verifier exceeds the safe Windows command-line budget: $($sfxLaunchCommand.Length) characters."
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
AppLaunched=$sfxLaunchCommand
PostInstallCmd=<None>
AdminQuietInstCmd=$sfxLaunchCommand
UserQuietInstCmd=$sfxLaunchCommand
SourceFiles=SourceFiles

[SourceFiles]
SourceFiles0="$sourceDirectoryForSed"

[SourceFiles0]
%FILE0%=
%FILE1%=

[Strings]
FILE0="payload.zip"
FILE1="$installerFileName"
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
    $cabinetPayloadPresent = $false

    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) {
            $currentLength = (Get-Item -LiteralPath $resolvedOutputPath).Length
            if ($currentLength -gt 0 -and $currentLength -eq $lastObservedLength) {
                $stableLengthChecks++
            } else {
                $lastObservedLength = $currentLength
                $stableLengthChecks = 0
            }

            # IExpress can leave a stable WEXTRACT stub while it builds the CAB asynchronously.
            # Do not treat the output as complete until the embedded cabinet exists and the
            # IExpress process that was started for this build has exited.
            if ($stableLengthChecks -ge 3 -and $currentLength -gt $payloadLength) {
                $cabinetPayloadPresent = Test-IExpressCabinetPayload -Path $resolvedOutputPath
                $newIExpressProcesses = @(
                    Get-Process -Name 'iexpress' -ErrorAction SilentlyContinue |
                        Where-Object { $existingIExpressIds -notcontains $_.Id }
                )
                if ($cabinetPayloadPresent -and $newIExpressProcesses.Count -eq 0) {
                    break
                }
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

    if ($outputExists) {
        $cabinetPayloadPresent = Test-IExpressCabinetPayload -Path $resolvedOutputPath
    }

    if (-not $outputExists -or
        $outputLength -le $payloadLength -or
        $stableLengthChecks -lt 3 -or
        -not $cabinetPayloadPresent) {
        $newIExpressProcesses = @(
            Get-Process -Name 'iexpress' -ErrorAction SilentlyContinue |
                Where-Object { $existingIExpressIds -notcontains $_.Id }
        )
        $processStatus = if ($newIExpressProcesses.Count -gt 0) {
            "$($newIExpressProcesses.Count) newly started IExpress process(es) are still running"
        } else {
            'No newly started IExpress process is still running'
        }

        throw "IExpress did not create a complete installer containing payload.zip within 120 seconds. $processStatus. Expected output: $resolvedOutputPath"
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
