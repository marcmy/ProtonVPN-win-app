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

    [switch] $ValidateOnly,

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
    param([Parameter(Mandatory = $true)] [string] $Value)

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

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-QuotedProcessArgument -Value $PSCommandPath)
    )
    if (-not [string]::IsNullOrWhiteSpace($elevatedPatchPath)) {
        $arguments += '-PatchPath'
        $arguments += ConvertTo-QuotedProcessArgument -Value $elevatedPatchPath
    }
    $arguments += '-InstallRoot'
    $arguments += ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($InstallRoot))
    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $arguments += '-TargetVersion'
        $arguments += ConvertTo-QuotedProcessArgument -Value $TargetVersion
    }
    if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
        $arguments += '-BackupRoot'
        $arguments += ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($BackupRoot))
    }
    $arguments += '-BackupRetentionCount'
    $arguments += [string] $BackupRetentionCount
    if ($NoRestart) { $arguments += '-NoRestart' }
    if ($RestartClient) { $arguments += '-RestartClient' }
    if ($PauseBeforeExit) { $arguments += '-PauseBeforeExit' }
    if ($WhatIfPreference) { $arguments += '-WhatIf' }

    $process = Start-Process -FilePath 'powershell.exe' `
        -ArgumentList ($arguments -join ' ') `
        -Verb RunAs `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

function Get-VersionSortValue {
    param([Parameter(Mandatory = $true)] [System.IO.DirectoryInfo] $Directory)

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

function Resolve-PayloadRoot {
    param([Parameter(Mandatory = $true)] [string] $Root)

    $manifestPath = Join-Path $Root 'patch-manifest.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Root).Path
    }

    $topLevelDirectories = @(Get-ChildItem -LiteralPath $Root -Directory)
    $topLevelFiles = @(Get-ChildItem -LiteralPath $Root -File)
    if ($topLevelDirectories.Count -eq 1 -and $topLevelFiles.Count -eq 0) {
        return Resolve-PayloadRoot -Root $topLevelDirectories[0].FullName
    }

    $manifests = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter 'patch-manifest.json')
    if ($manifests.Count -ne 1) {
        throw "Complete patch must contain exactly one patch-manifest.json below '$Root'; found $($manifests.Count)."
    }
    return $manifests[0].Directory.FullName
}

function Resolve-PatchSource {
    param([Parameter(Mandatory = $true)] [string] $WorkingDirectory)

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

function Test-CompletePatchPayload {
    param(
        [Parameter(Mandatory = $true)] [string] $PayloadRoot,
        [string] $ExpectedTargetVersion
    )

    $resolvedPayloadRoot = [System.IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\', '/')
    $reparsePoints = @(
        @(
            Get-Item -LiteralPath $resolvedPayloadRoot -Force
            Get-ChildItem -LiteralPath $resolvedPayloadRoot -Recurse -Force |
                Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }
        ) | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }
    )
    if ($reparsePoints.Count -gt 0) {
        throw "Patch payload must not contain symbolic links or other reparse points: $($reparsePoints[0].FullName)"
    }

    $manifestPath = Join-Path $resolvedPayloadRoot 'patch-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Patch manifest was not found at the payload root: $manifestPath"
    }

    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Patch manifest is not valid JSON: $($_.Exception.Message)"
    }

    foreach ($requiredProperty in @(
        'schemaVersion', 'targetVersion', 'buildMode', 'sourceCommit', 'files',
        'completeRuntimeCoverage', 'launcherIncluded', 'upstreamBaseCommit'
    )) {
        if ($null -eq $manifest.PSObject.Properties[$requiredProperty]) {
            throw "Complete patch manifest is missing required property '$requiredProperty'."
        }
    }

    if ([int] $manifest.schemaVersion -ne 2) {
        throw "Complete FastPatch requires manifest schema version 2; found $($manifest.schemaVersion)."
    }
    if (-not [bool] $manifest.completeRuntimeCoverage) {
        throw 'Complete FastPatch manifest does not declare completeRuntimeCoverage=true.'
    }
    if (([string] $manifest.buildMode) -notin @('client', 'service', 'both')) {
        throw "Patch manifest buildMode is invalid: $($manifest.buildMode)"
    }
    if ([string]::IsNullOrWhiteSpace([string] $manifest.sourceCommit)) {
        throw 'Patch manifest sourceCommit cannot be empty.'
    }
    if (([string] $manifest.upstreamBaseCommit) -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Complete patch manifest upstreamBaseCommit is invalid: $($manifest.upstreamBaseCommit)"
    }

    $manifestTargetVersion = ([string] $manifest.targetVersion).Trim().TrimStart([char[]] @('v', 'V'))
    if ($manifestTargetVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "Patch manifest targetVersion is invalid: $($manifest.targetVersion)"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetVersion)) {
        $normalizedExpectedVersion = $ExpectedTargetVersion.Trim().TrimStart([char[]] @('v', 'V'))
        if (-not $manifestTargetVersion.Equals($normalizedExpectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch targets Proton VPN $manifestTargetVersion, not the requested version $normalizedExpectedVersion."
        }
    }

    $declaredPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -eq 0) {
        throw 'Complete patch manifest does not declare any payload files.'
    }

    $installRootEntries = @()
    $toolEntries = @()
    foreach ($file in $manifestFiles) {
        foreach ($requiredFileProperty in @('path', 'scope', 'size', 'sha256')) {
            if ($null -eq $file.PSObject.Properties[$requiredFileProperty]) {
                throw "Complete patch manifest file entry is missing '$requiredFileProperty'."
            }
        }

        $scope = ([string] $file.scope).Trim()
        if ($scope -notin @('version', 'installRoot', 'tool')) {
            throw "Complete patch manifest contains unsupported install scope '$scope' for '$($file.path)'."
        }

        $relativePath = ([string] $file.path).Trim().Replace('/', '\')
        $segments = $relativePath.Split([char[]] @('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains(':') -or
            $segments -contains '..') {
            throw "Patch manifest contains an unsafe payload path: $($file.path)"
        }
        if ($relativePath.StartsWith('ServiceData\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch payload must not modify runtime ServiceData: $relativePath"
        }
        if (-not $declaredPaths.Add($relativePath)) {
            throw "Patch manifest declares the payload path more than once: $relativePath"
        }

        if ($scope -eq 'installRoot') {
            if (-not $relativePath.Equals('ProtonVPN.Launcher.exe', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Unsupported install-root payload file: $relativePath"
            }
            $installRootEntries += $file
        } elseif ($scope -eq 'tool') {
            if (-not $relativePath.Equals('Tools\Install-ProtonVPNPatch.base.ps1', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Unsupported complete-patch tool payload file: $relativePath"
            }
            $toolEntries += $file
        }

        $payloadPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedPayloadRoot $relativePath))
        $payloadRootPrefix = $resolvedPayloadRoot + [System.IO.Path]::DirectorySeparatorChar
        if (-not $payloadPath.StartsWith($payloadRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch manifest path escapes the payload root: $relativePath"
        }
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Patch payload file declared by the manifest is missing: $relativePath"
        }

        $payloadFile = Get-Item -LiteralPath $payloadPath
        if ([long] $file.size -ne $payloadFile.Length) {
            throw "Patch payload size mismatch for '$relativePath'. Expected $($file.size), found $($payloadFile.Length)."
        }
        $expectedHash = ([string] $file.sha256).Trim().ToLowerInvariant()
        if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
            throw "Patch manifest contains an invalid SHA-256 value for '$relativePath'."
        }
        $actualHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $actualHash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch payload hash mismatch for '$relativePath'. Expected $expectedHash, found $actualHash."
        }
    }

    $launcherExpected = [bool] $manifest.launcherIncluded
    if ($launcherExpected -ne ($installRootEntries.Count -eq 1)) {
        throw 'Complete patch launcherIncluded metadata does not match the install-root launcher payload.'
    }
    if ($toolEntries.Count -ne 1) {
        throw 'Complete patch must contain exactly one validated base-installer helper.'
    }

    $actualPayloadFiles = @(
        Get-ChildItem -LiteralPath $resolvedPayloadRoot -Recurse -File |
            Where-Object { -not $_.FullName.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase) }
    )
    foreach ($actualFile in $actualPayloadFiles) {
        $relativePath = $actualFile.FullName.Substring($resolvedPayloadRoot.Length).TrimStart('\', '/')
        if (-not $declaredPaths.Contains($relativePath)) {
            throw "Patch payload contains a file that is not declared by the manifest: $relativePath"
        }
    }
    if ($actualPayloadFiles.Count -ne $declaredPaths.Count) {
        throw "Patch manifest file count does not match the payload. Declared $($declaredPaths.Count), found $($actualPayloadFiles.Count)."
    }

    return $manifest
}

function New-VersionPayload {
    param(
        [Parameter(Mandatory = $true)] [string] $PayloadRoot,
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)] [string] $Destination
    )

    Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null

    $versionFiles = @($Manifest.files | Where-Object { ([string] $_.scope) -eq 'version' })
    if ($versionFiles.Count -eq 0) {
        throw 'Complete patch does not contain any version-folder payload files.'
    }

    foreach ($file in $versionFiles) {
        $relativePath = ([string] $file.path).Replace('/', '\')
        $sourcePath = Join-Path $PayloadRoot $relativePath
        $targetPath = Join-Path $Destination $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Path $targetPath -Parent) | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
    }

    $versionManifestFiles = @(
        foreach ($file in $versionFiles) {
            [ordered]@{
                path = [string] $file.path
                size = [long] $file.size
                sha256 = [string] $file.sha256
            }
        }
    )
    $sourceRefValue = if ($null -ne $Manifest.PSObject.Properties['sourceRef']) { [string] $Manifest.sourceRef } else { '' }
    $workflowRunIdValue = if ($null -ne $Manifest.PSObject.Properties['workflowRunId']) { [string] $Manifest.workflowRunId } else { '' }
    $versionManifest = [ordered]@{
        schemaVersion = 1
        targetVersion = [string] $Manifest.targetVersion
        buildMode = [string] $Manifest.buildMode
        sourceCommit = [string] $Manifest.sourceCommit
        sourceRef = $sourceRefValue
        workflowRunId = $workflowRunIdValue
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        files = $versionManifestFiles
    }
    $versionManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Destination 'patch-manifest.json') -Encoding utf8
}

function Get-BackupDirectories {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $TargetFolderName
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }
    $pattern = '^' + [Regex]::Escape($TargetFolderName) + '-backup-\d{8}-\d{6}$'
    return @(
        Get-ChildItem -LiteralPath $Root -Directory |
            Where-Object { $_.Name -match $pattern } |
            Sort-Object Name -Descending
    )
}

function Stop-RootLauncherProcesses {
    param([Parameter(Mandatory = $true)] [string] $LauncherPath)

    $normalizedLauncher = [System.IO.Path]::GetFullPath($LauncherPath)
    foreach ($process in @(Get-Process -Name 'ProtonVPN.Launcher' -ErrorAction SilentlyContinue)) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($process.Path) -and
                [System.IO.Path]::GetFullPath($process.Path).Equals($normalizedLauncher, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                $process.WaitForExit(5000)
            }
        } catch {
            throw "Could not stop ProtonVPN.Launcher before patching '$normalizedLauncher': $($_.Exception.Message)"
        }
    }
}

function Invoke-BaseInstaller {
    param(
        [Parameter(Mandatory = $true)] [string] $BaseInstallerPath,
        [Parameter(Mandatory = $true)] [string] $VersionPayloadPath,
        [Parameter(Mandatory = $true)] [string] $ResolvedTargetVersion
    )

    $arguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-QuotedProcessArgument -Value $BaseInstallerPath),
        '-PatchPath', (ConvertTo-QuotedProcessArgument -Value $VersionPayloadPath),
        '-InstallRoot', (ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($InstallRoot))),
        '-TargetVersion', (ConvertTo-QuotedProcessArgument -Value $ResolvedTargetVersion),
        '-BackupRetentionCount', [string] $BackupRetentionCount
    )
    if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
        $arguments += '-BackupRoot'
        $arguments += ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($BackupRoot))
    }
    if ($NoRestart) { $arguments += '-NoRestart' }
    if ($RestartClient) { $arguments += '-RestartClient' }
    if ($ValidateOnly) { $arguments += '-ValidateOnly' }
    if ($WhatIfPreference) { $arguments += '-WhatIf' }

    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList ($arguments -join ' ') -Wait -PassThru
    return $process.ExitCode
}

$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNCompletePatch-{0}" -f [Guid]::NewGuid().ToString('N'))
$pendingRootBackupDirectory = $null
$rootLauncherPatched = $false
$rootLauncherPersisted = $false
$exitCode = 1

try {
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $payloadRoot = Resolve-PatchSource -WorkingDirectory $workingDirectory
    $manifest = Test-CompletePatchPayload -PayloadRoot $payloadRoot -ExpectedTargetVersion $TargetVersion
    $resolvedTargetVersion = ([string] $manifest.targetVersion).Trim().TrimStart([char[]] @('v', 'V'))

    $versionPayload = Join-Path $workingDirectory 'VersionPayload'
    New-VersionPayload -PayloadRoot $payloadRoot -Manifest $manifest -Destination $versionPayload
    $baseInstallerPath = Join-Path $payloadRoot 'Tools\Install-ProtonVPNPatch.base.ps1'

    if ($ValidateOnly) {
        $baseExitCode = Invoke-BaseInstaller `
            -BaseInstallerPath $baseInstallerPath `
            -VersionPayloadPath $versionPayload `
            -ResolvedTargetVersion $resolvedTargetVersion
        if ($baseExitCode -ne 0) {
            throw "Base version-folder payload validation failed with exit code $baseExitCode."
        }
        Write-Host "Complete FastPatch payload validation succeeded for Proton VPN $resolvedTargetVersion." -ForegroundColor Green
        $exitCode = 0
    } else {
        if (-not (Test-IsAdministrator)) {
            Write-Host 'Complete FastPatch payload validation succeeded; requesting administrator access.'
            Restart-Elevated
        }

        $targetDirectory = Resolve-TargetDirectory
        $installedVersion = (Split-Path -Leaf $targetDirectory).TrimStart([char[]] @('v', 'V'))
        if (-not $installedVersion.Equals($resolvedTargetVersion, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch targets Proton VPN $resolvedTargetVersion, but the selected installation is $installedVersion."
        }

        $resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
        $resolvedBackupRoot = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
            Split-Path -Path $targetDirectory -Parent
        } else {
            [System.IO.Path]::GetFullPath($BackupRoot)
        }
        New-Item -ItemType Directory -Path $resolvedBackupRoot -Force | Out-Null

        $targetFolderName = Split-Path -Leaf $targetDirectory
        $backupsBefore = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($backup in Get-BackupDirectories -Root $resolvedBackupRoot -TargetFolderName $targetFolderName) {
            $null = $backupsBefore.Add([System.IO.Path]::GetFullPath($backup.FullName))
        }

        if ([bool] $manifest.launcherIncluded) {
            $launcherSource = Join-Path $payloadRoot 'ProtonVPN.Launcher.exe'
            $launcherTarget = Join-Path $resolvedInstallRoot 'ProtonVPN.Launcher.exe'
            if (-not (Test-Path -LiteralPath $launcherTarget -PathType Leaf)) {
                throw "Installed ProtonVPN.Launcher.exe was not found at the install root: $launcherTarget"
            }

            if ($WhatIfPreference) {
                Write-Host "What if: replace root launcher '$launcherTarget'."
            } elseif ($PSCmdlet.ShouldProcess($launcherTarget, 'Replace root ProtonVPN launcher')) {
                $pendingRootBackupDirectory = Join-Path $resolvedBackupRoot ('.pending-fastpatch-root-' + [Guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $pendingRootBackupDirectory -Force | Out-Null
                $pendingLauncherBackup = Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe'
                Copy-Item -LiteralPath $launcherTarget -Destination $pendingLauncherBackup -Force
                $rootLauncherPatched = $true

                Stop-RootLauncherProcesses -LauncherPath $launcherTarget
                Copy-Item -LiteralPath $launcherSource -Destination $launcherTarget -Force
                $expectedLauncherHash = [string] (@($manifest.files | Where-Object { ([string] $_.scope) -eq 'installRoot' })[0].sha256)
                $installedLauncherHash = (Get-FileHash -LiteralPath $launcherTarget -Algorithm SHA256).Hash.ToLowerInvariant()
                if (-not $installedLauncherHash.Equals($expectedLauncherHash, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Installed ProtonVPN.Launcher.exe hash verification failed after copy.'
                }
            } else {
                throw 'Root ProtonVPN launcher update was not approved; complete FastPatch installation cancelled.'
            }
        }

        $baseExitCode = Invoke-BaseInstaller `
            -BaseInstallerPath $baseInstallerPath `
            -VersionPayloadPath $versionPayload `
            -ResolvedTargetVersion $resolvedTargetVersion
        if ($baseExitCode -ne 0) {
            throw "Version-folder FastPatch installation failed with exit code $baseExitCode."
        }

        if ($rootLauncherPatched -and $pendingRootBackupDirectory) {
            $newBackups = @(
                Get-BackupDirectories -Root $resolvedBackupRoot -TargetFolderName $targetFolderName |
                    Where-Object { -not $backupsBefore.Contains([System.IO.Path]::GetFullPath($_.FullName)) }
            )
            if ($newBackups.Count -ge 1) {
                $selectedBackup = $newBackups | Sort-Object Name -Descending | Select-Object -First 1
                if ($newBackups.Count -gt 1) {
                    Write-Warning "Multiple new version backups were found; associating the root launcher backup with '$($selectedBackup.FullName)'."
                }
                $installRootBackupDir = Join-Path $selectedBackup.FullName 'InstallRoot'
                New-Item -ItemType Directory -Path $installRootBackupDir -Force | Out-Null
                Copy-Item `
                    -LiteralPath (Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe') `
                    -Destination (Join-Path $installRootBackupDir 'ProtonVPN.Launcher.exe') `
                    -Force
                Remove-Item -LiteralPath $pendingRootBackupDirectory -Recurse -Force
                $pendingRootBackupDirectory = $null
                $rootLauncherPersisted = $true
                Write-Host "Root launcher backup retained with version backup: $installRootBackupDir"
            } else {
                Write-Warning "Version patch succeeded, but its new backup directory could not be identified. Root launcher backup remains at '$pendingRootBackupDirectory'."
            }
        }

        Write-Host 'Complete Proton VPN FastPatch installed successfully.' -ForegroundColor Green
        $exitCode = 0
    }
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue

    if ($rootLauncherPatched -and -not $rootLauncherPersisted -and $pendingRootBackupDirectory) {
        $pendingLauncherBackup = Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe'
        $launcherTarget = Join-Path ([System.IO.Path]::GetFullPath($InstallRoot)) 'ProtonVPN.Launcher.exe'
        if (Test-Path -LiteralPath $pendingLauncherBackup -PathType Leaf) {
            try {
                Stop-RootLauncherProcesses -LauncherPath $launcherTarget
                Copy-Item -LiteralPath $pendingLauncherBackup -Destination $launcherTarget -Force
                Write-Host 'Root ProtonVPN launcher restored after failed installation.' -ForegroundColor Yellow
                Remove-Item -LiteralPath $pendingRootBackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
                $pendingRootBackupDirectory = $null
            } catch {
                Write-Error -Message "Could not restore the root ProtonVPN launcher. Backup remains at '$pendingRootBackupDirectory'. $($_.Exception.Message)" -ErrorAction Continue
            }
        }
    }
    $exitCode = 1
} finally {
    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($PauseBeforeExit) {
        try { Read-Host 'Press Enter to close' | Out-Null } catch {}
    }
}

exit $exitCode
