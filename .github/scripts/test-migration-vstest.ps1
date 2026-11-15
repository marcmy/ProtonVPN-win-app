[CmdletBinding()]
param(
    [ValidateSet('Client', 'Libraries', 'Service', 'Integration', 'All')]
    [string] $Group = 'All',
    [string] $Configuration = 'Release',
    [string] $Platform = 'x64',
    [string] $ArtifactsDirectory = 'artifacts/migration-tests'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not (Test-Path -LiteralPath (Join-Path $root 'src') -PathType Container)) {
    throw "Cannot find the repository source directory: $root"
}

$artifactRoot = if ([IO.Path]::IsPathRooted($ArtifactsDirectory)) {
    [IO.Path]::GetFullPath($ArtifactsDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $root $ArtifactsDirectory))
}
$logsDir = Join-Path $artifactRoot 'logs'
$resultsDir = Join-Path $artifactRoot 'results'
New-Item -ItemType Directory -Force -Path $logsDir, $resultsDir | Out-Null

$excluded = @(
    'ProtonVPN.Core.Tests',
    'ProtonVPN.ProcessCommunication.Common.Tests',
    'ProtonVPN.UI.Tests'
)

$projects = @(
    Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.Tests.csproj' |
        Where-Object { $_.BaseName -notin $excluded } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
            $testGroup = if ($relative -like 'src/Tests/ProtonVPN.Integration.Tests/*') {
                'Integration'
            } elseif ($relative -like 'src/Client/*') {
                'Client'
            } elseif ($relative -like 'src/Tests/*') {
                'Service'
            } else {
                'Libraries'
            }
            [pscustomobject]@{
                Name = $_.BaseName
                Path = $_.FullName
                RelativePath = $relative
                Group = $testGroup
            }
        } |
        Where-Object { $Group -eq 'All' -or $_.Group -eq $Group } |
        Sort-Object RelativePath
)
if ($projects.Count -eq 0) { throw "No supported test projects found for group '$Group'." }

Write-Host "Selected $($projects.Count) supported test projects for group '$Group'."
$results = [Collections.Generic.List[object]]::new()

foreach ($project in $projects) {
    $name = $project.Name
    $projectDir = Join-Path $resultsDir $name
    New-Item -ItemType Directory -Force -Path $projectDir | Out-Null

    $restoreLog = Join-Path $logsDir "$name.restore.log"
    $buildLog = Join-Path $logsDir "$name.build.log"
    $testLog = Join-Path $logsDir "$name.test.log"
    $trxName = "$name.trx"
    $trxPath = Join-Path $projectDir $trxName
    Remove-Item -LiteralPath $trxPath -Force -ErrorAction SilentlyContinue

    $status = 'PASS'
    $total = 0
    $passed = 0
    $failed = 0
    $skipped = 0
    $restoreExit = -1
    $buildExit = -1
    $testExit = -1
    $timer = [Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n===== [$Group] $name =====" -ForegroundColor Cyan

    $propertyArgs = @(
        "-p:Platform=$Platform"
    )

    # Explicit, serialized restore avoids partially restored project assets
    # and competing NuGet writes observed with implicit parallel restore.
    $restoreArgs = @(
        'restore', $project.Path,
        "-p:Platform=$Platform",
        '--disable-parallel', '--no-cache', '-m:1',
        '--verbosity', 'quiet'
    )
    & dotnet @restoreArgs *> $restoreLog
    $restoreExit = $LASTEXITCODE

    if ($restoreExit -ne 0) {
        $status = 'RESTORE FAILED'
        Get-Content -LiteralPath $restoreLog -Tail 25
    } else {
        $buildArgs = @(
            'build', $project.Path,
            '--configuration', $Configuration,
            '--no-restore', '--nologo', '--verbosity', 'quiet',
            '-m:1', '-p:BuildInParallel=false'
        ) + $propertyArgs

        & dotnet @buildArgs *> $buildLog
        $buildExit = $LASTEXITCODE
    }

    if ($restoreExit -eq 0 -and $buildExit -ne 0) {
        $status = 'BUILD FAILED'
        Get-Content -LiteralPath $buildLog -Tail 25
    } elseif ($restoreExit -eq 0 -and $buildExit -eq 0) {
        $targetArgs = @(
            'msbuild', $project.Path, '-nologo', '-getProperty:TargetPath',
            "-p:Configuration=$Configuration"
        ) + $propertyArgs

        $targetLines = @(& dotnet @targetArgs)
        $targetExit = $LASTEXITCODE
        $dll = if ($targetLines.Count -gt 0) { [string]$targetLines[-1] } else { '' }
        $dll = $dll.Trim()
        if (-not [string]::IsNullOrWhiteSpace($dll) -and -not [IO.Path]::IsPathRooted($dll)) {
            $dll = [IO.Path]::GetFullPath((Join-Path $root $dll))
        }

        if ($targetExit -ne 0 -or [string]::IsNullOrWhiteSpace($dll) -or
            -not (Test-Path -LiteralPath $dll -PathType Leaf)) {
            $status = 'TEST ASSEMBLY MISSING'
            Write-Warning "Test DLL was not found: $dll"
        } else {
            # Deliberately invoke VSTest directly: `dotnet test` previously
            # completed successfully here without executing any tests.
            $testArgs = @(
                'vstest', $dll,
                '--Logger:console;verbosity=normal',
                "--Logger:trx;LogFileName=$trxName",
                "--ResultsDirectory:$projectDir"
            )
            & dotnet @testArgs *> $testLog
            $testExit = $LASTEXITCODE

            if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
                try {
                    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
                    $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
                    if ($null -ne $counters) {
                        $total = [int]$counters.GetAttribute('total')
                        $passed = [int]$counters.GetAttribute('passed')
                        $failed = [int]$counters.GetAttribute('failed')
                        $skipped = [math]::Max(0, $total - $passed - $failed)
                    }
                } catch {
                    Write-Warning "Could not read TRX for $name`: $($_.Exception.Message)"
                }
            }
            if ($testExit -ne 0 -or $failed -gt 0) {
                $status = 'TEST FAILED'
            } elseif ($total -lt 1 -or $passed -lt 1) {
                $status = 'NO EXECUTED TESTS'
            } elseif ($total -ne ($passed + $failed + $skipped)) {
                $status = 'COUNT MISMATCH'
            }
            Get-Content -LiteralPath $testLog -Tail 8
        }
    }

    $timer.Stop()
    $results.Add([pscustomobject]@{
        Project = $name
        Group = $project.Group
        Status = $status
        Total = $total
        Passed = $passed
        Failed = $failed
        Skipped = $skipped
        RestoreExit = $restoreExit
        BuildExit = $buildExit
        TestExit = $testExit
        Seconds = [math]::Round($timer.Elapsed.TotalSeconds, 1)
    })
    Write-Host "$name`: $status ($passed passed, $failed failed, $skipped skipped)" -ForegroundColor $(if ($status -eq 'PASS') { 'Green' } else { 'Red' })
}

$csv = Join-Path $artifactRoot 'summary.csv'
$json = Join-Path $artifactRoot 'summary.json'
$results | Export-Csv -LiteralPath $csv
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $json -Encoding utf8

Write-Host "`n===== $Group SUMMARY =====" -ForegroundColor Cyan
$results | Format-Table Project, Status, Total, Passed, Failed, Skipped, Seconds -AutoSize

$bad = @($results | Where-Object Status -ne 'PASS')
Write-Host "Projects: $($results.Count), failures: $($bad.Count), tests passed: $(($results | Measure-Object Passed -Sum).Sum)"
Write-Host "Logs and TRX: $artifactRoot"
if ($bad.Count -gt 0) {
    throw "$($bad.Count) test project(s) did not pass: $(($bad.Project) -join ', ')"
}
