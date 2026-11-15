[CmdletBinding()]
param(
    [string] $WorkingDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$stageRuntimeScript = Join-Path $PSScriptRoot 'stage-runtime-dependency-delta.ps1'
$completePackager = Join-Path $PSScriptRoot 'package-complete-fast-patch.ps1'
$basePackager = Join-Path $PSScriptRoot 'package-patch-artifacts.ps1'
$baseInstallerScript = Join-Path $repositoryRoot 'scripts/Install-ProtonVPNPatch.ps1'
$completeInstallerScript = Join-Path $repositoryRoot 'scripts/Install-ProtonVPNCompletePatch.ps1'

$ownsWorkingDirectory = [string]::IsNullOrWhiteSpace($WorkingDirectory)
$testRoot = if ($ownsWorkingDirectory) {
    Join-Path ([System.IO.Path]::GetTempPath()) "protonvpn-complete-fast-patch-$([Guid]::NewGuid())"
} else {
    [System.IO.Path]::GetFullPath($WorkingDirectory)
}

function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Write-TestText {
    param([string] $Path, [AllowEmptyString()] [string] $Content)
    New-Item -ItemType Directory -Force -Path (Split-Path -Path $Path -Parent) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Write-PackageProps {
    param([string] $Path, [hashtable] $Versions)

    $items = @(
        foreach ($id in $Versions.Keys | Sort-Object) {
            '    <PackageVersion Include="{0}" Version="{1}" />' -f $id, $Versions[$id]
        }
    ) -join "`n"

    Write-TestText $Path @"
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
$items
  </ItemGroup>
</Project>
"@
}

function New-ClientDependencyFixture {
    param([string] $OutputDirectory)

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    Write-TestText (Join-Path $OutputDirectory 'Grpc.Net.Client.dll') 'grpc-client-2.80'
    Write-TestText (Join-Path $OutputDirectory 'Grpc.Core.Api.dll') 'grpc-core-2.80'
    Write-TestText (Join-Path $OutputDirectory 'System.Security.Cryptography.Pkcs.dll') 'pkcs-10.0.10'

    $target = [ordered]@{
        'Grpc.Net.Client/2.80.0' = [ordered]@{
            dependencies = [ordered]@{ 'Grpc.Core.Api' = '2.80.0' }
            runtime = [ordered]@{ 'lib/net8.0/Grpc.Net.Client.dll' = [ordered]@{} }
        }
        'Grpc.Core.Api/2.80.0' = [ordered]@{
            runtime = [ordered]@{ 'lib/net8.0/Grpc.Core.Api.dll' = [ordered]@{} }
        }
        'Microsoft.Windows.Compatibility/10.0.10' = [ordered]@{
            dependencies = [ordered]@{ 'System.Security.Cryptography.Pkcs' = '10.0.10' }
        }
        'System.Security.Cryptography.Pkcs/10.0.10' = [ordered]@{
            runtime = [ordered]@{ 'lib/net8.0/System.Security.Cryptography.Pkcs.dll' = [ordered]@{} }
        }
    }
    $libraries = [ordered]@{}
    foreach ($key in $target.Keys) { $libraries[$key] = [ordered]@{ type = 'package' } }

    $document = [ordered]@{
        runtimeTarget = [ordered]@{ name = '.NETCoreApp,Version=v8.0/win-x64' }
        targets = [ordered]@{ '.NETCoreApp,Version=v8.0/win-x64' = $target }
        libraries = $libraries
    }

    $depsPath = Join-Path $OutputDirectory 'ProtonVPN.Client.deps.json'
    Write-TestText $depsPath ($document | ConvertTo-Json -Depth 12)
    return $depsPath
}

function Test-RuntimeDependencyClosure {
    $fixture = Join-Path $testRoot 'runtime-closure'
    $currentProps = Join-Path $fixture 'current.props'
    $baselineProps = Join-Path $fixture 'baseline.props'
    $clientOutput = Join-Path $fixture 'client-output'
    $stageDir = Join-Path $fixture 'runtime-stage'
    $metadataPath = Join-Path $fixture 'runtime-metadata.json'

    Write-PackageProps $baselineProps @{
        'coverlet.collector' = '8.0.0'
        'Grpc.Net.Client' = '2.76.0'
        'Microsoft.Windows.Compatibility' = '10.0.3'
    }
    Write-PackageProps $currentProps @{
        'coverlet.collector' = '10.0.1'
        'Grpc.Net.Client' = '2.80.0'
        'Microsoft.Windows.Compatibility' = '10.0.10'
    }
    $depsPath = New-ClientDependencyFixture $clientOutput

    & $stageRuntimeScript `
        -BuildMode client `
        -CurrentPackagesPath $currentProps `
        -BaselinePackagesPath $baselineProps `
        -UpstreamBaseCommit ('a' * 40) `
        -ClientDependenciesPath $depsPath `
        -ClientOutputDirectory $clientOutput `
        -StageDirectory $stageDir `
        -MetadataPath $metadataPath

    foreach ($file in @('Grpc.Net.Client.dll', 'Grpc.Core.Api.dll', 'System.Security.Cryptography.Pkcs.dll')) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $stageDir $file) -PathType Leaf) "Runtime dependency closure omitted $file."
    }

    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Condition (@($metadata.changedDirectPackages).Count -eq 3) 'Changed direct package roots were not recorded.'
    Assert-Condition (@($metadata.runtimePackages).Count -eq 4) 'Transitive runtime package closure is incomplete.'
    Assert-Condition (@($metadata.files).Count -eq 3) 'Runtime dependency staged file count is incorrect.'
    $overlay = @($metadata.dependencyManifestOverlays)[0]
    Assert-Condition ([string] $overlay.graph -eq 'client') 'Client runtime probing metadata is missing.'
    Assert-Condition ($null -ne $overlay.assets.runtime.PSObject.Properties['Grpc.Core.Api.dll']) 'Transitive assembly is absent from runtime probing metadata.'

    $runtimePackageNames = @($metadata.runtimePackages | ForEach-Object { [string] $_.id })
    Assert-Condition ($runtimePackageNames -contains 'Grpc.Core.Api') 'Grpc transitive dependency was not followed.'
    Assert-Condition ($runtimePackageNames -contains 'System.Security.Cryptography.Pkcs') 'Compatibility transitive dependency was not followed.'

    return [ordered]@{ Stage = $stageDir; Metadata = $metadataPath }
}

function Test-CompletePackaging {
    param([System.Collections.IDictionary] $RuntimeFixture)

    $fixture = Join-Path $testRoot 'complete-package'
    $binDir = Join-Path $fixture 'bin'
    $clientDir = Join-Path $fixture 'client'
    $launcherDir = Join-Path $fixture 'launcher'
    $toolsDir = Join-Path $fixture 'tools'
    $patchDir = Join-Path $fixture 'patch'
    $installerDir = Join-Path $fixture 'installer'

    foreach ($directory in @($binDir, $clientDir, $launcherDir, $toolsDir)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    Write-TestText (Join-Path $clientDir 'ProtonVPN.Client.dll') 'client'
    Write-TestText (Join-Path $clientDir 'ProtonVPN.Shared.dll') 'shared'
    Write-TestText (Join-Path $clientDir 'ProtonVPN.Client.pri') 'pri'
    Write-TestText (Join-Path $clientDir 'App.xbf') 'app'
    Write-TestText (Join-Path $clientDir 'MainWindow.xbf') 'main'
    Write-TestText (Join-Path $clientDir 'UI/FeaturePage.xbf') 'feature'
    Write-TestText (Join-Path $launcherDir 'ProtonVPN.Launcher.exe') 'single-file-launcher-with-runtime'

    $builder = Join-Path $toolsDir 'builder.ps1'
    Write-TestText $builder @'
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $PatchPath,
    [Parameter(Mandatory = $true)] [string] $OutputPath,
    [Parameter(Mandatory = $true)] [string] $InstallerScriptPath,
    [Parameter(Mandatory = $true)] [string] $LauncherPath
)
New-Item -ItemType Directory -Force -Path (Split-Path -Path $OutputPath -Parent) | Out-Null
[IO.File]::WriteAllBytes($OutputPath, [byte[]]@(1, 2, 3, 4))
'@
    $installerLauncher = Join-Path $toolsDir 'installer.cmd'
    Write-TestText $installerLauncher '@echo off'

    & $completePackager `
        -BuildMode client `
        -TargetVersion '5.1.8' `
        -SourceCommit '0123456789abcdef' `
        -SourceRef 'test/complete-runtime' `
        -WorkflowRunId '4321' `
        -BinDirectory $binDir `
        -ClientOutputDirectory $clientDir `
        -ApplicationLauncherOutputDirectory $launcherDir `
        -RuntimeDependencyOutputDirectory $RuntimeFixture.Stage `
        -RuntimeDependencyMetadataPath $RuntimeFixture.Metadata `
        -PatchDirectory $patchDir `
        -InstallerDirectory $installerDir `
        -BasePackagerPath $basePackager `
        -BuilderPath $builder `
        -BaseInstallerScriptPath $baseInstallerScript `
        -CompleteInstallerScriptPath $completeInstallerScript `
        -InstallerLauncherPath $installerLauncher

    $manifest = Get-Content -LiteralPath (Join-Path $patchDir 'patch-manifest.json') -Raw | ConvertFrom-Json
    Assert-Condition ([int] $manifest.schemaVersion -eq 2) 'Complete FastPatch did not upgrade to manifest schema v2.'
    Assert-Condition ([bool] $manifest.completeRuntimeCoverage) 'Manifest does not declare complete runtime coverage.'
    Assert-Condition ([bool] $manifest.launcherIncluded) 'Manifest does not record the application launcher.'
    Assert-Condition ([string] $manifest.upstreamBaseCommit -eq ('a' * 40)) 'Upstream baseline provenance was lost.'
    Assert-Condition ([int] $manifest.runtimeDependencyFileCount -eq 3) 'Runtime dependency file count is incorrect.'
    Assert-Condition (@($manifest.dependencyManifestOverlays).Count -eq 1) 'Complete manifest dropped runtime probing metadata.'

    $entries = @{}
    foreach ($file in @($manifest.files)) { $entries[[string] $file.path] = $file }
    foreach ($path in @(
        'ProtonVPN.Launcher.exe',
        'Grpc.Net.Client.dll',
        'Grpc.Core.Api.dll',
        'System.Security.Cryptography.Pkcs.dll',
        'Tools/Install-ProtonVPNPatch.base.ps1'
    )) {
        Assert-Condition $entries.ContainsKey($path) "Complete FastPatch manifest omitted $path."
    }
    Assert-Condition ([string] $entries['ProtonVPN.Launcher.exe'].scope -eq 'installRoot') 'Launcher was not assigned installRoot scope.'
    Assert-Condition ([string] $entries['Grpc.Net.Client.dll'].scope -eq 'version') 'Runtime dependency was not assigned version scope.'
    Assert-Condition ([string] $entries['Tools/Install-ProtonVPNPatch.base.ps1'].scope -eq 'tool') 'Base installer helper was not assigned tool scope.'

    $oldInstallerOutput = @(& powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $baseInstallerScript `
        -PatchPath $patchDir `
        -TargetVersion '5.1.8' `
        -ValidateOnly 2>&1)
    $oldInstallerExitCode = $LASTEXITCODE
    Assert-Condition ($oldInstallerExitCode -ne 0) 'Schema-v1 installer accepted a schema-v2 complete FastPatch payload.'

    & powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $completeInstallerScript `
        -PatchPath $patchDir `
        -TargetVersion '5.1.8' `
        -ValidateOnly
    Assert-Condition ($LASTEXITCODE -eq 0) 'Complete installer rejected an untampered schema-v2 payload.'

    $tamperedStage = Join-Path $fixture 'tampered-runtime-stage'
    Copy-Item -LiteralPath $RuntimeFixture.Stage -Destination $tamperedStage -Recurse
    Write-TestText (Join-Path $tamperedStage 'Grpc.Net.Client.dll') 'tampered'

    $tamperRejected = $false
    try {
        & $completePackager `
            -BuildMode client `
            -TargetVersion '5.1.8' `
            -SourceCommit '0123456789abcdef' `
            -SourceRef 'test/complete-runtime' `
            -WorkflowRunId '4322' `
            -BinDirectory $binDir `
            -ClientOutputDirectory $clientDir `
            -ApplicationLauncherOutputDirectory $launcherDir `
            -RuntimeDependencyOutputDirectory $tamperedStage `
            -RuntimeDependencyMetadataPath $RuntimeFixture.Metadata `
            -PatchDirectory (Join-Path $fixture 'tampered-patch') `
            -InstallerDirectory (Join-Path $fixture 'tampered-installer') `
            -BasePackagerPath $basePackager `
            -BuilderPath $builder `
            -BaseInstallerScriptPath $baseInstallerScript `
            -CompleteInstallerScriptPath $completeInstallerScript `
            -InstallerLauncherPath $installerLauncher
    }
    catch {
        if ($_.Exception.Message -like '*source hash mismatch*') { $tamperRejected = $true } else { throw }
    }
    Assert-Condition $tamperRejected 'Tampered runtime dependency was accepted.'
}

function Test-DependencyManifestRuntimeProbing {
    # Exercise the real .NET host, rather than only asserting JSON shapes.
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($completeInstallerScript, [ref] $tokens, [ref] $errors)
    Assert-Condition ($errors.Count -eq 0) 'Complete installer has PowerShell syntax errors.'
    $mergeFunction = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Merge-RuntimeDependencyManifest'
    }, $true)
    Assert-Condition ($null -ne $mergeFunction) 'Runtime manifest merge function is missing.'
    Invoke-Expression $mergeFunction.Extent.Text

    $fixture = Join-Path $testRoot 'runtime-probing'
    $probe = Join-Path $fixture 'probe'
    $dependency = Join-Path $fixture 'dependency'
    $output = Join-Path $fixture 'output'
    $dependencyOutput = Join-Path $fixture 'dependency-output'
    Write-TestText (Join-Path $probe 'Probe.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    Write-TestText (Join-Path $probe 'Program.cs') @'
try
{
    System.Reflection.Assembly.Load("FastPatch.NewDependency");
    return 0;
}
catch (System.IO.FileNotFoundException)
{
    return 42;
}
'@
    Write-TestText (Join-Path $dependency 'Dependency.csproj') '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>FastPatch.NewDependency</AssemblyName></PropertyGroup></Project>'
    Write-TestText (Join-Path $dependency 'Dependency.cs') 'public sealed class NewDependency { }'
    & dotnet build (Join-Path $probe 'Probe.csproj') -o $output --nologo
    Assert-Condition ($LASTEXITCODE -eq 0) 'Could not build runtime probing fixture.'
    & dotnet build (Join-Path $dependency 'Dependency.csproj') -o $dependencyOutput --nologo
    Assert-Condition ($LASTEXITCODE -eq 0) 'Could not build new runtime dependency.'
    Copy-Item (Join-Path $dependencyOutput 'FastPatch.NewDependency.dll') $output

    & dotnet (Join-Path $output 'Probe.dll')
    Assert-Condition ($LASTEXITCODE -eq 42) 'Fixture did not reproduce a DLL present on disk but absent from the dependency manifest.'

    $depsPath = Join-Path $output 'Probe.deps.json'
    $baselineJson = Get-Content -LiteralPath $depsPath -Raw
    $baseline = $baselineJson | ConvertFrom-Json
    $overlay = [pscustomobject]@{
        runtimeTarget = [string] $baseline.runtimeTarget.name
        assets = [pscustomobject]@{
            runtime = [pscustomobject]@{ 'FastPatch.NewDependency.dll' = [pscustomobject]@{ assemblyVersion = '1.0.0.0'; fileVersion = '1.0.0.0' } }
            native = [pscustomobject]@{}
            resources = [pscustomobject]@{}
        }
    }
    $mergedJson = Merge-RuntimeDependencyManifest -BaselineJson $baselineJson -Overlay $overlay
    Write-TestText $depsPath $mergedJson
    & dotnet (Join-Path $output 'Probe.dll')
    Assert-Condition ($LASTEXITCODE -eq 0) 'Dependency manifest merge did not restore actual .NET assembly loading.'

    # Reapplying is idempotent, and the installed framework and app entry survive.
    $again = Merge-RuntimeDependencyManifest -BaselineJson $mergedJson -Overlay $overlay | ConvertFrom-Json
    Assert-Condition ([string] $again.runtimeTarget.name -eq [string] $baseline.runtimeTarget.name) 'Installed runtime target changed.'
    Assert-Condition ($null -ne $again.libraries.PSObject.Properties['Probe/1.0.0']) 'Installed application library was removed.'
    Assert-Condition (@($again.targets.($again.runtimeTarget.name).PSObject.Properties | Where-Object Name -eq 'ProtonVPN.FastPatch.Runtime/1.0.0').Count -eq 1) 'Repeated merge duplicated the overlay.'

    $unsafe = [pscustomobject]@{
        runtimeTarget = $overlay.runtimeTarget
        assets = [pscustomobject]@{
            runtime = [pscustomobject]@{ '../escape.dll' = [pscustomobject]@{} }
            native = [pscustomobject]@{}
            resources = [pscustomobject]@{}
        }
    }
    $rejected = $false
    try { $null = Merge-RuntimeDependencyManifest -BaselineJson $baselineJson -Overlay $unsafe }
    catch { if ($_.Exception.Message -like '*Unsafe runtime dependency overlay*') { $rejected = $true } else { throw } }
    Assert-Condition $rejected 'Unsafe runtime dependency overlay was accepted.'
}

function Test-InstallerLifecycleContracts {
    $content = Get-Content -LiteralPath $completeInstallerScript -Raw

    Assert-Condition (
        $content.Contains('return Invoke-ProcessAndWait') -and
        $content.Contains('-FilePath (Get-WindowsPowerShellPath)') -and
        $content.Contains('$startInfo.CreateNoWindow = $false')
    ) 'Complete FastPatch base-installer delegation must reuse the existing console through the trusted process helper.'
    Assert-Condition (-not $content.Contains("'-File', (ConvertTo-QuotedProcessArgument -Value `$PSCommandPath)")) `
        'Complete FastPatch must not cross UAC by reopening the mutable original script path.'
    Assert-Condition ($content.Contains('-EncodedCommand')) `
        'Complete FastPatch must use an inline encoded bootstrap across the elevation boundary.'
    Assert-Condition ($content.Contains('Assert-TrustedStage -StagePath $TrustedStagePath -PayloadPath $PatchPath')) `
        'Complete FastPatch must verify the protected stage before privileged consumption.'
    Assert-Condition ($content.Contains("`$arguments += '-TrustedStagePath'")) `
        'Complete FastPatch must keep the base helper inside the same protected stage.'
    Assert-Condition ($content.Contains('$clientWasRunningBeforeInstall = Test-ClientRunningForTarget -TargetDirectory $targetDirectory')) `
        'Complete FastPatch must capture client state before delegating the version-folder install.'
    Assert-Condition ($content.Contains('-not (Test-ClientRunningForTarget -TargetDirectory $targetDirectory)')) `
        'Complete FastPatch must avoid duplicate client restart when the base installer already restarted it.'
    Assert-Condition ($content.Contains("Base installer did not leave the previously running Proton VPN Client active; restarting it now.")) `
        'Complete FastPatch is missing its client restart fallback.'
}
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    Test-InstallerLifecycleContracts
    Test-DependencyManifestRuntimeProbing
    & (Join-Path $PSScriptRoot 'test-fast-patch-secure-staging.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "FastPatch secure staging tests failed with exit code $LASTEXITCODE."
    }
    $runtimeFixture = Test-RuntimeDependencyClosure
    Test-CompletePackaging $runtimeFixture
    Write-Host 'Complete FastPatch regression tests passed.'
}
finally {
    if ($ownsWorkingDirectory -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
