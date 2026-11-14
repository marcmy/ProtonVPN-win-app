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

    & $setVersionScript -TargetVersion '5.1.9' -AssemblyInfoPath $assemblyInfoPath
    $content = Get-Content -LiteralPath $assemblyInfoPath -Raw

    Assert-Condition ($content.Contains('[assembly: AssemblyVersion("5.1.9.0")]')) `
        'Version stamping did not set AssemblyVersion to 5.1.9.0.'
    Assert-Condition ($content.Contains('[assembly: AssemblyFileVersion("5.1.9.0")]')) `
        'Version stamping did not set AssemblyFileVersion to 5.1.9.0.'
    Assert-Condition ($content.Contains('[assembly: AssemblyInformationalVersion("5.1.9-marc-custom")]')) `
        'Version stamping did not set the fork display version.'
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
    Write-TestText (Join-Path $clientDir 'ProtonVPN.Builds.Variables.dll') 'public-source-empty-release-variables'
    Write-TestText (Join-Path $clientDir 'ProtonVPN.Shared.dll') 'shared-identical'
    Write-TestText (Join-Path $clientDir 'ProtonVPN.Client.pri') 'pri'
    Write-TestText (Join-Path $clientDir 'App.xbf') 'app'
    Write-TestText (Join-Path $clientDir 'MainWindow.xbf') 'main'
    Write-TestText (Join-Path $clientDir 'UI/FeaturePage.xbf') 'feature'

    $serviceAssets = @(
        'ProtonVPNService.dll',
        'ProtonVPN.Builds.Variables.dll',
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
        -TargetVersion '5.1.8' `
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

    $safeStageDirectory = Join-Path $fixture.Root 'client-stage'
    & $stageClientScript `
        -ClientOutputDirectory $fixture.Client `
        -StageDirectory $safeStageDirectory
    Assert-Condition (Test-Path -LiteralPath (Join-Path $safeStageDirectory 'ProtonVPN.Client.dll') -PathType Leaf) `
        'Client staging omitted ProtonVPN.Client.dll.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $safeStageDirectory 'ProtonVPN.Builds.Variables.dll'))) `
        'Client staging overwrote release-injected ProtonVPN.Builds.Variables.dll.'

    $unsafePackageRejected = $false
    try {
        & $packageScript `
            -BuildMode both `
            -TargetVersion '5.1.8' `
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

    $expectedServiceAssets = @(
        $fixture.ServiceAssets | Where-Object { $_ -ne 'ProtonVPN.Builds.Variables.dll' }
    )

    foreach ($asset in $expectedServiceAssets) {
        Assert-Condition ($manifestPaths -contains $asset) `
            "Packaged manifest omitted service runtime assembly: $asset"
    }

    Assert-Condition ($manifestPaths -notcontains 'ProtonVPN.Builds.Variables.dll') `
        'Packaged manifest overwrote release-injected ProtonVPN.Builds.Variables.dll.'

    Assert-Condition ([int] $manifest.serviceRuntimeAssemblyCount -eq $expectedServiceAssets.Count) `
        'Packaged manifest reported the wrong service runtime assembly count.'

    & powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $installerScript `
        -PatchPath $patchDir `
        -TargetVersion '5.1.8' `
        -ValidateOnly
    Assert-Condition ($LASTEXITCODE -eq 0) `
        'Installer rejected an untampered patch payload.'

    $realInstallerPath = Join-Path $fixture.Root 'real-installer/ProtonVPN-Custom-Patch-5.1.8.exe'
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
        -TargetVersion '5.1.8' `
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
        -TargetVersion '5.1.8' `
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
    $targetDirectory = Join-Path $fixtureRoot 'v5.1.8'
    $backupDirectory = Join-Path $fixtureRoot 'v5.1.8-backup'
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
    $renamedMiddle = (1..10 | ForEach-Object { "rename-unchanged=$_" }) -join "`n"
    $equivalentBase = "public class Message`n{`n    public int Status { get; }`n}`n"
    $equivalentFork = "public class Message`n{`n    public int Status { get; }`n    public bool HasStatusChanged { get; }`n    public bool HasIntentChanged { get; }`n}`n"
    $equivalentTarget = "public class Message`n{`n    public int Status { get; }`n`n    public bool HasStatusChanged { get; }`n    public bool HasIntentChanged { get; }`n}`n"
    Write-TestText (Join-Path $workingRepo 'shared.txt') "upstream=old`n$sharedMiddle`nfork=old`n"
    Write-TestText (Join-Path $workingRepo 'rename-me.txt') "upstream=old`n$renamedMiddle`nfork=old`n"
    Write-TestText (Join-Path $workingRepo 'overlap.txt') "policy=old`n"
    Write-TestText (Join-Path $workingRepo 'deleted-upstream.txt') "value=old`n"
    Write-TestText (Join-Path $workingRepo 'fork-whitespace.txt') "value=old`n"
    Write-TestText (Join-Path $workingRepo 'equivalent-backport.cs') $equivalentBase
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
    Write-TestText (Join-Path $workingRepo 'rename-me.txt') "upstream=old`n$renamedMiddle`nfork=complete`n"
    Write-TestText (Join-Path $workingRepo 'overlap.txt') "policy=fork-backport`n"
    Write-TestText (Join-Path $workingRepo 'deleted-upstream.txt') "value=fork-modified`n"
    Write-TestText (Join-Path $workingRepo 'fork-whitespace.txt') "value=fork-only-trailing   `n"
    Write-TestText (Join-Path $workingRepo 'equivalent-backport.cs') $equivalentFork
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
    Invoke-Git $workingRepo mv 'rename-me.txt' 'renamed-upstream.txt'
    Write-TestText (Join-Path $workingRepo 'renamed-upstream.txt') "upstream=future`n$renamedMiddle`nfork=old`n"
    Write-TestText (Join-Path $workingRepo 'overlap.txt') "policy=future-upstream`n"
    Remove-Item -LiteralPath (Join-Path $workingRepo 'deleted-upstream.txt') -Force
    Write-TestText (Join-Path $workingRepo 'equivalent-backport.cs') $equivalentTarget
    Write-TestText (Join-Path $workingRepo 'future-upstream.txt') 'future-release'
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'future upstream release'
    $futureCommit = Get-GitOutput $workingRepo rev-parse HEAD

    $officialRepo = Join-Path $fixtureRoot 'official.git'
    & git init --bare $officialRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize future-port official repository.'
    }
    & git -C $workingRepo tag -a 'v9.9.9' -m 'future source tag' $futureCommit
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to create the annotated future source tag.'
    }
    Invoke-Git $workingRepo push $officialRepo 'refs/tags/v9.9.9:refs/tags/v9.9.9'

    $externalOutputPath = Join-Path $fixtureRoot 'github-output-external.txt'
    $previousGitHubOutput = $env:GITHUB_OUTPUT
    try {
        $env:GITHUB_OUTPUT = $externalOutputPath
        Push-Location $workingRepo
        try {
            & $applyPatchScript `
                -BaseBranch 'official/v9.9.9' `
                -BaseRepositoryUrl $officialRepo `
                -BaseRef 'refs/tags/v9.9.9' `
                -SourcePatchBranch 'marc/proton' `
                -TargetBranch 'codex/future-external'
        }
        finally {
            Pop-Location
        }
    }
    finally {
        $env:GITHUB_OUTPUT = $previousGitHubOutput
    }

    Assert-Condition ((Get-GitOutput $workingRepo branch --show-current) -eq 'codex/future-external') `
        'Future-port automation did not create the external-base target branch.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $workingRepo 'future-upstream.txt') -PathType Leaf) `
        'Future-port automation did not use the external tagged base.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $workingRepo 'src/ProtonVPN.Vpn/PortMapping/NatPmpFeature.cs') -PathType Leaf) `
        'Future-port automation omitted fork changes when using an external tagged base.'
    $externalRenamedContent = Get-Content -LiteralPath (Join-Path $workingRepo 'renamed-upstream.txt') -Raw
    Assert-Condition ($externalRenamedContent.Contains('upstream=future')) `
        'Future-port automation discarded the external upstream side of a renamed file.'
    Assert-Condition ($externalRenamedContent.Contains('fork=complete')) `
        'Future-port automation failed to carry fork edits across an external upstream rename.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'overlap.txt') -Raw).Trim() -eq 'policy=future-upstream') `
        'Future-port automation did not prefer the external target release for overlapping changes.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $workingRepo 'deleted-upstream.txt'))) `
        'Future-port automation resurrected a path deleted by the external target release.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'fork-whitespace.txt') -Raw).Contains('value=fork-only-trailing   ')) `
        'Future-port automation rejected or discarded a fork-only whitespace diagnostic.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'equivalent-backport.cs') -Raw) -eq $equivalentTarget) `
        'Future-port automation replayed an already-upstream whitespace-equivalent backport.'

    $externalOutputs = Get-Content -LiteralPath $externalOutputPath -Raw
    Assert-Condition ($externalOutputs -match "(?m)^base_commit=$futureCommit\r?$") `
        'Future-port automation did not publish the resolved external base commit.'
    Assert-Condition ($externalOutputs -match '(?m)^base_ref=refs/tags/v9\.9\.9\r?$') `
        'Future-port automation did not publish the resolved external base ref.'

    Invoke-Git $workingRepo switch master
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

    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $workingRepo 'rename-me.txt'))) `
        'Future-port automation resurrected the pre-rename upstream path.'
    $renamedContent = Get-Content -LiteralPath (Join-Path $workingRepo 'renamed-upstream.txt') -Raw
    Assert-Condition ($renamedContent.Contains('upstream=future')) `
        'Future-port automation discarded the upstream side of a renamed file.'
    Assert-Condition ($renamedContent.Contains('fork=complete')) `
        'Future-port automation failed to carry fork edits across an upstream rename.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'overlap.txt') -Raw).Trim() -eq 'policy=future-upstream') `
        'Future-port automation did not prefer the target release for overlapping changes.'
    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $workingRepo 'deleted-upstream.txt'))) `
        'Future-port automation resurrected a path deleted by the target release.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'fork-whitespace.txt') -Raw).Contains('value=fork-only-trailing   ')) `
        'Future-port automation rejected or discarded a fork-only whitespace diagnostic.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'equivalent-backport.cs') -Raw) -eq $equivalentTarget) `
        'Future-port automation replayed an already-upstream whitespace-equivalent backport.'

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

function Test-RedundantForkChangeCleanup {
    $fixtureRoot = Join-Path $testRoot 'known-backport-cleanup'
    $workingRepo = Join-Path $fixtureRoot 'working'
    $remoteRepo = Join-Path $fixtureRoot 'origin.git'

    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    & git init --initial-branch=master $workingRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize known-backport working repository.'
    }
    Invoke-Git $workingRepo config user.name 'Patch Tooling Tests'
    Invoke-Git $workingRepo config user.email 'patch-tooling@example.invalid'

    $baseMixed = @'
namespace Demo;

public class MixedBehavior
{
    public string ForkArea() => "base";

    public bool Unchanged1() => true;
    public bool Unchanged2() => true;
    public bool Unchanged3() => true;
    public bool Unchanged4() => true;

    public bool UpstreamBehavior() => false;
}
'@
    $backportedMixed = @'
namespace Demo;

public class MixedBehavior
{
    public string ForkArea() => "base";

    public bool Unchanged1() => true;
    public bool Unchanged2() => true;
    public bool Unchanged3() => true;
    public bool Unchanged4() => true;

    public bool UpstreamBehavior() => true;
}
'@
    $customMixed = @'
namespace Demo;

public class MixedBehavior
{
    public string ForkArea() => "fork";

    public bool Unchanged1() => true;
    public bool Unchanged2() => true;
    public bool Unchanged3() => true;
    public bool Unchanged4() => true;

    public bool UpstreamBehavior() => true;
}
'@
    $targetMixed = @'
namespace Demo;

public class MixedBehavior
{
    public string ForkArea() => "base";

    public bool Unchanged1() => true;
    public bool Unchanged2() => true;
    public bool Unchanged3() => true;
    public bool Unchanged4() => true;

    public bool UpstreamBehavior()
    {
        return true;
    }
}
'@

    $dependentBase = @'
namespace Demo;

public class DependentBackport
{
    public bool UpstreamBehavior() => false;
}
'@
    $dependentBackported = @'
namespace Demo;

public class DependentBackport
{
    public bool UpstreamBehavior() => true;
}
'@
    $dependentCustom = @'
namespace Demo;

public class DependentBackport
{
    public bool UpstreamBehavior(bool forkPolicy) => true && forkPolicy;
}
'@
    $dependentRefined = @'
namespace Demo;

public class DependentBackport
{
    public bool UpstreamBehavior(bool forkPolicy, bool additionalPolicy) => true && forkPolicy && additionalPolicy;
}
'@

    $windowPositionBase = @'
namespace Demo;

public struct WindowPositionParameters
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int? XPosition { get; set; }
    public int? YPosition { get; set; }
}
'@
    $windowPositionBackported = @'
namespace Demo;

public struct WindowPositionParameters
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int? XPosition { get; set; }
    public int? YPosition { get; set; }
    public bool IsCentered { get; set; }
}
'@
    $windowLocationBackported = @'
namespace Demo;

public struct WindowLocation
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int? XPosition { get; set; }
    public int? YPosition { get; set; }
}
'@
    $feedbackBase = @'
<VisualStateGroup>
    <VisualState x:Name="Visible" />
</VisualStateGroup>
'@
    $feedbackEquivalent = @'
<VisualStateGroup>
    <VisualState x:Name="Visible" />
    <VisualState x:Name="DismissingFeedback">
        <Storyboard Duration="0.5" />
    </VisualState>
</VisualStateGroup>
'@
    $feedbackForkRefined = @'
<VisualStateGroup>
    <VisualState x:Name="Visible" />
    <VisualState x:Name="DismissingFeedback">
        <Setter Property="ForkMarker" Value="true" />
        <Storyboard Duration="0.5" />
    </VisualState>
</VisualStateGroup>
'@

    Write-TestText (Join-Path $workingRepo 'mixed-backport.cs') $baseMixed
    Write-TestText (Join-Path $workingRepo 'dependent-backport.cs') $dependentBase
    Write-TestText (Join-Path $workingRepo 'pure-backport.txt') "value=old`n"
    Write-TestText (Join-Path $workingRepo 'window-position.cs') $windowPositionBase
    Write-TestText (Join-Path $workingRepo 'feedback.xaml') $feedbackBase
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'old upstream release'

    & git init --bare $remoteRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize known-backport origin repository.'
    }
    Invoke-Git $workingRepo remote add origin $remoteRepo
    Invoke-Git $workingRepo push -u origin master

    Invoke-Git $workingRepo switch -c 'marc/proton'
    Write-TestText (Join-Path $workingRepo 'mixed-backport.cs') $backportedMixed
    Write-TestText (Join-Path $workingRepo 'dependent-backport.cs') $dependentBackported
    Write-TestText (Join-Path $workingRepo 'pure-backport.txt') "value=backported`n"
    Write-TestText (Join-Path $workingRepo 'window-position.cs') $windowPositionBackported
    Write-TestText (Join-Path $workingRepo 'window-location.cs') $windowLocationBackported
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Port Proton synthetic future behavior'
    $backportCommit = Get-GitOutput $workingRepo rev-parse HEAD
    $backportParent = Get-GitOutput $workingRepo rev-parse "$backportCommit^"
    $copyDetectionSummary = Get-GitOutput $workingRepo diff --summary --find-copies $backportParent $backportCommit
    Assert-Condition ($copyDetectionSummary -match 'copy .*window-position\.cs => window-location\.cs') `
        'Known-backport regression fixture did not create a detectable copy-shaped addition.'

    Write-TestText (Join-Path $workingRepo 'feedback.xaml') $feedbackEquivalent
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Add feedback animation already present in target release'
    $equivalentForkCommit = Get-GitOutput $workingRepo rev-parse HEAD

    Write-TestText (Join-Path $workingRepo 'mixed-backport.cs') $customMixed
    Write-TestText (Join-Path $workingRepo 'dependent-backport.cs') $dependentCustom
    Write-TestText (Join-Path $workingRepo 'fork-only.txt') "keep-me`n"
    Write-TestText (Join-Path $workingRepo 'feedback.xaml') $feedbackForkRefined
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Add fork-only behavior after upstream backport'
    Write-TestText (Join-Path $workingRepo 'dependent-backport.cs') $dependentRefined
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Refine dependent fork behavior'
    Invoke-Git $workingRepo push -u origin 'marc/proton'

    Invoke-Git $workingRepo switch master
    Write-TestText (Join-Path $workingRepo 'mixed-backport.cs') $targetMixed
    Write-TestText (Join-Path $workingRepo 'dependent-backport.cs') $dependentBackported
    Write-TestText (Join-Path $workingRepo 'pure-backport.txt') "value=future-upstream`n"
    Write-TestText (Join-Path $workingRepo 'window-position.cs') $windowPositionBackported
    Write-TestText (Join-Path $workingRepo 'window-location.cs') $windowLocationBackported
    Write-TestText (Join-Path $workingRepo 'feedback.xaml') $feedbackEquivalent
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'future upstream release'
    Invoke-Git $workingRepo branch 'release/v5.1.8'
    Invoke-Git $workingRepo push origin 'release/v5.1.8'

    $previousCutoff = $env:FUTURE_PORT_BACKPORT_CUTOFF
    $previousEquivalentCommits = $env:FUTURE_PORT_EQUIVALENT_FORK_COMMITS
    try {
        $env:FUTURE_PORT_BACKPORT_CUTOFF = $backportCommit
        $env:FUTURE_PORT_EQUIVALENT_FORK_COMMITS = $equivalentForkCommit
        Push-Location $workingRepo
        try {
            & $applyPatchScript -BaseBranch 'release/v5.1.8' -SourcePatchBranch 'marc/proton' -TargetBranch 'codex/backport-clean'
        }
        finally {
            Pop-Location
        }
    }
    finally {
        $env:FUTURE_PORT_BACKPORT_CUTOFF = $previousCutoff
        $env:FUTURE_PORT_EQUIVALENT_FORK_COMMITS = $previousEquivalentCommits
    }

    $mixedContent = Get-Content -LiteralPath (Join-Path $workingRepo 'mixed-backport.cs') -Raw
    Assert-Condition ($mixedContent.Contains('return true;')) 'Known-backport cleanup did not preserve the target-release implementation.'
    Assert-Condition (-not $mixedContent.Contains('UpstreamBehavior() => true;')) 'Known-backport cleanup replayed the old fork backport implementation.'
    Assert-Condition ($mixedContent.Contains('ForkArea() => "fork";')) 'Known-backport cleanup discarded a later fork-only edit from the same file.'
    $dependentContent = Get-Content -LiteralPath (Join-Path $workingRepo 'dependent-backport.cs') -Raw
    Assert-Condition ($dependentContent.Contains('UpstreamBehavior(bool forkPolicy, bool additionalPolicy) => true && forkPolicy && additionalPolicy;')) `
        'Known-backport cleanup did not replay later fork-specific changes in their original order.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'pure-backport.txt') -Raw).Trim() -eq 'value=future-upstream') 'Known-backport cleanup did not keep the target copy of a pure upstream backport.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'fork-only.txt') -Raw).Trim() -eq 'keep-me') 'Known-backport cleanup discarded an unrelated fork-only file.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $workingRepo 'window-location.cs')) `
        'Known-backport cleanup discarded a target-release file copied from a fork backport file.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'window-position.cs') -Raw).Contains('IsCentered')) `
        'Known-backport cleanup discarded the target-release copy of a modified source file.'
    $feedbackContent = Get-Content -LiteralPath (Join-Path $workingRepo 'feedback.xaml') -Raw
    Assert-Condition ($feedbackContent.Contains('DismissingFeedback')) `
        'Target-equivalent cleanup removed behavior already present in the release.'
    Assert-Condition ($feedbackContent.Contains('ForkMarker')) `
        'Target-equivalent cleanup discarded a later fork-only edit in the same file.'
}

function Test-KnownBackportCleanupFailsClosed {
    $fixtureRoot = Join-Path $testRoot 'known-backport-conflict'
    $workingRepo = Join-Path $fixtureRoot 'working'
    $remoteRepo = Join-Path $fixtureRoot 'origin.git'

    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    & git init --initial-branch=master $workingRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize known-backport conflict working repository.'
    }
    Invoke-Git $workingRepo config user.name 'Patch Tooling Tests'
    Invoke-Git $workingRepo config user.email 'patch-tooling@example.invalid'

    Write-TestText (Join-Path $workingRepo 'semantic-conflict.cs') ('public string Choice() => "base";' + [Environment]::NewLine)
    Write-TestText (Join-Path $workingRepo 'upstream-backport.txt') "value=old`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'old upstream release'

    & git init --bare $remoteRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize known-backport conflict origin repository.'
    }
    Invoke-Git $workingRepo remote add origin $remoteRepo
    Invoke-Git $workingRepo push -u origin master

    Invoke-Git $workingRepo switch -c 'marc/proton'
    Write-TestText (Join-Path $workingRepo 'upstream-backport.txt') "value=backported`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Port Proton synthetic upstream behavior'
    $backportCommit = Get-GitOutput $workingRepo rev-parse HEAD

    Write-TestText (Join-Path $workingRepo 'semantic-conflict.cs') ('public string Choice() => "fork";' + [Environment]::NewLine)
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Add fork-specific semantic change'
    Invoke-Git $workingRepo push -u origin 'marc/proton'

    Invoke-Git $workingRepo switch master
    Write-TestText (Join-Path $workingRepo 'semantic-conflict.cs') ('public string Choice() { return "target"; }' + [Environment]::NewLine)
    Write-TestText (Join-Path $workingRepo 'upstream-backport.txt') "value=future-upstream`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'future upstream release'
    Invoke-Git $workingRepo branch 'release/v9.9.9'
    Invoke-Git $workingRepo push origin 'release/v9.9.9'

    $previousCutoff = $env:FUTURE_PORT_BACKPORT_CUTOFF
    $failureMessage = ''
    try {
        $env:FUTURE_PORT_BACKPORT_CUTOFF = $backportCommit
        Push-Location $workingRepo
        try {
            try {
                & $applyPatchScript -BaseBranch 'release/v9.9.9' -SourcePatchBranch 'marc/proton' -TargetBranch 'codex/backport-conflict'
                throw 'Future-port automation unexpectedly accepted a genuine semantic conflict.'
            }
            catch {
                $failureMessage = "$($_.Exception.Message)"
            }
        }
        finally {
            Pop-Location
        }
    }
    finally {
        $env:FUTURE_PORT_BACKPORT_CUTOFF = $previousCutoff
    }

    Assert-Condition ($failureMessage.Contains('Fork changes still conflict with the target release')) `
        'Future-port automation did not fail closed on a cleaned semantic conflict.'
    Assert-Condition ($failureMessage.Contains('semantic-conflict.cs')) `
        'Future-port automation did not name the path containing the semantic conflict.'
    Assert-Condition ((Get-GitOutput $workingRepo branch --show-current) -eq 'codex/backport-conflict') `
        'Future-port automation did not leave the target branch checked out after the failed merge.'
    Assert-Condition ([string]::IsNullOrWhiteSpace((Get-GitOutput $workingRepo status --porcelain))) `
        'Future-port automation left merge changes in the target working tree after failing closed.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'semantic-conflict.cs') -Raw).Contains('return "target";')) `
        'Fail-closed merge handling changed the target-release file contents.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'upstream-backport.txt') -Raw).Trim() -eq 'value=future-upstream') `
        'Fail-closed merge handling changed the target-release backport file contents.'

    & git -C $workingRepo show-ref --verify --quiet 'refs/heads/__future_port_clean_source'
    Assert-Condition ($LASTEXITCODE -ne 0) 'Future-port automation left its temporary cleaned-source branch behind after failure.'
}

function Test-PromotedOfficialSourcePort {
    $fixtureRoot = Join-Path $testRoot 'promoted-official-source'
    $workingRepo = Join-Path $fixtureRoot 'working'
    $remoteRepo = Join-Path $fixtureRoot 'origin.git'

    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    & git init --initial-branch=master $workingRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize promoted-source working repository.'
    }
    Invoke-Git $workingRepo config user.name 'Patch Tooling Tests'
    Invoke-Git $workingRepo config user.email 'patch-tooling@example.invalid'

    Write-TestText (Join-Path $workingRepo 'upstream-behavior.txt') "value=old`n"
    Write-TestText (Join-Path $workingRepo 'fork-behavior.txt') "policy=old`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'old official source'
    $oldOfficial = Get-GitOutput $workingRepo rev-parse HEAD

    Invoke-Git $workingRepo switch -c legacy-fork
    Write-TestText (Join-Path $workingRepo 'upstream-behavior.txt') "value=backported`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Port Proton synthetic 5.1.8 behavior'
    $backportCommit = Get-GitOutput $workingRepo rev-parse HEAD
    Write-TestText (Join-Path $workingRepo 'fork-behavior.txt') "policy=fork`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Add original fork behavior'

    Invoke-Git $workingRepo switch master
    Write-TestText (Join-Path $workingRepo 'upstream-behavior.txt') "value=official-5.1.8`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'official v5.1.8 source'
    Invoke-Git $workingRepo tag v5.1.8

    Invoke-Git $workingRepo switch -c 'marc/proton'
    Write-TestText (Join-Path $workingRepo 'fork-behavior.txt') "policy=fork`n"
    Write-TestText (Join-Path $workingRepo 'fork-only.txt') "custom=retained`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'Review fork behavior on official v5.1.8'
    Invoke-Git $workingRepo merge -s ours --no-ff legacy-fork -m 'Record reviewed legacy fork history'

    $officialBase = Get-GitOutput $workingRepo merge-base v5.1.8 'marc/proton'
    Assert-Condition ($officialBase -eq (Get-GitOutput $workingRepo rev-parse v5.1.8)) `
        'Promoted-source fixture does not have official v5.1.8 as its active source base.'
    Assert-Condition ((Get-GitOutput $workingRepo merge-base $oldOfficial 'marc/proton') -eq $oldOfficial) `
        'Promoted-source fixture did not retain legacy fork ancestry.'

    & git init --bare $remoteRepo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to initialize promoted-source origin repository.'
    }
    Invoke-Git $workingRepo remote add origin $remoteRepo
    Invoke-Git $workingRepo push origin 'marc/proton'

    Invoke-Git $workingRepo switch master
    Write-TestText (Join-Path $workingRepo 'release.txt') "version=5.1.9`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'official v5.1.9 source'
    Invoke-Git $workingRepo branch 'release/v5.1.9'
    Invoke-Git $workingRepo push origin 'release/v5.1.9'

    $previousCutoff = $env:FUTURE_PORT_BACKPORT_CUTOFF
    try {
        $env:FUTURE_PORT_BACKPORT_CUTOFF = $backportCommit
        Push-Location $workingRepo
        try {
            & $applyPatchScript -BaseBranch 'release/v5.1.9' -SourcePatchBranch 'marc/proton' -TargetBranch 'future/proton-v5.1.9'
        }
        finally {
            Pop-Location
        }
    }
    finally {
        $env:FUTURE_PORT_BACKPORT_CUTOFF = $previousCutoff
    }

    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'upstream-behavior.txt') -Raw).Trim() -eq 'value=official-5.1.8') `
        'Post-promotion port replayed the historical backport instead of retaining official behavior.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'fork-behavior.txt') -Raw).Trim() -eq 'policy=fork') `
        'Post-promotion port discarded reviewed fork behavior.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $workingRepo 'fork-only.txt') -PathType Leaf) `
        'Post-promotion port discarded a fork-only file.'
    Assert-Condition (Test-Path -LiteralPath (Join-Path $workingRepo 'release.txt') -PathType Leaf) `
        'Post-promotion port discarded the new upstream release.'
    & git -C $workingRepo show-ref --verify --quiet 'refs/heads/__future_port_clean_source'
    Assert-Condition ($LASTEXITCODE -ne 0) 'Post-promotion port unnecessarily created a historical cleanup snapshot.'

    Invoke-Git $workingRepo switch master
    Write-TestText (Join-Path $workingRepo 'fork-behavior.txt') "policy=upstream-reworked`n"
    Invoke-Git $workingRepo add .
    Invoke-Git $workingRepo commit -m 'official v5.1.10 source conflicts with fork policy'
    Invoke-Git $workingRepo branch 'release/v5.1.10'
    Invoke-Git $workingRepo push origin 'release/v5.1.10'

    $failureMessage = ''
    Push-Location $workingRepo
    try {
        try {
            & $applyPatchScript -BaseBranch 'release/v5.1.10' -SourcePatchBranch 'marc/proton' -TargetBranch 'future/proton-v5.1.10'
            throw 'Post-promotion port unexpectedly accepted a genuine semantic conflict.'
        }
        catch {
            $failureMessage = "$($_.Exception.Message)"
        }
    }
    finally {
        Pop-Location
    }

    Assert-Condition ($failureMessage.Contains('Fork changes still conflict with the target release')) `
        'Post-promotion port did not fail closed on a semantic conflict.'
    Assert-Condition ($failureMessage.Contains('fork-behavior.txt')) `
        'Post-promotion port did not identify the conflicting path.'
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $workingRepo 'fork-behavior.txt') -Raw).Trim() -eq 'policy=upstream-reworked') `
        'Fail-closed post-promotion port changed the target-release implementation.'
    Assert-Condition ([string]::IsNullOrWhiteSpace((Get-GitOutput $workingRepo status --porcelain))) `
        'Fail-closed post-promotion port left merge changes in the working tree.'
}

function Test-ForkRegressionOutputIsolation {
    $regressionScriptPath = Join-Path $PSScriptRoot 'test-fork-regressions.ps1'
    $regressionScript = Get-Content -LiteralPath $regressionScriptPath -Raw

    Assert-Condition ($regressionScript -match '\$projectOutputPath\s*=\s*Resolve-RepositoryPath\s*\(\s*Join-Path\s+\$TestOutputDirectory\s+\$projectName\s*\)') `
        'Fork regression projects must use a stable, project-specific output directory.'
    Assert-Condition ($regressionScript -match '"-p:OutputPath=\$projectOutputPath"') `
        'Fork regression projects must pass their isolated output directory to MSBuild.'
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
    Test-VersionStamping
    Test-PackageComposition
    Test-InstallerRuntimeDataPreservation
    Test-CompleteForkPort
    Test-RedundantForkChangeCleanup
    Test-KnownBackportCleanupFailsClosed
    Test-PromotedOfficialSourcePort
    Test-ForkRegressionOutputIsolation
    Write-Host 'Patch tooling regression tests passed.'
}
finally {
    if ($ownsWorkingDirectory -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
