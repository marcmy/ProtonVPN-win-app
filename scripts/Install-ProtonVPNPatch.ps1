[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $PatchPath,

    [ValidateNotNullOrEmpty()]
    [string] $InstallRoot = 'C:\Program Files\Proton\VPN',

    [string] $TargetVersion,

    [string] $BackupRoot,

    [ValidateRange(0, 100)]
    [int] $BackupRetentionCount = 3,

    [switch] $NoRestart,

    [switch] $RestartClient,

    [switch] $PauseBeforeExit
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
    $elevatedPatchPath = $PatchPath
    if (-not [string]::IsNullOrWhiteSpace($elevatedPatchPath)) {
        $elevatedPatchPath = [System.IO.Path]::GetFullPath($elevatedPatchPath)
    }

    $argumentList = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-QuotedProcessArgument -Value $PSCommandPath)
    )

    if (-not [string]::IsNullOrWhiteSpace($elevatedPatchPath)) {
        $argumentList += '-PatchPath'
        $argumentList += ConvertTo-QuotedProcessArgument -Value $elevatedPatchPath
    }

    $argumentList += '-InstallRoot'
    $argumentList += ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($InstallRoot))

    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $argumentList += '-TargetVersion'
        $argumentList += ConvertTo-QuotedProcessArgument -Value $TargetVersion
    }

    if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
        $argumentList += '-BackupRoot'
        $argumentList += ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($BackupRoot))
    }

    $argumentList += '-BackupRetentionCount'
    $argumentList += [string] $BackupRetentionCount

    if ($NoRestart) {
        $argumentList += '-NoRestart'
    }

    if ($RestartClient) {
        $argumentList += '-RestartClient'
    }

    if ($PauseBeforeExit) {
        $argumentList += '-PauseBeforeExit'
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

    $versionText = $Directory.Name.TrimStart([char[]] @('v', 'V'))
    $parsedVersion = [Version]::new(0, 0)
    if ([Version]::TryParse($versionText, [ref] $parsedVersion)) {
        return $parsedVersion
    }

    return [Version]::new(0, 0)
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
        Get-ChildItem -LiteralPath $InstallRoot -Directory |
            Where-Object { $_.Name -match '^v\d+(?:\.\d+){1,3}$' } |
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
        throw 'The patch does not contain any expected Proton VPN payload files.'
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

    $copyMode = if ($Mirror) { '/MIR' } else { '/E' }
    $arguments = @(
        $Source,
        $Destination,
        $copyMode,
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

            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            try {
                $process.WaitForExit(5000)
            } catch {
            }
        }
    }

    return $clientWasRunning
}

function Stop-ProtonServices {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Services
    )

    foreach ($service in @($Services | Sort-Object Name -Descending)) {
        $controller = Get-Service -Name $service.Name -ErrorAction Stop
        if ($controller.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            continue
        }

        Stop-Service -Name $service.Name -Force -ErrorAction Stop
        $controller = Get-Service -Name $service.Name -ErrorAction Stop
        $controller.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(20)
        )
    }
}

function Remove-OldBackups {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $TargetFolderName,

        [Parameter(Mandatory = $true)]
        [string] $CurrentBackupDirectory,

        [Parameter(Mandatory = $true)]
        [int] $RetentionCount
    )

    if ($RetentionCount -eq 0 -or -not (Test-Path -LiteralPath $Root -PathType Container)) {
        return
    }

    $backupNamePattern = '^' + [Regex]::Escape($TargetFolderName) + '-backup-\d{8}-\d{6}$'
    $backups = @(
        Get-ChildItem -LiteralPath $Root -Directory |
            Where-Object { $_.Name -match $backupNamePattern } |
            Sort-Object Name -Descending
    )

    $keepPaths = @([System.IO.Path]::GetFullPath($CurrentBackupDirectory))
    foreach ($backup in $backups) {
        if ($keepPaths.Count -ge $RetentionCount) {
            break
        }

        $fullPath = [System.IO.Path]::GetFullPath($backup.FullName)
        if ($keepPaths -notcontains $fullPath) {
            $keepPaths += $fullPath
        }
    }

    foreach ($backup in $backups) {
        $fullPath = [System.IO.Path]::GetFullPath($backup.FullName)
        if ($keepPaths -contains $fullPath) {
            continue
        }

        Write-Host "Removing old backup: $fullPath"
        Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
    }
}

if (-not (Test-IsAdministrator)) {
    Restart-Elevated
}

$mutex = New-Object Threading.Mutex($false, 'Global\ProtonVPNCustomPatchInstaller')
$hasMutex = $false
$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNPatch-{0}" -f [Guid]::NewGuid().ToString('N'))
$backupDirectory = $null
$resolvedBackupRoot = $null
$targetDirectory = $null
$targetFolderName = $null
$services = @()
$runningServiceNames = @()
$clientWasRunning = $false
$backupCompleted = $false
$installCompleted = $false
$exitCode = 1
$transcriptStarted = $false
$logPath = $null

if (-not $WhatIfPreference) {
    try {
        $logRoot = Join-Path $env:ProgramData 'ProtonVPN Custom Patch\Logs'
        New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
        $logPath = Join-Path $logRoot ("install-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Start-Transcript -LiteralPath $logPath -Force | Out-Null
        $transcriptStarted = $true
        Write-Host "Log:     $logPath"
    } catch {
        Write-Warning "Could not start installer logging: $($_.Exception.Message)"
    }
}

try {
    $hasMutex = $mutex.WaitOne(0, $false)
    if (-not $hasMutex) {
        throw 'Another Proton VPN patch installation is already running.'
    }

    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $targetDirectory = Resolve-TargetDirectory
    $payloadRoot = Resolve-PatchSource -WorkingDirectory $workingDirectory

    $resolvedBackupRoot = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
        Split-Path -Path $targetDirectory -Parent
    } else {
        [System.IO.Path]::GetFullPath($BackupRoot)
    }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $targetFolderName = Split-Path -Leaf $targetDirectory
    $backupDirectory = Join-Path $resolvedBackupRoot ("{0}-backup-{1}" -f $targetFolderName, $timestamp)

    $normalizedTargetDirectory = [System.IO.Path]::GetFullPath($targetDirectory).TrimEnd('\') + '\'
    $normalizedBackupDirectory = [System.IO.Path]::GetFullPath($backupDirectory).TrimEnd('\') + '\'
    if ($normalizedBackupDirectory.StartsWith($normalizedTargetDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup directory cannot be inside the Proton VPN version folder: $backupDirectory"
    }

    if (Test-Path -LiteralPath $backupDirectory) {
        throw "Backup directory already exists: $backupDirectory"
    }

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

        Write-Host 'Closing Proton VPN client...'
        $clientWasRunning = Stop-ProtonProcessesForTarget -TargetDirectory $targetDirectory

        Write-Host 'Stopping Proton VPN services...'
        Stop-ProtonServices -Services $services

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

    $exitCode = 0
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue

    if ($backupCompleted -and -not $installCompleted -and $targetDirectory -and $backupDirectory) {
        Write-Warning 'Patch installation failed. Restoring the backup automatically...'
        try {
            Invoke-Robocopy -Source $backupDirectory -Destination $targetDirectory -Mirror
            Write-Host 'Backup restored successfully.' -ForegroundColor Yellow
        } catch {
            Write-Error `
                -Message "Automatic rollback failed. The backup remains at '$backupDirectory'. $($_.Exception.Message)" `
                -ErrorAction Continue
        }
    }

    $exitCode = 1
} finally {
    if (-not $NoRestart -and $targetDirectory) {
        foreach ($serviceName in $runningServiceNames) {
            try {
                Start-Service -Name $serviceName -ErrorAction Stop
            } catch {
                Write-Warning "Could not restart service '$serviceName': $($_.Exception.Message)"
            }
        }

        if ($RestartClient -and $clientWasRunning) {
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
    try {
        Remove-OldBackups `
            -Root $resolvedBackupRoot `
            -TargetFolderName $targetFolderName `
            -CurrentBackupDirectory $backupDirectory `
            -RetentionCount $BackupRetentionCount
    } catch {
        Write-Warning "Patch installed, but old backup cleanup failed: $($_.Exception.Message)"
    }

    Write-Host "Backup retained at: $backupDirectory"
    if ($clientWasRunning -and -not $RestartClient) {
        Write-Host 'Proton VPN Client was left closed to avoid changing the previous connection state.' -ForegroundColor Yellow
    }
} elseif ($exitCode -ne 0) {
    Write-Host 'Patch installation failed.' -ForegroundColor Red
}

if ($transcriptStarted) {
    Write-Host "Installer log retained at: $logPath"
    Stop-Transcript | Out-Null
}

if ($PauseBeforeExit) {
    Write-Host
    $null = Read-Host 'Press Enter to close this window'
}

exit $exitCode
