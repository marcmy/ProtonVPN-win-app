[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$baseInstallerScript = Join-Path $repositoryRoot 'scripts\Install-ProtonVPNPatch.ps1'
$completeInstallerScript = Join-Path $repositoryRoot 'scripts\Install-ProtonVPNCompletePatch.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("protonvpn-fastpatch-secure-stage-{0}" -f [Guid]::NewGuid().ToString('N'))

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
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

    Invoke-Expression $functionAst.Extent.Text
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
Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'

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
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))

    $output = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)
    $exitCode = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
        StagePath = Get-StagePath -StageId $StageId
    }
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
    $output = @(& powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $baseInstallerScript `
        -PatchPath $payload `
        -InstallRoot $installRoot `
        -TargetVersion '5.1.5' `
        -BackupRoot $backupRoot `
        -NoRestart 2>&1)
    $exitCode = $LASTEXITCODE
    Assert-Condition ($exitCode -eq 0) "Protected standalone FastPatch install failed: $($output -join [Environment]::NewLine)"
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $target 'ProtonVPN.Client.dll') -Raw) -eq 'new-client') `
        'Standalone FastPatch did not install from the protected staged payload.'
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
    $output = @(& powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $completeInstallerScript `
        -PatchPath $payload `
        -InstallRoot $installRoot `
        -TargetVersion '5.1.5' `
        -BackupRoot $backupRoot `
        -NoRestart 2>&1)
    $exitCode = $LASTEXITCODE

    Assert-Condition ($exitCode -ne 0) 'Synthetic base-installer failure unexpectedly produced a successful complete install.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $installRoot 'ProtonVPN.Launcher.exe') -Raw) -eq 'original-launcher') `
        "Complete FastPatch failed to restore the root launcher after staged install failure. Output: $($output -join [Environment]::NewLine)"
    $pending = @(Get-ChildItem -LiteralPath $backupRoot -Directory -Filter '.pending-fastpatch-root-*' -ErrorAction SilentlyContinue)
    Assert-Condition ($pending.Count -eq 0) 'Complete FastPatch left a pending launcher rollback directory after failure.'
    $afterStages = @(Get-CurrentStageDirectories)
    Assert-Condition ($afterStages.Count -eq $beforeStages.Count) 'Complete FastPatch leaked a trusted staging directory after rollback.'
}

function Test-StaticSecurityContracts {
    $base = Get-Content -LiteralPath $baseInstallerScript -Raw
    $complete = Get-Content -LiteralPath $completeInstallerScript -Raw

    foreach ($item in @(
        [pscustomobject]@{ Name = 'base'; Content = $base },
        [pscustomobject]@{ Name = 'complete'; Content = $complete }
    )) {
        Assert-Condition (-not $item.Content.Contains("'-File', (ConvertTo-QuotedProcessArgument -Value `$PSCommandPath)")) `
            "$($item.Name) installer still reopens its mutable original script path after UAC."
        Assert-Condition ($item.Content.Contains('$MyInvocation.MyCommand.ScriptContents')) `
            "$($item.Name) installer does not snapshot the already-parsed script bytes."
        Assert-Condition ($item.Content.Contains('-EncodedCommand')) `
            "$($item.Name) installer does not carry an inline bootstrap across elevation."
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
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    Assert-Condition (Test-IsAdministrator) 'Secure-staging Windows regression requires the GitHub Windows runner to have an administrator token.'

    Test-StaticSecurityContracts
    Test-BootstrapSuccessAndAcl
    Test-MutatedPayloadRejected -Kind Runtime
    Test-MutatedPayloadRejected -Kind Helper
    Test-MutatedInstallerRejected
    Test-ReparseSourceRejected
    Test-BaseInstallerEndToEnd
    Test-CompleteLauncherRollbackAfterProtectedStageFailure

    Write-Host 'FastPatch secure staging regression tests passed.'
    exit 0
} finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
