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
$realInstallerScript = Join-Path $repositoryRoot 'scripts/Install-ProtonVPNPatch.ps1'

$ownsWorkingDirectory = [string]::IsNullOrWhiteSpace($WorkingDirectory)
$testRoot = if ($ownsWorkingDirectory) {
    Join-Path ([System.IO.Path]::GetTempPath()) "protonvpn-complete-fast-patch-$([Guid]::NewGuid())"
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

    New-Item -ItemType Directory -Force -Path (Split-Path -Path $Path -Parent) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Write-PackageProps {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [hashtable] $Versions
    )

    $items = @(
        foreach ($id in $Versions.Keys | Sort-Object) {
            "    <PackageVersion Include=\"$id\" Version=\"$($Versions[$id])\" />"
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
    param(
        [Parameter(Mandatory = $true)]
        [string] $OutputDirectory
    )

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    Write-TestText (Join-Path $OutputDirectory 'Grpc.Net.Client.dll') 'grpc-client-2.80'
    Write-TestText (Join-Path $OutputDirectory 'Grpc.Core.Api.dll') 'grpc-core-2.80'
    Write-TestText (Join-Path $OutputDirectory 'System.Security.Cryptography.Pkcs.dll') 'pkcs-10.0.10'

    $target = [ordered]@{
        'Grpc.Net.Client/2.80.0' = [ordered]@{
            dependencies = [ordered]@{
                'Grpc.Core.Api' = '2.80.0'
            }
            runtime = [ordered]@{
                'lib/net8.0/Grpc.Net.Client.dll' = [ordered]@{}
            }
        }
        'Grpc.Core.Api/2.80.0' = [ordered]@{
            runtime = [ordered]@{
                'lib/net8.0/Grpc.Core.Api.dll' = [ordered]@{}
            }
        }
        'Microsoft.Windows.Compatibility/10.0.10' = [ordered]@{
            dependencies = [ordered]@{
                'System.Security.Cryptography.Pkcs' = '10.0.10'
            }
        }
        'System.Security.Cryptography.Pkcs/10.0.10' = [ordered]@{
            runtime = [ordered]@{
                'lib/net8.0/System.Security.Cryptography.Pkcs.dll' = [ordered]@{}
            }
        }
    }

    $libraries = [ordered]@{}
    foreach ($key in $target.Keys) {
        $libraries[$key] = [ordered]@{ type = 'package' }
    }

    $document = [ordered]@{
        runtimeTarget = [ordered]@{
            name = '.NETCoreApp,Version=v8.0/win-x64'
        }
        targets = [ordered]@{
            '.NETCoreApp,Version=v8.0/win-x64' = $target
        }
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

    Write-PackageProps -Path $baselineProps -Versions @{
        'coverlet.collector' = '8.0.0'
        'Grpc.Net.Client' = '2.76.0'
        'Microsoft.Windows.Compatibility' = '10.0.3'
    }
    Write-PackageProps -Path $currentProps -Versions @{
        'coverlet.collector' = '10.0.1'
        'Grpc.Net.Client' = '2.80.0'
        'Microsoft.Windows.Compatibility' = '10.0.10'
    }
    $depsPath = New-ClientDependencyFixture -OutputDirectory $clientOutput

    & $stageRuntimeScript `
        -BuildMode client `
        -CurrentPackagesPath $currentProps `
        -BaselinePackagesPath $baselineProps `
        -UpstreamBaseCommit ('a' * 40) `
        -ClientDependenciesPath $depsPath `
        -ClientOutputDirectory $clientOutput `
        -StageDirectory $stageDir `
        -MetadataPath $metadataPath

    foreach ($file in @(
        'Grpc.Net.Client.dll',
        'Grpc.Core.Api.dll',
        'System.Security.Cryptography.Pkcs.dll'
    )) {
        Assert-Condition (Test-Path -LiteralPath (Join-Path $stageDir $file) -PathType Leaf) `
            "Runtime dependency closure omitted $file."
    }

    Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $stageDir 'coverlet.collector.dll'))) `
        'Test-only coverlet package leaked into runtime dependency staging.'

    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Condition (@($metadata.changedDirectPackages).Count -eq 3) `
        'Runtime dependency metadata did not record all changed direct package roots.'
    Assert-Condition (@($metadata.runtimePackages).Count -eq 4) `
        'Runtime dependency closure did not retain both changed roots and transitive runtime packages.'
    Assert-Condition (@($metadata.files).Count -eq 3) `
        'Runtime dependency metadata reported the wrong staged file count.'

    $runtimePackageNames = @($metadata.runtimePackages | ForEach-Object { [string] $_.id })
    Assert-Condition ($runtimePackageNames -contains 'Grpc.Core.Api') `
        'Runtime dependency closure did not follow a transitive package dependency.'
    Assert-Condition ($runtimePackageNames -contains 'System.Security.Cryptography.Pkcs') `
        'Runtime dependency closure did not follow the compatibility meta-package dependency.'

    return [ordered]@{
        Root = $fixture
        Stage = $stageDir
        Metadata = $metadataPath
    }
}

function Test-CompletePackaging {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary] $RuntimeFixture
    )

    $fixture = Join-Path $testRoot 'complete-package'
    $binDir = Join-Path $fixture 'bin'
    $clientDir = Join-Path $fixture 'client'
    $launcherDir = Join-Path $fixture 'launcher'
    $patchDir = Join-Path $fixture 'patch'
    $installerDir = Join-Path $fixture 'installer'
    $toolsDir = Join-Path $fixture 'tools'

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
        -TargetVersion '5.1.5' `
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
        -InstallerScriptPath $realInstallerScript `
        -InstallerLauncherPath $installerLauncher

    $manifestPath = Join-Path $patchDir 'patch-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $paths = @($manifest.files | ForEach-Object { [string] $_.path })

    Assert-Condition ([bool] $manifest.completeRuntimeCoverage) `
        'Complete FastPatch manifest does not declare complete runtime coverage.'
    Assert-Condition ([bool] $manifest.launcherIncluded) `
        'Complete FastPatch manifest does not record the application launcher.'
    Assert-Condition ([string] $manifest.upstreamBaseCommit -eq ('a' * 40)) `
        'Complete FastPatch manifest lost upstream dependency-baseline provenance.'
    Assert-Condition ([int] $manifest.runtimeDependencyFileCount -eq 3) `
        'Complete FastPatch manifest reported the wrong runtime dependency file count.'

    foreach ($path in @(
        'ProtonVPN.Launcher.exe',
        'Grpc.Net.Client.dll',
        'Grpc.Core.Api.dll',
        'System.Security.Cryptography.Pkcs.dll'
    )) {
        Assert-Condition ($paths -contains $path) `
            "Complete FastPatch manifest omitted $path."
    }

    & powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $realInstallerScript `
        -PatchPath $patchDir `
        -TargetVersion '5.1.5' `
        -ValidateOnly
    Assert-Condition ($LASTEXITCODE -eq 0) `
        'Installer rejected an untampered complete-runtime FastPatch payload.'

    $tamperedStage = Join-Path $fixture 'tampered-runtime-stage'
    Copy-Item -LiteralPath $RuntimeFixture.Stage -Destination $tamperedStage -Recurse
    Write-TestText (Join-Path $tamperedStage 'Grpc.Net.Client.dll') 'tampered'

    $tamperRejected = $false
    try {
        & $completePackager `
            -BuildMode client `
            -TargetVersion '5.1.5' `
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
            -InstallerScriptPath $realInstallerScript `
            -InstallerLauncherPath $installerLauncher
    }
    catch {
        if ($_.Exception.Message -like '*source hash mismatch*') {
            $tamperRejected = $true
        } else {
            throw
        }
    }
    Assert-Condition $tamperRejected `
        'Complete FastPatch packaging accepted a runtime dependency whose staged hash differed from metadata.'
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null

try {
    $runtimeFixture = Test-RuntimeDependencyClosure
    Test-CompletePackaging -RuntimeFixture $runtimeFixture
    Write-Host 'Complete FastPatch regression tests passed.'
}
finally {
    if ($ownsWorkingDirectory -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
