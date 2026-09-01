from pathlib import Path


def read(path):
    return Path(path).read_text(encoding='utf-8').replace('\r\n', '\n')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace_exact(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected exactly one match, found {count}')
    return text.replace(old, new)

base_path = 'scripts/Install-ProtonVPNPatch.ps1'
complete_path = 'scripts/Install-ProtonVPNCompletePatch.ps1'
test_path = '.github/scripts/test-fast-patch-secure-staging.ps1'
base = read(base_path)
complete = read(complete_path)
test = read(test_path)

# A valid install can have no matching Proton services (client-only fixtures are one example).
base = replace_exact(base,
'''function Stop-ProtonServices {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Services
    )
''',
'''function Stop-ProtonServices {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]] $Services
    )
''', 'allow empty service collection')
base = replace_exact(base,
'''    $services = Get-ProtonServicesForTarget -TargetDirectory $targetDirectory
''',
'''    $services = @(Get-ProtonServicesForTarget -TargetDirectory $targetDirectory)
''', 'normalize service discovery to array')

# Keep rollback input inside the Administrator/SYSTEM-only trusted stage. BackupRoot remains
# archival output only and is never consumed after privileged installation has started.
base = replace_exact(base,
'''    if ($exitCode -gt 7) {
        throw "robocopy failed with exit code $exitCode while copying '$Source' to '$Destination'."
    }
}

function Get-ProtonServicesForTarget {
''',
'''    if ($exitCode -gt 7) {
        throw "robocopy failed with exit code $exitCode while copying '$Source' to '$Destination'."
    }
}

function Restore-TrustedVersionBackup {
    param(
        [Parameter(Mandatory = $true)] [string] $TrustedRollbackDirectory,
        [Parameter(Mandatory = $true)] [string] $TargetDirectory
    )

    Invoke-Robocopy `
        -Source $TrustedRollbackDirectory `
        -Destination $TargetDirectory `
        -Mirror `
        -ExcludedDirectories @('ServiceData')
}

function Get-ProtonServicesForTarget {
''', 'insert protected rollback helper')
base = replace_exact(base,
'''$backupDirectory = $null
$resolvedBackupRoot = $null
$targetDirectory = $null
''',
'''$backupDirectory = $null
$resolvedBackupRoot = $null
$trustedRollbackDirectory = $null
$targetDirectory = $null
''', 'declare protected rollback directory')
base = replace_exact(base,
'''    if ($PSCmdlet.ShouldProcess($targetDirectory, 'Back up and install Proton VPN custom patch')) {
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

        Write-Host 'Closing Proton VPN client...'
''',
'''    if ($PSCmdlet.ShouldProcess($targetDirectory, 'Back up and install Proton VPN custom patch')) {
        Write-Host 'Closing Proton VPN client...'
''', 'defer retained backup creation')
base = replace_exact(base,
'''        # ServiceData contains live settings and access-restricted WireGuard key material.
        # Patch payloads cannot target it, so preserve it in place across backup and rollback.
        Write-Host 'Backing up installed program files while preserving runtime ServiceData...'
        Invoke-Robocopy `
            -Source $targetDirectory `
            -Destination $backupDirectory `
            -Mirror `
            -ExcludedDirectories @('ServiceData')
        $backupCompleted = $true

        Write-Host 'Applying patch files...'
''',
'''        # ServiceData contains live settings and access-restricted WireGuard key material.
        # Patch payloads cannot target it, so preserve it in place across backup and rollback.
        # BackupRoot may be user-writable; it is archival output only. Rollback consumes only
        # the Administrator/SYSTEM-controlled snapshot under TrustedStagePath.
        $trustedRollbackDirectory = Join-Path `
            $TrustedStagePath `
            ('Rollback\\Version-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $trustedRollbackDirectory -Force | Out-Null

        Write-Host 'Creating protected rollback snapshot while preserving runtime ServiceData...'
        Invoke-Robocopy `
            -Source $targetDirectory `
            -Destination $trustedRollbackDirectory `
            -Mirror `
            -ExcludedDirectories @('ServiceData')

        Write-Host 'Retaining requested backup copy...'
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
        Invoke-Robocopy `
            -Source $trustedRollbackDirectory `
            -Destination $backupDirectory `
            -Mirror
        $backupCompleted = $true

        Write-Host 'Applying patch files...'
''', 'protect version rollback source')
base = replace_exact(base,
'''    if ($backupCompleted -and -not $installCompleted -and $targetDirectory -and $backupDirectory) {
        Write-Warning 'Patch installation failed. Restoring the backup automatically...'
        try {
            Invoke-Robocopy `
                -Source $backupDirectory `
                -Destination $targetDirectory `
                -Mirror `
                -ExcludedDirectories @('ServiceData')
            Write-Host 'Backup restored successfully.' -ForegroundColor Yellow
''',
'''    if ($backupCompleted -and -not $installCompleted -and $targetDirectory -and $trustedRollbackDirectory) {
        Write-Warning 'Patch installation failed. Restoring the protected rollback snapshot automatically...'
        try {
            Restore-TrustedVersionBackup `
                -TrustedRollbackDirectory $trustedRollbackDirectory `
                -TargetDirectory $targetDirectory
            Write-Host 'Protected rollback snapshot restored successfully.' -ForegroundColor Yellow
''', 'consume protected version rollback source')

# Same rule for the install-root launcher in complete patches.
complete = replace_exact(complete,
'''                $pendingRootBackupDirectory = Join-Path $resolvedBackupRoot ('.pending-fastpatch-root-' + [Guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $pendingRootBackupDirectory -Force | Out-Null
                $pendingLauncherBackup = Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe'
                Copy-Item -LiteralPath $launcherTarget -Destination $pendingLauncherBackup -Force
                $rootLauncherPatched = $true
''',
'''                # Root-launcher rollback input must never live in mutable BackupRoot storage.
                Ensure-AdministratorOnlySubdirectory -Root $TrustedStagePath -RelativePath 'Rollback'
                $rollbackRoot = Join-Path $TrustedStagePath 'Rollback'
                $pendingRootBackupDirectory = Join-Path `
                    $rollbackRoot `
                    ('InstallRoot-' + [Guid]::NewGuid().ToString('N'))
                New-AdministratorOnlyDirectory -Path $pendingRootBackupDirectory
                $pendingLauncherBackup = Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe'
                $launcherBackupHash = (Get-FileHash -LiteralPath $launcherTarget -Algorithm SHA256).Hash.ToLowerInvariant()
                Copy-AdministratorOnlyFile `
                    -SourcePath $launcherTarget `
                    -DestinationPath $pendingLauncherBackup `
                    -ExpectedHash $launcherBackupHash
                $rootLauncherPatched = $true
''', 'protect root launcher rollback source')

# Permanent regression coverage for protected rollback and the empty-service path exercised by
# Test-BaseInstallerEndToEnd.
test = replace_exact(test,
'''Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Test-PatchPayload'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'
''',
'''Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Test-PatchPayload'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-SystemExecutablePath'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Invoke-Robocopy'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Restore-TrustedVersionBackup'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'
''', 'import rollback helpers')

test = replace_exact(test,
'''function Test-BaseInstallerEndToEnd {
''',
'''function Test-ProtectedRollbackIgnoresMutableRetainedBackup {
    $fixtureRoot = Join-Path $testRoot 'protected-rollback-source'
    $trustedRollback = Join-Path $fixtureRoot 'trusted-rollback'
    $retainedBackup = Join-Path $fixtureRoot 'user-writable-retained-backup'
    $target = Join-Path $fixtureRoot 'target'
    New-Item -ItemType Directory -Force -Path $trustedRollback | Out-Null
    New-Item -ItemType Directory -Force -Path $retainedBackup | Out-Null
    New-Item -ItemType Directory -Force -Path $target | Out-Null

    Write-TestText (Join-Path $trustedRollback 'ProtonVPN.Client.dll') 'original-protected-bytes'
    Write-TestText (Join-Path $retainedBackup 'ProtonVPN.Client.dll') 'attacker-mutated-retained-backup'
    Write-TestText (Join-Path $target 'ProtonVPN.Client.dll') 'partially-installed-bytes'

    Restore-TrustedVersionBackup `
        -TrustedRollbackDirectory $trustedRollback `
        -TargetDirectory $target

    Assert-Condition `
        ((Get-Content -LiteralPath (Join-Path $target 'ProtonVPN.Client.dll') -Raw) -eq 'original-protected-bytes') `
        'Rollback consumed the mutable retained BackupRoot copy instead of the protected trusted-stage snapshot.'
}

function Test-BaseInstallerEndToEnd {
''', 'insert protected rollback regression')

static_marker = '''    Assert-Condition ($base.Contains("Join-Path `$windowsPowerShellHome 'Modules\\CimCmdlets\\CimCmdlets.psd1'")) `
        'Base FastPatch does not import CimCmdlets from the protected Windows PowerShell module directory.'

    Assert-Condition (-not $sfx.Contains('IEXPRESS')) `
'''
static_replacement = '''    Assert-Condition ($base.Contains("Join-Path `$windowsPowerShellHome 'Modules\\CimCmdlets\\CimCmdlets.psd1'")) `
        'Base FastPatch does not import CimCmdlets from the protected Windows PowerShell module directory.'
    Assert-Condition ($base.Contains("`$trustedRollbackDirectory = Join-Path")) `
        'Base FastPatch does not create its rollback source inside trusted staging.'
    Assert-Condition ($base.Contains('-TrustedRollbackDirectory $trustedRollbackDirectory')) `
        'Base FastPatch rollback does not consume the protected trusted-stage snapshot.'
    Assert-Condition (-not $base.Contains('-Source $backupDirectory `')) `
        'Base FastPatch still consumes the mutable retained BackupRoot copy as privileged rollback input.'
    Assert-Condition ($complete.Contains("`$rollbackRoot = Join-Path `$TrustedStagePath 'Rollback'")) `
        'Complete FastPatch does not protect the root-launcher rollback source inside trusted staging.'
    Assert-Condition (-not $complete.Contains("Join-Path `$resolvedBackupRoot ('.pending-fastpatch-root-'")) `
        'Complete FastPatch still stores its live launcher rollback source in mutable BackupRoot storage.'

    Assert-Condition (-not $sfx.Contains('IEXPRESS')) `
'''
test = replace_exact(test, static_marker, static_replacement, 'add rollback static contracts')

test = replace_exact(test,
'''    Test-ReparseSourceRejected
    Test-BaseInstallerEndToEnd
    Test-CompleteLauncherRollbackAfterProtectedStageFailure
''',
'''    Test-ReparseSourceRejected
    Test-ProtectedRollbackIgnoresMutableRetainedBackup
    Test-BaseInstallerEndToEnd
    Test-CompleteLauncherRollbackAfterProtectedStageFailure
''', 'run protected rollback regression')

write(base_path, base)
write(complete_path, complete)
write(test_path, test)
