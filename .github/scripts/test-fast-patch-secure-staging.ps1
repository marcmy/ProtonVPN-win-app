[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$windowsPowerShellModulePath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules'
if ((Test-Path -LiteralPath $windowsPowerShellModulePath -PathType Container) -and
    (($env:PSModulePath -split ';') -notcontains $windowsPowerShellModulePath)) {
    $env:PSModulePath = $windowsPowerShellModulePath + ';' + $env:PSModulePath
}
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$baseInstallerScript = Join-Path $repositoryRoot 'scripts\Install-ProtonVPNPatch.ps1'
$completeInstallerScript = Join-Path $repositoryRoot 'scripts\Install-ProtonVPNCompletePatch.ps1'
$sfxBuilderScript = Join-Path $repositoryRoot 'scripts\New-ProtonVPNPatchSfx.ps1'
$installerLauncher = Join-Path $repositoryRoot 'scripts\Install-ProtonVPNPatch.cmd'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("protonvpn-fastpatch-secure-stage-{0}" -f [Guid]::NewGuid().ToString('N'))

# Production functions imported below normally receive these from the installer param block.
# The regression imports the functions without executing that block, so initialize the same
# script-scope state explicitly.
$script:PatchPath = ''
$script:ExpectedPatchArchiveSha256 = ''
$script:TrustedArchiveManifestText = ''
$script:TrustedSfxArchiveBytes = $null
$script:ValidatedManifestText = ''

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Invoke-TestProcessCapture {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string] $Arguments,
        [string] $WorkingDirectory = ''
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start test child process: $FilePath" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $exitCode = [int] $process.ExitCode
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $output = @()
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { $output += $stdout.TrimEnd() }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { $output += $stderr.TrimEnd() }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
    } finally {
        $process.Dispose()
    }
}

function Write-TestText {
    param([string] $Path, [AllowEmptyString()] [string] $Content)
    New-Item -ItemType Directory -Force -Path (Split-Path -Path $Path -Parent) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Get-TextHash {
    param([Parameter(Mandatory = $true)] [string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Add-ManifestFile {
    param(
        [Parameter(Mandatory = $true)] [Collections.IList] $List,
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $RelativePath,
        [string] $Scope = ''
    )

    $path = Join-Path $Root $RelativePath
    $entry = [ordered]@{
        path = $RelativePath.Replace('\', '/')
        size = (Get-Item -LiteralPath $path).Length
        sha256 = Get-TextHash -Path $path
    }
    if (-not [string]::IsNullOrWhiteSpace($Scope)) {
        $entry['scope'] = $Scope
    }
    $List.Add($entry) | Out-Null
}

function Import-FunctionDefinition {
    param(
        [Parameter(Mandatory = $true)] [string] $ScriptPath,
        [Parameter(Mandatory = $true)] [string] $Name
    )

    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $ScriptPath,
        [ref] $tokens,
        [ref] $errors)
    if ($errors.Count -gt 0) {
        throw "PowerShell parser rejected '$ScriptPath': $($errors[0].Message)"
    }

    $functionAst = $ast.Find(
        {
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $Name
        },
        $true)
    if ($null -eq $functionAst) {
        throw "Function '$Name' was not found in '$ScriptPath'."
    }

    $definition = $functionAst.Extent.Text
    $scriptScopedDefinition = [regex]::Replace(
        $definition,
        '^function\s+' + [regex]::Escape($Name),
        ('function script:' + $Name),
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    Invoke-Expression $scriptScopedDefinition
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-StagePath {
    param([Parameter(Mandatory = $true)] [string] $StageId)
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    return Join-Path $programFiles ('.ProtonVPNFastPatchStage-' + $StageId)
}

function Get-CurrentStageDirectories {
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    return @(
        Get-ChildItem -LiteralPath $programFiles -Directory -Force -Filter '.ProtonVPNFastPatchStage-*' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.FullName }
    )
}

Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'ConvertTo-Base64Utf8'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'ConvertTo-CompressedEncodedCommand'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Resolve-PayloadRoot'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Resolve-PatchSource'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Test-PatchPayload'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-SystemExecutablePath'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Invoke-Robocopy'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Restore-TrustedVersionBackup'
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'
Import-FunctionDefinition -ScriptPath $sfxBuilderScript -Name 'Get-SfxLoaderScript'

function New-BootstrapFixture {
    param([Parameter(Mandatory = $true)] [string] $Root)

    $sourceRoot = Join-Path $Root 'source-payload'
    $snapshotRoot = Join-Path $Root 'snapshots'
    $probePath = Join-Path $Root 'probe.json'
    New-Item -ItemType Directory -Force -Path (Join-Path $sourceRoot 'Runtime') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $sourceRoot 'Tools') | Out-Null
    New-Item -ItemType Directory -Force -Path $snapshotRoot | Out-Null

    Write-TestText (Join-Path $sourceRoot 'Runtime\version.bin') 'validated-version-bytes'
    Write-TestText (Join-Path $sourceRoot 'Tools\Install-ProtonVPNPatch.base.ps1') 'validated-helper-bytes'

    $manifestFiles = [Collections.ArrayList]::new()
    Add-ManifestFile -List $manifestFiles -Root $sourceRoot -RelativePath 'Runtime\version.bin'
    Add-ManifestFile -List $manifestFiles -Root $sourceRoot -RelativePath 'Tools\Install-ProtonVPNPatch.base.ps1'
    $manifest = [ordered]@{
        schemaVersion = 2
        targetVersion = '5.1.5'
        files = @($manifestFiles)
    }

    $manifestSnapshot = Join-Path $snapshotRoot 'patch-manifest.snapshot.json'
    Write-TestText $manifestSnapshot ($manifest | ConvertTo-Json -Depth 8)

    $installerSnapshot = Join-Path $snapshotRoot 'ProbeInstaller.ps1'
    Write-TestText $installerSnapshot @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PatchPath,
    [Parameter(Mandatory = $true)] [string] $TrustedStagePath,
    [Parameter(Mandatory = $true)] [string] $ProbePath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$adminSid = 'S-1-5-32-544'
$systemSid = 'S-1-5-18'
$allLocked = $true
$allNoReparse = $true
$items = @(
    Get-Item -LiteralPath $TrustedStagePath -Force
    Get-ChildItem -LiteralPath $TrustedStagePath -Recurse -Force
)
foreach ($item in $items) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $allNoReparse = $false
    }
    $acl = Get-Acl -LiteralPath $item.FullName
    if (-not $acl.AreAccessRulesProtected -or
        $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $adminSid) {
        $allLocked = $false
    }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            $rule.IdentityReference.Value -notin @($adminSid, $systemSid)) {
            $allLocked = $false
        }
    }
}

$result = [ordered]@{
    ScriptPath = [IO.Path]::GetFullPath($PSCommandPath)
    TrustedStagePath = [IO.Path]::GetFullPath($TrustedStagePath)
    PatchPath = [IO.Path]::GetFullPath($PatchPath)
    AllLocked = $allLocked
    AllNoReparse = $allNoReparse
    VersionHash = (Get-FileHash -LiteralPath (Join-Path $PatchPath 'Runtime\version.bin') -Algorithm SHA256).Hash.ToLowerInvariant()
    HelperHash = (Get-FileHash -LiteralPath (Join-Path $PatchPath 'Tools\Install-ProtonVPNPatch.base.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $ProbePath -Encoding UTF8
exit 0
'@

    return [pscustomobject]@{
        SourceRoot = $sourceRoot
        SnapshotRoot = $snapshotRoot
        InstallerSnapshot = $installerSnapshot
        InstallerHash = Get-TextHash -Path $installerSnapshot
        ManifestSnapshot = $manifestSnapshot
        ManifestHash = Get-TextHash -Path $manifestSnapshot
        ProbePath = $probePath
        VersionHash = Get-TextHash -Path (Join-Path $sourceRoot 'Runtime\version.bin')
        HelperHash = Get-TextHash -Path (Join-Path $sourceRoot 'Tools\Install-ProtonVPNPatch.base.ps1')
    }
}

function Invoke-BootstrapFixture {
    param(
        [Parameter(Mandatory = $true)] $Fixture,
        [Parameter(Mandatory = $true)] [string] $StageId
    )

    $quotedProbe = '"' + $Fixture.ProbePath + '"'
    $bootstrap = Get-TrustedStageBootstrap `
        -PayloadRoot $Fixture.SourceRoot `
        -InstallerSnapshotPath $Fixture.InstallerSnapshot `
        -InstallerHash $Fixture.InstallerHash `
        -ManifestSnapshotPath $Fixture.ManifestSnapshot `
        -ManifestHash $Fixture.ManifestHash `
        -InstallerFileName 'ProbeInstaller.ps1' `
        -ForwardedArgumentText "-ProbePath $quotedProbe" `
        -StageId $StageId
    $encoded = ConvertTo-CompressedEncodedCommand -ScriptText $bootstrap
    Assert-Condition ($encoded.Length -le 30000) 'Compressed trusted-stage bootstrap exceeded the safe command-line budget.'

    # Several tests intentionally make the bootstrap fail. Windows PowerShell writes an
    # unhandled child exception to stderr, which must be captured as test evidence rather
    # than promoted by this harness's ErrorActionPreference=Stop.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
        StagePath = Get-StagePath -StageId $StageId
    }
}

function Test-CompressedBootstrapExitPropagation {
    $controlEncoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes('[Environment]::Exit(7)'))
    $control = Invoke-TestProcessCapture `
        -FilePath (Get-SystemExecutablePath -RelativePath 'WindowsPowerShell\v1.0\powershell.exe') `
        -Arguments "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $controlEncoded"
    Assert-Condition ($control.ExitCode -eq 7) `
        ("Raw Windows PowerShell process control could not observe Environment.Exit(7); saw {0}." -f $control.ExitCode)

    $encoded = ConvertTo-CompressedEncodedCommand -ScriptText "throw 'compressed-bootstrap-probe'"
    $decoder = Invoke-TestProcessCapture `
        -FilePath (Get-SystemExecutablePath -RelativePath 'WindowsPowerShell\v1.0\powershell.exe') `
        -Arguments "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    Assert-Condition ($decoder.ExitCode -eq 1) `
        ("Compressed bootstrap decoder flattened a terminating failure to exit {0}. Output:`n{1}" -f $decoder.ExitCode, ($decoder.Output -join [Environment]::NewLine))
}

function Test-BootstrapSuccessAndAcl {
    $fixture = New-BootstrapFixture -Root (Join-Path $testRoot 'bootstrap-success')
    $stageId = [Guid]::NewGuid().ToString('N')
    $result = Invoke-BootstrapFixture -Fixture $fixture -StageId $stageId

    Assert-Condition ($result.ExitCode -eq 0) "Trusted staging bootstrap failed: $($result.Output -join [Environment]::NewLine)"
    Assert-Condition (Test-Path -LiteralPath $fixture.ProbePath -PathType Leaf) 'Protected staged installer did not execute.'
    $probe = Get-Content -LiteralPath $fixture.ProbePath -Raw | ConvertFrom-Json
    Assert-Condition ([bool] $probe.AllLocked) 'Trusted stage did not retain Administrator/SYSTEM-only protected ACLs.'
    Assert-Condition ([bool] $probe.AllNoReparse) 'Trusted stage unexpectedly contained a reparse point.'
    Assert-Condition ([string] $probe.VersionHash -eq $fixture.VersionHash) 'Staged version payload hash changed.'
    Assert-Condition ([string] $probe.HelperHash -eq $fixture.HelperHash) 'Staged helper hash changed.'
    Assert-Condition ([string] $probe.ScriptPath -like "$($probe.TrustedStagePath)\*") 'Elevated child did not execute the protected script copy.'
    Assert-Condition (-not (Test-Path -LiteralPath $result.StagePath)) 'Trusted stage was not cleaned after successful execution.'
}

function Test-MutatedPayloadRejected {
    param(
        [Parameter(Mandatory = $true)] [ValidateSet('Runtime', 'Helper')] [string] $Kind
    )

    $fixture = New-BootstrapFixture -Root (Join-Path $testRoot ("mutated-" + $Kind.ToLowerInvariant()))
    if ($Kind -eq 'Runtime') {
        Write-TestText (Join-Path $fixture.SourceRoot 'Runtime\version.bin') 'attacker-replaced-version-bytes'
    } else {
        Write-TestText (Join-Path $fixture.SourceRoot 'Tools\Install-ProtonVPNPatch.base.ps1') 'attacker-replaced-helper-bytes'
    }

    $stageId = [Guid]::NewGuid().ToString('N')
    $result = Invoke-BootstrapFixture -Fixture $fixture -StageId $stageId
    Assert-Condition ($result.ExitCode -ne 0) "Mutated $Kind payload unexpectedly crossed the trusted staging boundary."
    Assert-Condition (-not (Test-Path -LiteralPath $fixture.ProbePath)) "Mutated $Kind payload caused privileged script execution."
    Assert-Condition (-not (Test-Path -LiteralPath $result.StagePath)) "Trusted stage leaked after rejecting mutated $Kind payload."
}

function Test-MutatedInstallerRejected {
    $fixture = New-BootstrapFixture -Root (Join-Path $testRoot 'mutated-installer')
    Write-TestText $fixture.InstallerSnapshot 'Write-Output "attacker installer"'
    $stageId = [Guid]::NewGuid().ToString('N')
    $result = Invoke-BootstrapFixture -Fixture $fixture -StageId $stageId

    Assert-Condition ($result.ExitCode -ne 0) 'Mutated installer snapshot unexpectedly crossed the trusted staging boundary.'
    Assert-Condition (-not (Test-Path -LiteralPath $fixture.ProbePath)) 'Mutated installer snapshot was executed with administrator privileges.'
    Assert-Condition (-not (Test-Path -LiteralPath $result.StagePath)) 'Trusted stage leaked after rejecting mutated installer bytes.'
}

function Test-ReparseSourceRejected {
    $fixture = New-BootstrapFixture -Root (Join-Path $testRoot 'reparse-source')
    $externalRuntime = Join-Path $testRoot 'external-runtime'
    New-Item -ItemType Directory -Force -Path $externalRuntime | Out-Null
    Write-TestText (Join-Path $externalRuntime 'version.bin') 'validated-version-bytes'
    Remove-Item -LiteralPath (Join-Path $fixture.SourceRoot 'Runtime') -Recurse -Force

    $junction = Join-Path $fixture.SourceRoot 'Runtime'
    & cmd.exe /d /c "mklink /J `"$junction`" `"$externalRuntime`"" | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $junction -PathType Container)) {
        throw 'Could not create the deterministic junction used by the trusted-staging reparse regression.'
    }

    $stageId = [Guid]::NewGuid().ToString('N')
    $result = Invoke-BootstrapFixture -Fixture $fixture -StageId $stageId
    Assert-Condition ($result.ExitCode -ne 0) 'Reparse-point payload source unexpectedly crossed the trusted staging boundary.'
    Assert-Condition (-not (Test-Path -LiteralPath $fixture.ProbePath)) 'Reparse-point payload source caused privileged script execution.'
    Assert-Condition (-not (Test-Path -LiteralPath $result.StagePath)) 'Trusted stage leaked after rejecting a reparse-point source.'
}

function Test-ProtectedRollbackIgnoresMutableRetainedBackup {
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
    $fixtureRoot = Join-Path $testRoot 'base-end-to-end'
    $installRoot = Join-Path $fixtureRoot 'install'
    $target = Join-Path $installRoot 'v5.1.5'
    $payload = Join-Path $fixtureRoot 'payload'
    $backupRoot = Join-Path $fixtureRoot 'backups'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    New-Item -ItemType Directory -Force -Path $payload | Out-Null
    Write-TestText (Join-Path $target 'ProtonVPN.Client.dll') 'old-client'
    Write-TestText (Join-Path $payload 'ProtonVPN.Client.dll') 'new-client'

    $files = [Collections.ArrayList]::new()
    Add-ManifestFile -List $files -Root $payload -RelativePath 'ProtonVPN.Client.dll'
    $manifest = [ordered]@{
        schemaVersion = 1
        targetVersion = '5.1.5'
        buildMode = 'client'
        sourceCommit = 'secure-staging-test'
        files = @($files)
    }
    Write-TestText (Join-Path $payload 'patch-manifest.json') ($manifest | ConvertTo-Json -Depth 6)

    $beforeStages = @(Get-CurrentStageDirectories)
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass' +
        ' -File "' + $baseInstallerScript + '"' +
        ' -PatchPath "' + $payload + '"' +
        ' -InstallRoot "' + $installRoot + '"' +
        ' -TargetVersion 5.1.5' +
        ' -BackupRoot "' + $backupRoot + '"' +
        ' -NoRestart'
    $child = Invoke-TestProcessCapture -FilePath (Get-SystemExecutablePath -RelativePath 'WindowsPowerShell\v1.0\powershell.exe') -Arguments $arguments
    $exitCode = $child.ExitCode
    $output = @($child.Output)
    Assert-Condition ($exitCode -eq 0) "Protected standalone FastPatch install failed: $($output -join [Environment]::NewLine)"
    $actualClientText = Get-Content -LiteralPath (Join-Path $target 'ProtonVPN.Client.dll') -Raw
    Assert-Condition ($actualClientText -eq 'new-client') `
        ("Standalone FastPatch did not install from the protected staged payload. Actual: '{0}'. Child output:`n{1}" -f $actualClientText, ($output -join [Environment]::NewLine))
    $afterStages = @(Get-CurrentStageDirectories)
    Assert-Condition ($afterStages.Count -eq $beforeStages.Count) 'Standalone FastPatch leaked a trusted staging directory.'
}

function Test-CompleteLauncherRollbackAfterProtectedStageFailure {
    $fixtureRoot = Join-Path $testRoot 'complete-rollback'
    $installRoot = Join-Path $fixtureRoot 'install'
    $target = Join-Path $installRoot 'v5.1.5'
    $payload = Join-Path $fixtureRoot 'payload'
    $backupRoot = Join-Path $fixtureRoot 'backups'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $payload 'Tools') | Out-Null

    Write-TestText (Join-Path $installRoot 'ProtonVPN.Launcher.exe') 'original-launcher'
    Write-TestText (Join-Path $payload 'ProtonVPN.Launcher.exe') 'patched-launcher'
    Write-TestText (Join-Path $payload 'ProtonVPN.Client.dll') 'new-client'
    Write-TestText (Join-Path $payload 'Tools\Install-ProtonVPNPatch.base.ps1') @'
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $PatchPath,
    [string] $InstallRoot,
    [string] $TargetVersion,
    [string] $BackupRoot,
    [int] $BackupRetentionCount = 3,
    [switch] $NoRestart,
    [switch] $RestartClient,
    [switch] $ValidateOnly,
    [string] $TrustedStagePath = ''
)
if ([string]::IsNullOrWhiteSpace($TrustedStagePath)) { exit 41 }
exit 42
'@

    $files = [Collections.ArrayList]::new()
    Add-ManifestFile -List $files -Root $payload -RelativePath 'ProtonVPN.Launcher.exe' -Scope 'installRoot'
    Add-ManifestFile -List $files -Root $payload -RelativePath 'ProtonVPN.Client.dll' -Scope 'version'
    Add-ManifestFile -List $files -Root $payload -RelativePath 'Tools\Install-ProtonVPNPatch.base.ps1' -Scope 'tool'
    $manifest = [ordered]@{
        schemaVersion = 2
        targetVersion = '5.1.5'
        buildMode = 'client'
        sourceCommit = 'secure-staging-test'
        completeRuntimeCoverage = $true
        launcherIncluded = $true
        upstreamBaseCommit = ('a' * 40)
        files = @($files)
    }
    Write-TestText (Join-Path $payload 'patch-manifest.json') ($manifest | ConvertTo-Json -Depth 8)

    $beforeStages = @(Get-CurrentStageDirectories)
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass' +
        ' -File "' + $completeInstallerScript + '"' +
        ' -PatchPath "' + $payload + '"' +
        ' -InstallRoot "' + $installRoot + '"' +
        ' -TargetVersion 5.1.5' +
        ' -BackupRoot "' + $backupRoot + '"' +
        ' -NoRestart'
    $child = Invoke-TestProcessCapture -FilePath (Get-SystemExecutablePath -RelativePath 'WindowsPowerShell\v1.0\powershell.exe') -Arguments $arguments
    $exitCode = $child.ExitCode
    $output = @($child.Output)

    Assert-Condition ($exitCode -ne 0) ("Synthetic base-installer failure unexpectedly produced a successful complete install. Child output:`n{0}" -f ($output -join [Environment]::NewLine))
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $installRoot 'ProtonVPN.Launcher.exe') -Raw) -eq 'original-launcher') `
        "Complete FastPatch failed to restore the root launcher after staged install failure. Output: $($output -join [Environment]::NewLine)"
    $pending = @(Get-ChildItem -LiteralPath $backupRoot -Directory -Filter '.pending-fastpatch-root-*' -ErrorAction SilentlyContinue)
    Assert-Condition ($pending.Count -eq 0) 'Complete FastPatch left a pending launcher rollback directory after failure.'
    $afterStages = @(Get-CurrentStageDirectories)
    Assert-Condition ($afterStages.Count -eq $beforeStages.Count) 'Complete FastPatch leaked a trusted staging directory after rollback.'
}



function Test-VerifiedArchiveManifestSurvivesExtractionRace {
    $fixtureRoot = Join-Path $testRoot 'verified-archive-manifest-race'
    $source = Join-Path $fixtureRoot 'source'
    $archive = Join-Path $fixtureRoot 'payload.zip'
    $working = Join-Path $fixtureRoot 'working'
    New-Item -ItemType Directory -Force -Path $source | Out-Null
    New-Item -ItemType Directory -Force -Path $working | Out-Null

    Write-TestText (Join-Path $source 'ProtonVPN.Client.dll') 'verified-archive-client'
    $files = [Collections.ArrayList]::new()
    Add-ManifestFile -List $files -Root $source -RelativePath 'ProtonVPN.Client.dll'
    $trustedManifest = [ordered]@{
        schemaVersion = 1
        targetVersion = '5.1.5'
        buildMode = 'client'
        sourceCommit = 'verified-archive-race-test'
        files = @($files)
    }
    Write-TestText (Join-Path $source 'patch-manifest.json') ($trustedManifest | ConvertTo-Json -Depth 6)
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $archive -CompressionLevel Optimal -Force
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()

    $previousPatchPath = $script:PatchPath
    $previousExpectedArchiveHash = $script:ExpectedPatchArchiveSha256
    $previousTrustedManifest = $script:TrustedArchiveManifestText
    try {
        $script:PatchPath = $archive
        $script:ExpectedPatchArchiveSha256 = $archiveHash
        $payloadRoot = Resolve-PatchSource -WorkingDirectory $working
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($script:TrustedArchiveManifestText)) `
            'Hash-pinned archive did not retain its manifest as the validation trust anchor.'

        # Simulate the exact race: after extraction, replace BOTH payload and manifest with a
        # self-consistent malicious pair. Reading the temp manifest would accept this pair.
        Write-TestText (Join-Path $payloadRoot 'ProtonVPN.Client.dll') 'attacker-client'
        $maliciousFiles = [Collections.ArrayList]::new()
        Add-ManifestFile -List $maliciousFiles -Root $payloadRoot -RelativePath 'ProtonVPN.Client.dll'
        $maliciousManifest = [ordered]@{
            schemaVersion = 1
            targetVersion = '5.1.5'
            buildMode = 'client'
            sourceCommit = 'attacker-replacement'
            files = @($maliciousFiles)
        }
        Write-TestText (Join-Path $payloadRoot 'patch-manifest.json') ($maliciousManifest | ConvertTo-Json -Depth 6)

        $rejected = $false
        try {
            Test-PatchPayload -PayloadRoot $payloadRoot -ExpectedTargetVersion '5.1.5' | Out-Null
        } catch {
            if ($_.Exception.Message -match 'size mismatch|hash mismatch') {
                $rejected = $true
            } else {
                throw
            }
        }
        Assert-Condition $rejected `
            'FastPatch accepted a malicious manifest+payload pair substituted after verified ZIP extraction.'
    } finally {
        $script:PatchPath = $previousPatchPath
        $script:ExpectedPatchArchiveSha256 = $previousExpectedArchiveHash
        $script:TrustedArchiveManifestText = $previousTrustedManifest
    }
}

function Test-SfxLoaderRejectsMutatedInstaller {
    $fixtureRoot = Join-Path $testRoot 'sfx-mutated-installer'
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $installer = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.ps1'
    $payload = Join-Path $fixtureRoot 'payload.zip'
    Write-TestText $installer 'param() exit 0'
    [IO.File]::WriteAllBytes($payload, [byte[]]@(1, 2, 3, 4))
    $installerHash = Get-TextHash -Path $installer
    $payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    $loader = Get-SfxLoaderScript `
        -InstallerFileName (Split-Path -Leaf $installer) `
        -InstallerHash $installerHash `
        -PayloadFileName (Split-Path -Leaf $payload) `
        -PayloadHash $payloadHash `
        -TargetVersion '5.1.5'

    Write-TestText $installer 'param() Write-Output "attacker replacement"; exit 0'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($loader))
    $child = Invoke-TestProcessCapture `
        -FilePath (Get-SystemExecutablePath -RelativePath 'WindowsPowerShell\v1.0\powershell.exe') `
        -Arguments "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded" `
        -WorkingDirectory $fixtureRoot
    $exitCode = $child.ExitCode
    $output = @($child.Output)

    Assert-Condition ($exitCode -ne 0) 'Hash-pinned SFX loader executed a mutated extracted installer.'
    $loaderOutputText = $output -join [Environment]::NewLine
    Assert-Condition ($loaderOutputText -match 'installer hash mismatch') `
        ("SFX loader rejected the mutated installer for an unexpected reason. Child output:`n{0}" -f $loaderOutputText)
}

function Test-CompiledSfxVerifiedInMemoryEntry {
    $fixtureRoot = Join-Path $testRoot 'sfx-real-compiled'
    $patch = Join-Path $fixtureRoot 'patch'
    $output = Join-Path $fixtureRoot 'ProtonVPN-Test-Patch.exe'
    $fakeInstaller = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.ps1'
    $fakeLauncher = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.cmd'
    $probe = Join-Path $fixtureRoot 'probe.json'
    $attackerMarker = Join-Path $fixtureRoot 'attacker-marker.txt'
    New-Item -ItemType Directory -Force -Path $patch | Out-Null
    Write-TestText (Join-Path $patch 'ProtonVPN.Client.dll') 'sfx-payload'
    $payloadFile = Get-Item -LiteralPath (Join-Path $patch 'ProtonVPN.Client.dll')
    $manifest = [ordered]@{
        schemaVersion = 1
        targetVersion = '5.1.5'
        buildMode = 'client'
        sourceCommit = 'sfx-entry-smoke'
        files = @([ordered]@{
            path = 'ProtonVPN.Client.dll'
            size = $payloadFile.Length
            sha256 = (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    Write-TestText (Join-Path $patch 'patch-manifest.json') ($manifest | ConvertTo-Json -Depth 6)
    Write-TestText $fakeLauncher '@echo off'
    Write-TestText $fakeInstaller @'
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $PatchPath,
    [string] $TargetVersion,
    [switch] $RestartClient,
    [switch] $PauseBeforeExit,
    [switch] $ValidateOnly,
    [string] $ExpectedPatchArchiveSha256 = ''
)
function Dummy-ElevationWait {
    $process = Start-Process -FilePath 'cmd.exe' `
        -ArgumentList '/c exit 0' `
        -Wait `
        -PassThru
    return $process.ExitCode
}
function Test-SfxWriteDenied {
    param([Parameter(Mandatory = $true)] [string] $Path)
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
        return $false
    } catch [IO.IOException] {
        return $true
    } catch [UnauthorizedAccessException] {
        return $true
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}
function Test-SfxDeleteDenied {
    param([Parameter(Mandatory = $true)] [string] $Path)
    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        return $false
    } catch [IO.IOException] {
        return $true
    } catch [UnauthorizedAccessException] {
        return $true
    }
}
if ($ValidateOnly) { exit 0 }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$resourceInstallerPath = Join-Path (Get-Location).Path 'Install-ProtonVPNPatch.ps1'
$trustedArchiveBytes = [byte[]] (Get-Variable -Name 'ProtonVpnFastPatchVerifiedSfxArchiveBytes' -Scope Global -ErrorAction Stop).Value
$archiveSha = [Security.Cryptography.SHA256]::Create()
try {
    $actualTrustedArchiveHash = ([BitConverter]::ToString($archiveSha.ComputeHash($trustedArchiveBytes))).Replace('-', '').ToLowerInvariant()
} finally {
    $archiveSha.Dispose()
}
$result = [ordered]@{
    IsAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Is64BitProcess = [Environment]::Is64BitProcess
    PSCommandPath = [string] $PSCommandPath
    DefinitionContainsProbe = ([string] $MyInvocation.MyCommand.Definition).Contains('DefinitionContainsProbe')
    PatchPath = [IO.Path]::GetFullPath($PatchPath)
    InstallerResourcePath = [IO.Path]::GetFullPath($resourceInstallerPath)
    ExpectedPatchArchiveSha256 = $ExpectedPatchArchiveSha256
    ActualPatchArchiveSha256 = $actualTrustedArchiveHash
    PayloadWriteDenied = Test-SfxWriteDenied -Path $PatchPath
    PayloadDeleteDenied = Test-SfxDeleteDenied -Path $PatchPath
    InstallerWriteDenied = Test-SfxWriteDenied -Path $resourceInstallerPath
    InstallerDeleteDenied = Test-SfxDeleteDenied -Path $resourceInstallerPath
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $env:PROTONVPN_FASTPATCH_SFX_PROBE -Encoding UTF8
exit 0
'@

    $stagesBefore = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'ProtonVPNFastPatchSfx-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    & $sfxBuilderScript `
        -PatchPath $patch `
        -OutputPath $output `
        -InstallerScriptPath $fakeInstaller `
        -LauncherPath $fakeLauncher
    Assert-Condition (Test-Path -LiteralPath $output -PathType Leaf) 'Compiled FastPatch SFX smoke fixture was not built.'

    Write-TestText $fakeInstaller @'
param([string] $PatchPath, [string] $TargetVersion, [switch] $RestartClient, [switch] $PauseBeforeExit, [switch] $ValidateOnly, [string] $ExpectedPatchArchiveSha256 = '')
Set-Content -LiteralPath $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER -Value 'attacker replacement executed'
exit 0
'@
    Write-TestText (Join-Path $patch 'ProtonVPN.Client.dll') 'attacker-replaced-build-input'

    $previousProbe = $env:PROTONVPN_FASTPATCH_SFX_PROBE
    $previousAttackerMarker = $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER
    try {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $probe
        $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER = $attackerMarker
        $process = Start-Process -FilePath $output -PassThru
        if (-not $process.WaitForExit(90000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw 'Compiled FastPatch SFX smoke run did not terminate within 90 seconds.'
        }
        Assert-Condition ($process.ExitCode -eq 0) "Compiled FastPatch SFX smoke run failed with exit code $($process.ExitCode)."
    } finally {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $previousProbe
        $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER = $previousAttackerMarker
    }

    Assert-Condition (-not (Test-Path -LiteralPath $attackerMarker -PathType Leaf)) `
        'Compiled FastPatch SFX executed the mutable original installer after packaging.'
    Assert-Condition (Test-Path -LiteralPath $probe -PathType Leaf) `
        'Compiled FastPatch SFX did not execute the embedded hash-pinned in-memory installer entry.'
    $result = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    Assert-Condition ([bool] $result.IsAdministrator) 'Compiled SFX smoke installer did not inherit the administrator test token.'
    Assert-Condition ([bool] $result.Is64BitProcess) 'Compiled SFX did not launch native 64-bit Windows PowerShell.'
    Assert-Condition ([string]::IsNullOrWhiteSpace([string] $result.PSCommandPath)) `
        'Compiled SFX directly executed a mutable installer path instead of verified in-memory bytes.'
    Assert-Condition ([bool] $result.DefinitionContainsProbe) `
        'Embedded verified installer bytes were not executed as the expected in-memory script block.'
    Assert-Condition ([string] $result.ExpectedPatchArchiveSha256 -eq [string] $result.ActualPatchArchiveSha256) `
        'Compiled SFX did not bind the verified in-memory payload archive to its build-time SHA-256.'
    Assert-Condition ([bool] $result.PayloadWriteDenied) `
        'Compiled SFX did not keep the extracted payload archive write-locked for the installer lifetime.'
    Assert-Condition ([bool] $result.PayloadDeleteDenied) `
        'Compiled SFX did not keep the extracted payload archive delete-locked for the installer lifetime.'
    Assert-Condition ([bool] $result.InstallerWriteDenied) `
        'Compiled SFX did not keep the extracted installer resource write-locked for the installer lifetime.'
    Assert-Condition ([bool] $result.InstallerDeleteDenied) `
        'Compiled SFX did not keep the extracted installer resource delete-locked for the installer lifetime.'
    Assert-Condition (-not (Test-Path -LiteralPath ([string] $result.PatchPath))) `
        'Compiled SFX left its extracted payload resource behind after the installer exited.'
    Assert-Condition (-not (Test-Path -LiteralPath ([string] $result.InstallerResourcePath))) `
        'Compiled SFX left its extracted installer resource behind after the installer exited.'

    $stagesAfter = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'ProtonVPNFastPatchSfx-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $newStages = @($stagesAfter | Where-Object { $stagesBefore -notcontains $_ })
    Assert-Condition ($newStages.Count -eq 0) `
        ("Compiled SFX leaked runtime staging directories: {0}" -f ($newStages -join ', '))
}

function Test-StaticSecurityContracts {
    $base = Get-Content -LiteralPath $baseInstallerScript -Raw
    $complete = Get-Content -LiteralPath $completeInstallerScript -Raw
    $sfx = Get-Content -LiteralPath $sfxBuilderScript -Raw

    foreach ($item in @(
        [pscustomobject]@{ Name = 'base'; Content = $base },
        [pscustomobject]@{ Name = 'complete'; Content = $complete }
    )) {
        Assert-Condition (-not $item.Content.Contains("'-File', (ConvertTo-QuotedProcessArgument -Value `$PSCommandPath)")) `
            "$($item.Name) installer still reopens its mutable original script path after UAC."
        Assert-Condition ($item.Content.Contains("PSObject.Properties['ScriptContents']")) `
            "$($item.Name) installer does not snapshot the already-parsed external-script bytes."
        Assert-Condition ($item.Content.Contains("'ProtonVpnFastPatchVerifiedSfxScriptText'")) `
            "$($item.Name) installer does not accept the hash-pinned in-memory SFX script snapshot."
        Assert-Condition ($item.Content.Contains('-EncodedCommand')) `
            "$($item.Name) installer does not carry an inline bootstrap across elevation."
        Assert-Condition (-not $item.Content.Contains("Start-Process -FilePath 'powershell.exe'")) `
            "$($item.Name) installer still resolves PowerShell through a mutable executable search path."
        Assert-Condition ($item.Content.Contains('Get-WindowsPowerShellPath')) `
            "$($item.Name) installer does not anchor privileged PowerShell launches to the protected Windows system directory."
        Assert-Condition ($item.Content.Contains('ConvertTo-CompressedEncodedCommand -ScriptText $bootstrap')) `
            "$($item.Name) installer does not compress its inline elevation bootstrap before -EncodedCommand."
        Assert-Condition ($item.Content.Contains('[IO.DirectoryInfo]::new($Path)')) `
            "$($item.Name) installer is missing atomic protected directory creation."
        Assert-Condition ($item.Content.Contains('[IO.FileStream]::new(')) `
            "$($item.Name) installer is missing atomic protected file creation."
        Assert-Condition ($item.Content.Contains('Assert-TrustedStage -StagePath $TrustedStagePath -PayloadPath $PatchPath')) `
            "$($item.Name) installer does not verify the protected stage before privileged use."
    }

    Assert-Condition ($complete.Contains("-InstallerFileName 'Install-ProtonVPNCompletePatch.ps1'")) `
        'Complete FastPatch does not stage its own immutable script snapshot.'
    Assert-Condition ($complete.Contains("-TrustedDestination:(-not `$ValidateOnly)")) `
        'Complete FastPatch version payload is not created as Administrator-owned protected files.'
    Assert-Condition ($complete.Contains("`$arguments += '-TrustedStagePath'")) `
        'Complete FastPatch does not forward the protected-stage trust context to the base helper.'
    Assert-Condition (-not $base.Contains('& robocopy.exe @arguments')) `
        'Base FastPatch still resolves robocopy through a mutable executable search path.'
    Assert-Condition ($base.Contains("Get-SystemExecutablePath -RelativePath 'robocopy.exe'")) `
        'Base FastPatch does not anchor robocopy to the protected Windows system directory.'
    Assert-Condition ($base.Contains('CimCmdlets\Get-CimInstance -ClassName Win32_Service')) `
        'Base FastPatch does not qualify privileged CIM service discovery to the trusted CimCmdlets module.'
    Assert-Condition ($base.Contains("Join-Path `$windowsPowerShellHome 'Modules\CimCmdlets\CimCmdlets.psd1'")) `
        'Base FastPatch does not import CimCmdlets from the protected Windows PowerShell module directory.'
    Assert-Condition ($base.Contains('function Invoke-ProcessAndWait')) `
        'Base FastPatch does not use the raw process-status helper.'
    Assert-Condition ($complete.Contains('function Invoke-ProcessAndWait')) `
        'Complete FastPatch does not use the raw process-status helper.'
    Assert-Condition (-not $base.Contains('Start-Process -FilePath (Get-WindowsPowerShellPath)')) `
        'Base FastPatch still relies on Start-Process for a privilege-critical child status.'
    Assert-Condition (-not $complete.Contains('Start-Process -FilePath (Get-WindowsPowerShellPath)')) `
        'Complete FastPatch still relies on Start-Process for a privilege-critical child status.'
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
        'FastPatch SFX still depends on IExpress extraction-before-execution semantics.'
    Assert-Condition (-not $sfx.Contains('AppLaunched=')) `
        'FastPatch SFX still carries an IExpress AppLaunched trust boundary.'
    Assert-Condition ($sfx.Contains('GetSystemDirectory')) `
        'Compiled SFX does not derive the protected Windows system directory through Win32.'
    Assert-Condition ($sfx.Contains('GetManifestResourceStream')) `
        'Compiled SFX does not load installer, payload, and loader bytes from its own executable resources.'
    Assert-Condition ($sfx.Contains('FileMode.CreateNew')) `
        'Compiled SFX does not create its resource copies without replacing attacker-controlled files.'
    Assert-Condition ($sfx.Contains('FileShare.Read')) `
        'Compiled SFX parent does not deny write/delete sharing while child PowerShell consumes resource copies.'
    Assert-Condition ($sfx.Contains('[IO.FileShare]::ReadWrite')) `
        'Compiled SFX loader cannot read resources while the parent write-lock handle remains open.'
    Assert-Condition ($sfx.Contains('ProtonVpnFastPatchVerifiedSfxArchiveBytes')) `
        'Compiled SFX does not bind verified payload archive bytes into installer process memory.'
    Assert-Condition ($sfx.Contains('Encoding.Unicode.GetBytes(loaderText)')) `
        'Compiled SFX does not encode the verified embedded loader bytes directly for Windows PowerShell.'
    Assert-Condition ($sfx.Contains('-EncodedCommand " + encodedCommand')) `
        'Compiled SFX does not execute the verified embedded loader through direct system PowerShell EncodedCommand.'
    Assert-Condition ($sfx.Contains('encodedCommand.Length > 30000')) `
        'Compiled SFX does not enforce the Windows command-line budget for its in-memory loader.'
    Assert-Condition (-not $sfx.Contains('RedirectStandardInput = true')) `
        'Compiled SFX still relies on the failed PowerShell stdin loader path.'
    foreach ($resourceName in @('FastPatch.Loader', 'FastPatch.Installer', 'FastPatch.Payload')) {
        Assert-Condition ($sfx.Contains($resourceName)) `
            "Compiled SFX does not embed required resource '$resourceName'."
    }
    Assert-Condition ($sfx.Contains('[ScriptBlock]::Create($scriptText)')) `
        'Compiled SFX loader does not execute only the hash-pinned installer bytes in memory.'
    Assert-Condition ($sfx.Contains("-ExpectedPatchArchiveSha256 '__PAYLOAD_HASH__'")) `
        'Compiled SFX loader does not bind payload.zip to its build-time SHA-256.'

    foreach ($content in @($base, $complete)) {
        Assert-Condition ($content.Contains('[IO.FileShare]::Read)')) `
            'FastPatch installer does not hold a no-write/no-delete read-sharing lock while expanding a hash-pinned archive.'
        Assert-Condition ($content.Contains('$ExpectedPatchArchiveSha256')) `
            'FastPatch installer cannot receive the SFX build-time payload archive hash.'
        Assert-Condition ($content.Contains('$script:TrustedArchiveManifestText = $manifestReader.ReadToEnd()')) `
            'FastPatch does not bind manifest validation to the already verified ZIP stream.'
        Assert-Condition ($content.Contains('$script:TrustedArchiveManifestText')) `
            'FastPatch does not retain the verified archive manifest across extraction.'
    }
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    Assert-Condition (Test-IsAdministrator) 'Secure-staging Windows regression requires the GitHub Windows runner to have an administrator token.'

    Test-StaticSecurityContracts
    Test-CompressedBootstrapExitPropagation
    Test-BootstrapSuccessAndAcl
    Test-MutatedPayloadRejected -Kind Runtime
    Test-MutatedPayloadRejected -Kind Helper
    Test-MutatedInstallerRejected
    Test-VerifiedArchiveManifestSurvivesExtractionRace
    Test-SfxLoaderRejectsMutatedInstaller
    Test-CompiledSfxVerifiedInMemoryEntry
    Test-ReparseSourceRejected
    Test-ProtectedRollbackIgnoresMutableRetainedBackup
    Test-BaseInstallerEndToEnd
    Test-CompleteLauncherRollbackAfterProtectedStageFailure

    Write-Host 'FastPatch secure staging regression tests passed.'
    exit 0
} finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
