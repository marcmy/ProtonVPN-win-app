[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $PatchPath,

    [ValidateNotNullOrEmpty()]
    [string] $InstallRoot = 'C:\Program Files\Proton\VPN',

    [string] $TargetVersion,

    [ValidateNotNullOrEmpty()]
    [string] $BackupRoot = "$env:ProgramData\ProtonVPN Custom Patch\Backups",

    [switch] $NoRestart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-QuotedProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    if ($Value.Contains('"')) {
        throw "Arguments containing quote characters are not supported: $Value"
    }

    return '"' + $Value + '"'
}

function Restart-Elevated {
    $argumentList = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-QuotedProcessArgument -Value $PSCommandPath)
    )

    if (-not [string]::IsNullOrWhiteSpace($PatchPath)) {
        $argumentList += '-PatchPath'
        $argumentList += ConvertTo-QuotedProcessArgument -Value $PatchPath
    }

    $argumentList += '-InstallRoot'
    $argumentList += ConvertTo-QuotedProcessArgument -Value $InstallRoot

    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $argumentList += '-TargetVersion'
        $argumentList += ConvertTo-QuotedProcessArgument -Value $TargetVersion
    }

    $argumentList += '-BackupRoot'
    $argumentList += ConvertTo-QuotedProcessArgument -Value $BackupRoot

    if ($NoRestart) {
        $argumentList += '-NoRestart'
    }

    if ($WhatIfPreference) {
        $argumentList += '-WhatIf'
    }

    $process = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList ($argumentList -join ' ') `
        -Verb RunAs `
        -Wait `
        -PassThru

    exit $process.ExitCode
}

function Get-VersionSortValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.DirectoryInfo] $Directory
    )

    $versionText = $Directory.Name.TrimStart('v', 'V')
    $parsedVersion = New-Object Version(0, 0)
    if ([Version]::TryParse($versionText, [ref] $parsedVersion)) {
        return $parsedVersion
    }

    return New-Object Version(0, 0)
}

function Resolve-TargetDirectory {
    if (-not (Test-Path -LiteralPath $InstallRoot -PathType Container)) {
        throw "Proton VPN install root was not found: $InstallRoot"
    }

    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $normalizedVersion = if ($TargetVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
            $TargetVersion
        } else {
            "v$TargetVersion"
        }

        $explicitTarget = Join-Path $InstallRoot $normalizedVersion
        if (-not (Test-Path -LiteralPath $explicitTarget -PathType Container)) {
            throw "Requested Proton VPN version folder was not found: $explicitTarget"
        }

        return (Resolve-Path -LiteralPath $explicitTarget).Path
    }

    $versionDirectories = @(
        Get-ChildItem -LiteralPath $InstallRoot -Directory -Filter 'v*' |
            Sort-Object @{ Expression = { Get-VersionSortValue -Directory $_ }; Descending = $true },
                        @{ Expression = { $_.LastWriteTimeUtc }; Descending = $true }
    )

    if ($versionDirectories.Count -eq 0) {
        throw "No Proton VPN version folders were found below: $InstallRoot"
    }

    return $versionDirectories[0].FullName
}

function Resolve-PatchSource {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory
    )

    if ([string]::IsNullOrWhiteSpace($PatchPath)) {
        $defaultPayloadZip = Join-Path $PSScriptRoot 'payload.zip'
        if (Test-Path -LiteralPath $defaultPayloadZip -PathType Leaf) {
            $script:PatchPath = $defaultPayloadZip
        } else {
            $script:PatchPath = $PSScriptRoot
        }
    }

    $resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path
    if (Test-Path -LiteralPath $resolvedPatchPath -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($resolvedPatchPath) -ne '.zip') {
            throw "PatchPath must be a directory or a .zip archive: $resolvedPatchPath"
        }

        $expandedPath = Join-Path $WorkingDirectory 'ExpandedPatch'
        New-Item -ItemType Directory -Path $expandedPath -Force | Out-Null
        Expand-Archive -LiteralPath $resolvedPatchPath -DestinationPath $expandedPath -Force
        return Resolve-PayloadRoot -Root $expandedPath
    }

    if (-not (Test-Path -LiteralPath $resolvedPatchPath -PathType Container)) {
        throw "PatchPath does not exist: $resolvedPatchPath"
    }

    return Resolve-PayloadRoot -Root $resolvedPatchPath
}

function Resolve-PayloadRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $markerNames = @(
        'ProtonVPN.Client.dll',
        'ProtonVPNService.dll',
        'ProtonVPN.Client.pri',
        'App.xbf'
    )

    foreach ($markerName in $markerNames) {
        if (Test-Path -LiteralPath (Join-Path $Root $markerName) -PathType Leaf) {
            return (Resolve-Path -LiteralPath $Root).Path
        }
    }

    $topLevelDirectories = @(Get-ChildItem -LiteralPath $Root -Directory)
    $topLevelFiles = @(Get-ChildItem -LiteralPath $Root -File)
    if ($topLevelDirectories.Count -eq 1 -and $topLevelFiles.Count -eq 0) {
        return Resolve-PayloadRoot -Root $topLevelDirectories[0].FullName
    }

    $recursiveMarkers = @(
        foreach ($markerName in $markerNames) {
            Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $markerName -ErrorAction SilentlyContinue
        }
    )

    if ($recursiveMarkers.Count -eq 0) {
        throw "The patch does not contain any expected Proton VPN payload files."
    }

    $candidateRoots = @(
        $recursiveMarkers |
            ForEach-Object { $_.Directory.FullName } |
            Sort-Object -Unique
    )

    if ($candidateRoots.Count -ne 1) {
        $candidateText = $candidateRoots -join [Environment]::NewLine
        throw "The patch payload root is ambiguous. Candidate folders:$([Environment]::NewLine)$candidateText"
    }

    return $candidateRoots[0]
}

function Invoke-Robocopy {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination,

        [switch] $Mirror,

        [string[]] $ExcludedFiles = @()
    )

    $arguments = @(
        $Source,
        $Destination,
        $(if ($Mirror) { '/MIR' } else { '/E' }),
        '/COPY:DAT',
        '/DCOPY:DAT',
        '/R:2',
        '/W:1',
        '/XJ',
        '/NFL',
        '/NDL',
        '/NP'
    )

    if ($ExcludedFiles.Count -gt 0) {
        $arguments += '/XF'
        $arguments += $ExcludedFiles
    }

    & robocopy.exe @arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -gt 7) {
        throw "robocopy failed with exit code $exitCode while copying '$Source' to '$Destination'."
    }
}

function Get-ProtonServicesForTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetDirectory
    )

    $escapedTarget = [Regex]::Escape($TargetDirectory)
    return @(
        Get-CimInstance -ClassName Win32_Service |
            Where-Object {
                $_.Name -like 'ProtonVPN*' -or
                $_.DisplayName -like 'ProtonVPN*' -or
                ([string] $_.PathName) -match $escapedTarget
            } |
            Sort-Object Name -Unique
    )
}

function Stop-ProtonProcessesForTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetDirectory
    )

    $clientWasRunning = $false
    $normalizedTarget = $TargetDirectory.TrimEnd('\') + '\'

    foreach ($process in @(Get-Process)) {
        $processPath = $null
        try {
            $processPath = $process.Path
        } catch {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($processPath)) {
            continue
        }

        if ($processPath.StartsWith($normalizedTarget, [StringComparison]::OrdinalIgnoreCase)) {
            if ($process.ProcessName -like 'ProtonVPN.Client*') {
                $clientWasRunning = $true
            }

            try {
                if ($process.MainWindowHandle -ne 0) {
                    $null = $process.CloseMainWindow()
                    if ($process.WaitForExit(5000)) {
                        continue
                    }
                }
            } catch {
            }

            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    return $clientWasRunning
}

if (-not (Test-IsAdministrator)) {
    Restart-Elevated
}

$mutex = New-Object Threading.Mutex($false, 'Global\ProtonVPNCustomPatchInstaller')
$hasMutex = $false
$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNPatch-{0}" -f [Guid]::NewGuid().ToString('N'))
$backupDirectory = $null
$targetDirectory = $null
$services = @()
$runningServiceNames = @()
$clientWasRunning = $false
$backupCompleted = $false
$installCompleted = $false

try {
    $hasMutex = $mutex.WaitOne(0, $false)
    if (-not $hasMutex) {
        throw 'Another Proton VPN patch installation is already running.'
    }

    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $targetDirectory = Resolve-TargetDirectory
    $payloadRoot = Resolve-PatchSource -WorkingDirectory $workingDirectory

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupDirectory = Join-Path $BackupRoot ("{0}-{1}" -f (Split-Path -Leaf $targetDirectory), $timestamp)

    Write-Host "Target:  $targetDirectory"
    Write-Host "Payload: $payloadRoot"
    Write-Host "Backup:  $backupDirectory"

    $services = Get-ProtonServicesForTarget -TargetDirectory $targetDirectory
    $runningServiceNames = @(
        $services |
            Where-Object { $_.State -eq 'Running' } |
            ForEach-Object { [string] $_.Name }
    )

    if ($PSCmdlet.ShouldProcess($targetDirectory, 'Back up and install Proton VPN custom patch')) {
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

        Write-Host 'Stopping Proton VPN services...'
        foreach ($service in $services) {
            if ($service.State -ne 'Stopped') {
                Stop-Service -Name $service.Name -Force -ErrorAction Stop
            }
        }

        $clientWasRunning = Stop-ProtonProcessesForTarget -TargetDirectory $targetDirectory

        Write-Host 'Backing up the complete official version folder...'
        Invoke-Robocopy -Source $targetDirectory -Destination $backupDirectory -Mirror
        $backupCompleted = $true

        Write-Host 'Applying patch files...'
        Invoke-Robocopy `
            -Source $payloadRoot `
            -Destination $targetDirectory `
            -ExcludedFiles @('Install-ProtonVPNPatch.ps1', 'Install-ProtonVPNPatch.cmd', 'payload.zip')

        $installCompleted = $true
        Write-Host 'Patch installed successfully.' -ForegroundColor Green
    }
} catch {
    Write-Error $_

    if ($backupCompleted -and -not $installCompleted -and $targetDirectory -and $backupDirectory) {
        Write-Warning 'Patch installation failed. Restoring the backup automatically...'
        try {
            Invoke-Robocopy -Source $backupDirectory -Destination $targetDirectory -Mirror
            Write-Host 'Backup restored successfully.' -ForegroundColor Yellow
        } catch {
            Write-Error "Automatic rollback failed. The backup remains at '$backupDirectory'. $($_.Exception.Message)"
        }
    }

    exit 1
} finally {
    if (-not $NoRestart -and $targetDirectory) {
        foreach ($serviceName in $runningServiceNames) {
            try {
                Start-Service -Name $serviceName -ErrorAction Stop
            } catch {
                Write-Warning "Could not restart service '$serviceName': $($_.Exception.Message)"
            }
        }

        if ($clientWasRunning) {
            $clientExecutable = Join-Path $targetDirectory 'ProtonVPN.Client.exe'
            if (Test-Path -LiteralPath $clientExecutable -PathType Leaf) {
                try {
                    Start-Process -FilePath $clientExecutable | Out-Null
                } catch {
                    Write-Warning "Could not restart Proton VPN Client: $($_.Exception.Message)"
                }
            }
        }
    }

    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}

if ($installCompleted) {
    Write-Host "Backup retained at: $backupDirectory"
    exit 0
}
