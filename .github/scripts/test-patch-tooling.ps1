[CmdletBinding()]
param(
    [string] $WorkingDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$applyPatchScript = Join-Path $PSScriptRoot 'apply-future-version-patch.ps1'
$packageScript = Join-Path $PSScriptRoot 'package-patch-artifacts.ps1'
$setVersionScript = Join-Path $PSScriptRoot 'set-assembly-version.ps1'
$stageClientScript = Join-Path $PSScriptRoot 'stage-client-patch-output.ps1'
$installerScript = Join-Path $repositoryRoot 'scripts/Install-ProtonVPNPatch.ps1'
$installerLauncher = Join-Path $repositoryRoot 'scripts/Install-ProtonVPNPatch.cmd'
$sfxBuilderScript = Join-Path $repositoryRoot 'scripts/New-ProtonVPNPatchSfx.ps1'

$ownsWorkingDirectory = [string]::IsNullOrWhiteSpace($WorkingDirectory)
$testRoot = if ($ownsWorkingDirectory) {
    Join-Path ([System.IO.Path]::GetTempPath()) "protonvpn-patch-tooling-$([System.Guid]::NewGuid())"
} else {
    [System.IO.Path]::GetFullPath($WorkingDirectory)
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)]
        [bool] $Condition,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Write-TestText {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Content
    )

    $parent = Split-Path -Path $Path -Parent
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    & git -C $Repository @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $Repository $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Repository,

        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    $output = & git -C $Repository @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git -C $Repository $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }

    return ($output -join "`n").Trim()
}

function Test-VersionStamping {
    $assemblyInfoPath = Join-Path $testRoot 'version/GlobalAssemblyInfo.cs'
    New-Item -ItemType Directory -Force -Path (Split-Path -Path $assemblyInfoPath -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'src/GlobalAssemblyInfo.cs') -Destination $assemblyInfoPath -Force

    & $setVersionScript -TargetVersion '5.1.6' -AssemblyInfoPath $assemblyInfoPath
    $content = Get-Content -LiteralPath $assemblyInfoPath -Raw

    Assert-Condition ($content.Contains('[assembly: AssemblyVersion("5.1.6.0")]')) `
        'Version stamping did not set AssemblyVersion to 5.1.6.0.'
    Assert-Condition ($content.Contains('[assembly: AssemblyFileVersion("5.1.6.0")]')) `
        'Version stamping did not set AssemblyFileVersion to 5.1.6.0.'
    Assert-Condition ($content.Contains('[assembly: AssemblyInformationalVersion("5.1.6.1-marcmy-split-tunnel")]')) `
        'Version stamping discarded the fork informational-version suffix.'
}

function New-PackageFixture {
    $fixtureRoot = Join-Path $testRoot 'package'
    $binDir = Join-Path $fixtureRoot 'bin'
    $clientDir = Join-Path $fixtureRoot 'client'
    $serviceDir = Join-Path $fixtureRoot 'service'
    $toolsDir = Join-Path $fixtureRoot 'tools'

    foreach ($directory in @($binDir, $clientDir, $serviceDir, $toolsDir)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Write-TestText (Join-Path $clientDir 'ProtonVPN.Client.dll') 'client'
    Write-TestText (Join-Path $clientDir 'ProtonVPN.Shared.dll') 'shared-identical'
    Write-TestText (Join-Path $clientDir 'ProtonVPN.Client.pri') 'pri'
    Write-TestText (Join-Path $clientDir 'App.xbf') 'app'
    Write-TestText (Join-Path $clientDir 'MainWindow.xbf') 'main'
    Write-TestText (Join-Path $clientDir 'UI/FeaturePage.xbf') 'feature'

    $serviceAssets = @(
        'ProtonVPNService.dll',
        'ProtonVPN.Vpn.dll',
        'ProtonVPN.Update.dll',
        'ProtonVPN.ProcessCommunication.Service.dll',
        'ProtonVPN.ProTun.dll',
        'ProtonVPN.Native.dll',
        'ProtonVPN.NetworkFilter.dll',
        'ProtonVPN.Common.Installers.dll',
        'ProtonVPN.Shared.dll'
    )

    foreach ($asset in $serviceAssets) {
        $content = if ($asset -eq 'ProtonVPN.Shared.dll') { 'shared-identical' } else { "service-$asset" }
        Write-TestText (Join-Path $serviceDir $asset) $content
    }

    $runtime = [ordered]@{}
    foreach ($asset in $serviceAssets) {
        $runtime[$asset] = [ordered]@{}
    }

    $runtimeTarget = [ordered]@{
        'Fixture.Host/1.0.0' = [ordered]@{
            runtime = $runtime
        }
    }
    $targets = [ordered]@{
        '.NETCoreApp,Version=v8.0/win-x64' = $runtimeTarget
    }
    $dependencies = [ordered]@{
        runtimeTarget = [ordered]@{
            name = '.NETCoreApp,Version=v8.0/win-x64'
        }
        targets = $targets
    }
    Write-TestText `
        (Join-Path $serviceDir 'ProtonVPNService.deps.json') `
        ($dependencies | ConvertTo-Json -Depth 10)

    $builderPath = Join-Path $toolsDir 'builder.ps1'
    Write-TestText $builderPath @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PatchPath,
    [Parameter(Mandatory = $true)] [string] $OutputPath,
    [Parameter(Mandatory = $true)] [string] $InstallerScriptPath,
    [Parameter(Mandatory = $true)] [string] $LauncherPath
)
[System.IO.File]::WriteAllBytes($OutputPath, [byte[]]@(1, 2, 3))
'@

    $installerScriptPath = Join-Path $toolsDir 'installer.ps1'
    $launcherPath = Join-Path $toolsDir 'launcher.cmd'
    Write-TestText $installerScriptPath 'param()'
    Write-TestText $launcherPath '@echo off'

    return [ordered]@{
        Root = $fixtureRoot
        Bin = $binDir
        Client = $clientDir
        Service = $serviceDir
        Builder = $builderPath
        InstallerScript = $installerScriptPath
        Launcher = $launcherPath
        ServiceAssets = $serviceAssets
    }
}

function Invoke-PackageFixture {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $Fixture,

        [Parameter(Mandatory = $true)]
        [string] $OutputSuffix
    )

    $patchDir = Join-Path $Fixture.Root "patch-$OutputSuffix"
    $installerDir = Join-Path $Fixture.Root "installer-$OutputSuffix"

    & $packageScript `
        -BuildMode both `
        -TargetVersion '5.1.5' `
        -SourceCommit '0123456789abcdef' `
        -SourceRef 'test/full-fork' `
        -WorkflowRunId '1234' `
        -BinDirectory $Fixture.Bin `
        -ServiceOutputDirectory $Fixture.Service `
        -ClientOutputDirectory $Fixture.Client `
        -PatchDirectory $patchDir `
        -InstallerDirectory $installerDir `
        -BuilderPath $Fixture.Builder `
        -InstallerScriptPath $Fixture.InstallerScript `
        -LauncherPath $Fixture.Launcher

    return $patchDir
}

function Test-PackageComposition {
    $fixture = New-PackageFixture

    $installerSource = Get-Content -LiteralPath $installerScript -Raw
    $stagingGuardIndex = $installerSource.IndexOf(
        'if (-not $ValidateOnly -and [string]::IsNullOrWhiteSpace($TrustedStagePath)) {',
        [StringComparison]::Ordinal)
    $preflightValidationIndex = $installerSource.IndexOf(
        'Test-PatchPayload',
        $stagingGuardIndex,
        [StringComparison]::Ordinal)
    $stagingIndex = $installerSource.IndexOf(
        'Invoke-TrustedStage',
        $preflightValidationIndex,
        [StringComparison]::Ordinal)

    Assert-Condition (
        $stagingGuardIndex -ge 0 -and
        $preflightValidationIndex -gt $stagingGuardIndex -and
        $stagingIndex -gt $preflightValidationIndex
    ) 'Installer payload validation must occur before protected privileged staging.'

    $unsafeStageRejected = $false
    try {
        & $stageClientScript `
            -ClientOutputDirectory $fixture.Client `
            -StageDirectory $fixture.Client
    }
    catch {
        if ($_.Exception.Message -like '*must not overlap client build output*') {
            $unsafeStageRejected = $true
        } else {
            throw
        }
    }

    Assert-Condition $unsafeStageRejected `
        'Client staging did not reject a cleanup directory that overlapped its source.'

    $unsafePackageRejected = $false
    try {
        & $packageScript `
            -BuildMode both `
            -TargetVersion '5.1.5' `
            -SourceCommit '0123456789abcdef' `
            -SourceRef 'test/full-fork' `
            -WorkflowRunId '1234' `
            -BinDirectory $fixture.Bin `
            -ServiceOutputDirectory $fixture.Service `
            -ClientOutputDirectory $fixture.Client `
            -PatchDirectory $fixture.Client `
            -InstallerDirectory (Join-Path $fixture.Root 'unsafe-installer') `
            -BuilderPath $fixture.Builder `
            -InstallerScriptPath $fixture.InstallerScript `
            -LauncherPath $fixture.Launcher
    }
    catch {
        if ($_.Exception.Message -like '*must not overlap protected input path*') {
            $unsafePackageRejected = $true
        } else {
            throw
        }
    }

    Assert-Condition $unsafePackageRejected `
        'Patch packaging did not reject a cleanup directory that overlapped an input.'

    $patchDir = Invoke-PackageFixture -Fixture $fixture -OutputSuffix 'complete'
    $manifest = Get-Content -LiteralPath (Join-Path $patchDir 'patch-manifest.json') -Raw | ConvertFrom-Json
    $manifestPaths = @($manifest.files | ForEach-Object { [string] $_.path })

    foreach ($asset in $fixture.ServiceAssets) {
        Assert-Condition ($manifestPaths -contains $asset) `
            "Packaged manifest omitted service runtime assembly: $asset"
    }

    Assert-Condition ([int] $manifest.serviceRuntimeAssemblyCount -eq $fixture.ServiceAssets.Count) `
        'Packaged manifest reported the wrong service runtime assembly count.'

    & powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $installerScript `
        -PatchPath $patchDir `
        -TargetVersion '5.1.5' `
        -ValidateOnly
    Assert-Condition ($LASTEXITCODE -eq 0) `
        'Installer rejected an untampered patch payload.'

    $realInstallerPath = Join-Path $fixture.Root 'real-installer/ProtonVPN-Custom-Patch-5.1.5.exe'
    & $sfxBuilderScript `
        -PatchPath $patchDir `
        -OutputPath $realInstallerPath `
        -InstallerScriptPath $installerScript `
        -LauncherPath $installerLauncher
$realInstallerBytes = [System.IO.File]::ReadAllBytes($realInstallerPath)
Assert-Condition (
    $realInstallerBytes.Length -gt 2 -and
    $realInstallerBytes[0] -eq 0x4D -and
    $realInstallerBytes[1] -eq 0x5A
) 'Compiled FastPatch packaging did not return a valid PE executable.'
$realInstallerAscii = [Text.Encoding]::ASCII.GetString($realInstallerBytes)
foreach ($resourceName in @('FastPatch.Loader', 'FastPatch.Installer', 'FastPatch.Payload')) {
    Assert-Condition ($realInstallerAscii.Contains($resourceName)) `
        "Compiled FastPatch executable omitted embedded resource: $resourceName"
}

    $runtimeDataPatchDir = Join-Path $fixture.Root 'patch-runtime-data'
    Copy-Item -LiteralPath $patchDir -Destination $runtimeDataPatchDir -Recurse
    $runtimeDataPath = Join-Path $runtimeDataPatchDir 'ServiceData/ServiceSettings.json'
    Write-TestText $runtimeDataPath 'must-not-be-patched'
    $runtimeDataManifestPath = Join-Path $runtimeDataPatchDir 'patch-manifest.json'
    $runtimeDataManifest = Get-Content -LiteralPath $runtimeDataManifestPath -Raw | ConvertFrom-Json
    $runtimeDataManifest.files = @($runtimeDataManifest.files) + [pscustomobject]@{
        path = 'ServiceData\ServiceSettings.json'
        size = (Get-Item -LiteralPath $runtimeDataPath).Length
        sha256 = (Get-FileHash -LiteralPath $runtimeDataPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $runtimeDataManifest |
        ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $runtimeDataManifestPath -Encoding utf8

    $runtimeDataValidationOutput = @(& powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $installerScript `
        -PatchPath $runtimeDataPatchDir `
        -TargetVersion '5.1.5' `
        -ValidateOnly 2>&1)
    $runtimeDataValidationExitCode = $LASTEXITCODE
    Assert-Condition ($runtimeDataValidationExitCode -ne 0) `
        'Installer accepted a patch payload that targets runtime ServiceData.'
    Assert-Condition (
        ($runtimeDataValidationOutput -join "`n") -match
            'Patch payload must not modify\s+runtime\s+ServiceData'
    ) 'Installer rejected a ServiceData payload for an unexpected reason.'

    Write-TestText (Join-Path $patchDir 'ProtonVPN.Client.dll') 'tampered-client'
    & powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $installerScript `
        -PatchPath $patchDir `
        -TargetVersion '5.1.5' `
        -ValidateOnly 2>$null
    Assert-Condition ($LASTEXITCODE -ne 0) `
        'Installer accepted a patch payload whose manifest hash no longer matched.'

    Write-TestText (Join-Path $fixture.Service 'ProtonVPN.Shared.dll') 'service-collision'
    $collisionDetected = $false
    try {
        Invoke-PackageFixture -Fixture $fixture -OutputSuffix 'collision' | Out-Null
    }
    catch {
        if ($_.Exception.Message -like "*Patch payload collision for 'ProtonVPN.Shared.dll'*") {
            $collisionDetected = $true
        } else {
            throw
        }
    }

    Assert-Condition $collisionDetected `
        'Packaging did not reject conflicting client and service assemblies with the same install path.'
}

function Test-InstallerRuntimeDataPreservation {
    $fixtureRoot = Join-Path $testRoot 'installer-runtime-data'
    $targetDirectory = Join-Path $fixtureRoot 'v5.1.5'
    $backupDirectory = Join-Path $fixtureRoot 'v5.1.5-backup'
    $serviceDataDirectory = Join-Path $targetDirectory 'ServiceData'
    $wireGuardDirectory = Join-Path $serviceDataDirectory 'WireGuard'

    Write-TestText (Join-Path $targetDirectory 'ProtonVPN.Client.dll') 'official-client'
    Write-TestText (Join-Path $serviceDataDirectory 'ServiceSettings.json') 'live-settings'
    Write-TestText (Join-Path $wireGuardDirectory 'ProtonVPN.conf') 'private-runtime-state'

    $tokens = $null
    $parseErrors = $null
    $installerAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $installerScript,
        [ref] $tokens,
        [ref] $parseErrors)
    Assert-Condition ($parseErrors.Count -eq 0) `
        'Installer script could not be parsed for runtime-data preservation testing.'

    $systemExecutableFunctionAst = $installerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-SystemExecutablePath'
    }, $true)
    Assert-Condition ($null -ne $systemExecutableFunctionAst) `
        'Installer does not define Get-SystemExecutablePath.'
    . ([ScriptBlock]::Create($systemExecutableFunctionAst.Extent.Text))

    $robocopyFunctionAst = $installerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Invoke-Robocopy'
    }, $true)
    Assert-Condition ($null -ne $robocopyFunctionAst) `
        'Installer does not define Invoke-Robocopy.'
    . ([ScriptBlock]::Create($robocopyFunctionAst.Extent.Text))

    $mirrorInvocations = @($installerAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq 'Invoke-Robocopy' -and
            $node.Extent.Text -match '(?m)-Mirror(?:\s|$)'
    }, $true))
Assert-Condition ($mirrorInvocations.Count -eq 3) `
'Installer must have one protected snapshot, one archival backup copy, and one protected rollback mirror.'
$serviceDataPreservingMirrors = @(
$mirrorInvocations | Where-Object {
    $_.Extent.Text.Contains("-ExcludedDirectories @('ServiceData')")
}
)
Assert-Condition ($serviceDataPreservingMirrors.Count -eq 2) `
'Protected rollback snapshot and restore must both preserve runtime ServiceData.'
$archiveMirrors = @(
$mirrorInvocations | Where-Object {
    $_.Extent.Text.Contains('-Source $trustedRollbackDirectory') -and
        $_.Extent.Text.Contains('-Destination $backupDirectory')
}
)
Assert-Condition ($archiveMirrors.Count -eq 1) `
'Installer must retain exactly one archival backup from the protected rollback snapshot.'

    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
    Invoke-Robocopy `
        -Source $targetDirectory `
        -Destination $backupDirectory `
        -Mirror `
        -ExcludedDirectories @('ServiceData')

    Assert-Condition (Test-Path -LiteralPath (Join-Path $backupDirectory 'ProtonVPN.Client.dll')) `
        'Installer backup omitted an installed program file.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $backupDirectory 'ServiceData'))) `
        'Installer backup copied runtime ServiceData.'

    Write-TestText (Join-Path $targetDirectory 'ProtonVPN.Client.dll') 'patched-client'
    Write-TestText (Join-Path $targetDirectory 'CustomPatchOnly.dll') 'patch-only'
    Write-TestText (Join-Path $serviceDataDirectory 'ServiceSettings.json') 'updated-live-settings'

    Invoke-Robocopy `
        -Source $backupDirectory `
        -Destination $targetDirectory `
        -Mirror `
        -ExcludedDirectories @('ServiceData')

    Assert-Condition (
        (Get-Content -LiteralPath (Join-Path $targetDirectory 'ProtonVPN.Client.dll') -Raw) -eq
            'official-client'
    ) 'Installer rollback did not restore an installed program file.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $targetDirectory 'CustomPatchOnly.dll'))) `
        'Installer rollback retained a patch-only program file.'
    Assert-Condition (
        (Get-Content -LiteralPath (Join-Path $serviceDataDirectory 'ServiceSettings.json') -Raw) -eq
            'updated-live-settings'
    ) 'Installer rollback changed live ServiceData.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $wireGuardDirectory 'ProtonVPN.conf')) `
        'Installer rollback removed the WireGuard runtime configuration.'
}

function Test-CompleteForkPort {
    $fixtureRoot = Join-Path $testRoot 'future-port'
    $workingRepo = Join-Path $fixtureRoot 'working'
    $remoteRepo = Join-Path $fixtureRoot 'origin.git'

    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    & git init --initial-branch=master $workingRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize future-port working repository.'
    }
    Invoke-Git $workingRepo config user.name 'Patch Tooling Tests'
    Invoke-Git $workingRepo config user.email 'patch-tooling@example.invalid'

    $sharedMiddle = (1..10 | ForEach-Object { "unchanged=$_" }) -join "`n"
    Write-TestText (Join-Path $workingRepo 'shared.txt') "upstream=old`n$sharedMiddle`nfork=old`n"
    Write-TestText (Join-Path $workingRepo 'remove-on-fork.txt') "remove me`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'old upstream release'

    & git init --bare $remoteRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize future-port origin repository.'
    }
    Invoke-Git $workingRepo remote add origin $remoteRepo
    Invoke-Git $workingRepo push -u origin master

    Invoke-Git $workingRepo switch -c 'marc/proton'
    Write-TestText (Join-Path $workingRepo 'shared.txt') "upstream=old`n$sharedMiddle`nfork=complete`n"
    Write-TestText (Join-Path $workingRepo 'src/ProtonVPN.Vpn/PortMapping/NatPmpFeature.cs') 'nat-pmp'
    Write-TestText (Join-Path $workingRepo 'src/ProtonVPN.Service/SplitTunneling/SplitFeature.cs') 'split-tunnel'
    Write-TestText (Join-Path $workingRepo 'src/Client/ServerHealth/ServerHealthFeature.cs') 'server-health'
    Write-TestText (Join-Path $workingRepo '.github/scripts/custom-automation.ps1') 'automation'
    New-Item -ItemType Directory -Force -Path (Join-Path $workingRepo 'assets') | Out-Null
    [System.IO.File]::WriteAllBytes(
        (Join-Path $workingRepo 'assets/fork-feature.bin'),
        [byte[]]@(0, 1, 2, 3, 255))
    Remove-Item -LiteralPath (Join-Path $workingRepo 'remove-on-fork.txt') -Force
    Invoke-Git $workingRepo add --all
    Invoke-Git $workingRepo commit -m 'complete maintained fork'
    Invoke-Git $workingRepo push -u origin 'marc/proton'

    Invoke-Git $workingRepo switch master
    Write-TestText (Join-Path $workingRepo 'shared.txt') "upstream=future`n$sharedMiddle`nfork=old`n"
    Write-TestText (Join-Path $workingRepo 'future-upstream.txt') 'future-release'
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'future upstream release'
    Invoke-Git $workingRepo push

    $outputPath = Join-Path $fixtureRoot 'github-output.txt'
    $previousGitHubOutput = $env:GITHUB_OUTPUT
    try {
        $env:GITHUB_OUTPUT = $outputPath
        Push-Location $workingRepo
        try {
            & $applyPatchScript `
                -BaseBranch master `
                -SourcePatchBranch 'marc/proton' `
                -TargetBranch 'codex/future'
        }
        finally {
            Pop-Location
        }
    }
    finally {
        $env:GITHUB_OUTPUT = $previousGitHubOutput
    }

    Assert-Condition ((Get-GitOutput $workingRepo branch --show-current) -eq 'codex/future') `
        'Future-port automation did not create the requested target branch.'

    $sharedContent = Get-Content -LiteralPath (Join-Path $workingRepo 'shared.txt') -Raw
    Assert-Condition ($sharedContent.Contains('upstream=future')) `
        'Future-port automation discarded the future upstream change.'
    Assert-Condition ($sharedContent.Contains('fork=complete')) `
        'Future-port automation discarded the maintained fork change.'

    $requiredForkPaths = @(
        'src/ProtonVPN.Vpn/PortMapping/NatPmpFeature.cs',
        'src/ProtonVPN.Service/SplitTunneling/SplitFeature.cs',
        'src/Client/ServerHealth/ServerHealthFeature.cs',
        '.github/scripts/custom-automation.ps1',
        'assets/fork-feature.bin'
    )
    foreach ($path in $requiredForkPaths) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $workingRepo $path) -PathType Leaf) `
            "Future-port automation omitted fork path: $path"
    }

    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $workingRepo 'remove-on-fork.txt'))) `
        'Future-port automation did not carry a fork deletion.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $workingRepo 'future-upstream.txt') -PathType Leaf) `
        'Future-port automation discarded a file from the future upstream base.'

    $outputs = Get-Content -LiteralPath $outputPath -Raw
    Assert-Condition ($outputs -match '(?m)^fork_patch_commit=[0-9a-f]{40}\r?$') `
        'Future-port automation did not publish the complete fork patch commit output.'
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
    Test-VersionStamping
    Test-PackageComposition
    Test-InstallerRuntimeDataPreservation
    Test-CompleteForkPort
    Write-Host 'Patch tooling regression tests passed.'
}
finally {
    if ($ownsWorkingDirectory -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
