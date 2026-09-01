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
$sfxBuilderScript = Join-Path $repositoryRoot 'scripts\New-ProtonVPNPatchSfx.ps1'
$installerLauncher = Join-Path $repositoryRoot 'scripts\Install-ProtonVPNPatch.cmd'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ("protonvpn-fastpatch-secure-stage-{0}" -f [Guid]::NewGuid().ToString('N'))

# Production functions imported below normally receive these from the installer param block.
# The regression imports the functions without executing that block, so initialize the same
# script-scope state explicitly.
$script:PatchPath = ''
$script:ExpectedPatchArchiveSha256 = ''
$script:TrustedArchiveManifestText = ''
$script:ValidatedManifestText = ''

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
    Push-Location $fixtureRoot
    try {
        $output = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    Assert-Condition ($exitCode -ne 0) 'Hash-pinned SFX loader executed a mutated extracted installer.'
    $loaderOutputText = $output -join [Environment]::NewLine
    Assert-Condition ($loaderOutputText -match 'installer hash mismatch') `
        ("SFX loader rejected the mutated installer for an unexpected reason. Child output:`n{0}" -f $loaderOutputText)
}

function Test-IExpressVerifiedInMemoryEntry {
    $fixtureRoot = Join-Path $testRoot 'sfx-real-iexpress'
    $patch = Join-Path $fixtureRoot 'patch'
    $output = Join-Path $fixtureRoot 'ProtonVPN-Test-Patch.exe'
    $fakeInstaller = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.ps1'
    $fakeLauncher = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.cmd'
    $probe = Join-Path $fixtureRoot 'probe.json'
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
# This dormant function deliberately preserves the packaged-installer process-wait rewrite seam.
function Dummy-ElevationWait {
    $process = Start-Process -FilePath 'cmd.exe' `
        -ArgumentList '/c exit 0' `
        -Wait `
        -PassThru
    return $process.ExitCode
}
if ($ValidateOnly) { exit 0 }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$result = [ordered]@{
    IsAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Is64BitProcess = [Environment]::Is64BitProcess
    PSCommandPath = [string] $PSCommandPath
    DefinitionContainsProbe = ([string] $MyInvocation.MyCommand.Definition).Contains('DefinitionContainsProbe')
    PatchPath = [IO.Path]::GetFullPath($PatchPath)
    ExpectedPatchArchiveSha256 = $ExpectedPatchArchiveSha256
    ActualPatchArchiveSha256 = (Get-FileHash -LiteralPath $PatchPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $env:PROTONVPN_FASTPATCH_SFX_PROBE -Encoding UTF8
exit 0
'@

    & $sfxBuilderScript `
        -PatchPath $patch `
        -OutputPath $output `
        -InstallerScriptPath $fakeInstaller `
        -LauncherPath $fakeLauncher
    Assert-Condition (Test-Path -LiteralPath $output -PathType Leaf) 'IExpress SFX smoke fixture was not built.'

    $previousProbe = $env:PROTONVPN_FASTPATCH_SFX_PROBE
    $previousSystemRoot = $env:SystemRoot
    $fakeSystemRoot = Join-Path $fixtureRoot 'attacker-system-root'
    $fakePowerShellDirectory = Join-Path $fakeSystemRoot 'System32\WindowsPowerShell\v1.0'
    New-Item -ItemType Directory -Force -Path $fakePowerShellDirectory | Out-Null
    # If the SFX still expands %SystemRoot%, this renamed cmd.exe receives the PowerShell
    # arguments and the smoke probe never gets written. GLOBALROOT must bypass it entirely.
    Copy-Item `
        -LiteralPath (Join-Path $previousSystemRoot 'System32\cmd.exe') `
        -Destination (Join-Path $fakePowerShellDirectory 'powershell.exe') `
        -Force
    try {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $probe
        $env:SystemRoot = $fakeSystemRoot
        $process = Start-Process -FilePath $output -PassThru
        if (-not $process.WaitForExit(90000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw 'IExpress SFX smoke run did not terminate within 90 seconds.'
        }
        Assert-Condition ($process.ExitCode -eq 0) "IExpress SFX smoke run failed with exit code $($process.ExitCode)."
    } finally {
        $env:SystemRoot = $previousSystemRoot
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $previousProbe
    }

    Assert-Condition (Test-Path -LiteralPath $probe -PathType Leaf) 'IExpress did not execute the hash-pinned in-memory installer entry.'
    $result = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    Assert-Condition ([bool] $result.IsAdministrator) 'IExpress smoke installer did not inherit the administrator test token.'
    Assert-Condition ([bool] $result.Is64BitProcess) 'IExpress did not launch native 64-bit Windows PowerShell.'
    Assert-Condition ([string]::IsNullOrWhiteSpace([string] $result.PSCommandPath)) `
        'IExpress directly executed the extracted mutable installer path instead of verified in-memory bytes.'
    Assert-Condition ([bool] $result.DefinitionContainsProbe) 'Verified installer bytes were not executed as the expected in-memory script block.'
    Assert-Condition ([string] $result.ExpectedPatchArchiveSha256 -eq [string] $result.ActualPatchArchiveSha256) `
        'IExpress did not bind the extracted payload archive to its build-time SHA-256.'
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

    Assert-Condition (-not $sfx.Contains('AppLaunched=$launcherFileName')) `
        'IExpress still launches the extracted mutable CMD file directly.'
    Assert-Condition ($sfx.Contains('AppLaunched=$sfxLaunchCommand')) `
        'IExpress is not anchored to the encoded system-PowerShell verifier.'
    Assert-Condition (-not $sfx.Contains('%SystemRoot%\System32\WindowsPowerShell')) `
        'IExpress verifier still trusts the user-overridable SystemRoot environment variable.'
    Assert-Condition ($sfx.Contains('\\?\GLOBALROOT\SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe')) `
        'IExpress verifier is not anchored to the immutable GLOBALROOT system PowerShell path.'
    Assert-Condition ($sfx.Contains('[ScriptBlock]::Create($scriptText)')) `
        'IExpress verifier does not execute only the hash-pinned installer bytes in memory.'
    Assert-Condition ($sfx.Contains("-ExpectedPatchArchiveSha256 '__PAYLOAD_HASH__'")) `
        'IExpress verifier does not bind payload.zip to its build-time SHA-256.'
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
    Test-BootstrapSuccessAndAcl
    Test-MutatedPayloadRejected -Kind Runtime
    Test-MutatedPayloadRejected -Kind Helper
    Test-MutatedInstallerRejected
    Test-VerifiedArchiveManifestSurvivesExtractionRace
    Test-SfxLoaderRejectsMutatedInstaller
    Test-IExpressVerifiedInMemoryEntry
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
