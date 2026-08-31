[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Platform = 'x64',

    [ValidateNotNullOrEmpty()]
    [string] $ArtifactsDirectory = 'artifacts/fork-coverage',

    [string] $RepositoryRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$scriptRepositoryCandidate = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repositoryRoot = if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [IO.Path]::GetFullPath($RepositoryRoot)
} elseif ((Test-Path -LiteralPath (Join-Path $scriptRepositoryCandidate 'src') -PathType Container) -and
          (Test-Path -LiteralPath (Join-Path $scriptRepositoryCandidate '.github') -PathType Container)) {
    $scriptRepositoryCandidate
} else {
    [IO.Path]::GetFullPath([Environment]::CurrentDirectory)
}

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

$coverageProjects = @(
    'src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj',
    'src/Client/Logic/Connection/ProtonVPN.Client.Logic.Connection.Tests/ProtonVPN.Client.Logic.Connection.Tests.csproj',
    'src/Client/Logic/Servers/ProtonVPN.Client.Logic.Servers.Tests/ProtonVPN.Client.Logic.Servers.Tests.csproj',
    'src/Client/Logic/Servers/ProtonVPN.Client.Logic.Servers.Mappers.Tests/ProtonVPN.Client.Logic.Servers.Mappers.Tests.csproj',
    'src/Tests/ProtonVPN.Vpn.Tests/ProtonVPN.Vpn.Tests.csproj',
    'src/Tests/ProtonVPN.Service.Tests/ProtonVPN.Service.Tests.csproj',
    'src/Tests/ProtonVPN.Integration.Tests/ProtonVPN.Integration.Tests.csproj'
)

# These are reporting selectors, not thresholds. They intentionally map to fork-sensitive
# functionality rather than pretending repository-wide coverage is a useful quality gate.
$focusAreas = [ordered]@{
    'Guest Hole / connection transitions' = 'GuestHole|ConnectionManager'
    'Server-health / server refresh' = 'ServerHealth|HealthProbe|ServerListUpdater'
    'NAT-PMP / port mapping' = 'PortMapping|NatPmp|NAT.?PMP|UdpClientWrapper'
    'Endpoint candidate/scanner' = 'VpnEndpoint|PortScanner|UdpPing'
    'App port-forwarding route shim' = 'PortForwardingForApps|PortForwarding.*Route|NatPmp.*Route'
    'Domain split tunneling' = 'Domain.*Split|SplitTunnel.*Domain|DomainRoute'
    'Certificate IPC hardening' = 'ConnectionCertificate|TlsCredentials'
    'Connection-card presentation' = 'ConnectionCardComponentViewModel'
}

$artifactsRoot = Resolve-RepositoryPath $ArtifactsDirectory
$reportsDirectory = Join-Path $artifactsRoot 'reports'
$logsDirectory = Join-Path $artifactsRoot 'logs'
New-Item -ItemType Directory -Force -Path $reportsDirectory, $logsDirectory | Out-Null

$projectCoverage = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()

foreach ($project in $coverageProjects) {
    $projectPath = Resolve-RepositoryPath $project
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Coverage test project not found: $project"
    }

    $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
    $resultDirectory = Join-Path $artifactsRoot ("results/" + $projectName)
    New-Item -ItemType Directory -Force -Path $resultDirectory | Out-Null
    $logPath = Join-Path $logsDirectory "$projectName.log"

    Write-Host "Collecting coverage through: $project"
    & dotnet test $projectPath `
        --configuration $Configuration `
        "-p:Platform=$Platform" `
        -p:RestoreUseStaticGraphEvaluation=true `
        --collect 'Code Coverage;Format=Cobertura' `
        --logger "trx;LogFileName=$projectName.trx" `
        --results-directory $resultDirectory `
        --nologo `
        --verbosity minimal *>&1 |
        Tee-Object -FilePath $logPath

    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $failures.Add($project)
        Write-Error "Coverage test project failed with exit code $exitCode`: $project" -ErrorAction Continue
        continue
    }

    $coverageFile = Get-ChildItem -LiteralPath $resultDirectory -Recurse -File -Filter '*.cobertura.xml' |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $coverageFile) {
        $failures.Add($project)
        Write-Error "No Cobertura report was produced for $project" -ErrorAction Continue
        continue
    }

    $destination = Join-Path $reportsDirectory "$projectName.cobertura.xml"
    Copy-Item -LiteralPath $coverageFile.FullName -Destination $destination -Force

    [xml] $coverage = Get-Content -LiteralPath $destination -Raw
    $root = $coverage.coverage
    $lineRate = if ($null -ne $root.'line-rate') { [double] $root.'line-rate' } else { 0.0 }
    $branchRate = if ($null -ne $root.'branch-rate') { [double] $root.'branch-rate' } else { 0.0 }
    $projectCoverage.Add([pscustomobject]@{
        Project = $project
        Report = [IO.Path]::GetFileName($destination)
        LinePercent = [math]::Round($lineRate * 100.0, 2)
        BranchPercent = [math]::Round($branchRate * 100.0, 2)
    })
}

if ($projectCoverage.Count -eq 0) {
    throw 'No readable Cobertura coverage reports were produced.'
}

$classCoverage = @()
foreach ($report in Get-ChildItem -LiteralPath $reportsDirectory -File -Filter '*.cobertura.xml') {
    [xml] $coverage = Get-Content -LiteralPath $report.FullName -Raw
    $classes = @($coverage.SelectNodes("//*[local-name()='class']"))
    foreach ($class in $classes) {
        $name = [string] $class.name
        $filename = [string] $class.filename
        if ($name -match '\.Tests?(\.|$)' -or $filename -match 'Tests?[\\/]') {
            continue
        }

        $lineRate = if ($null -ne $class.'line-rate') { [double] $class.'line-rate' } else { 0.0 }
        $branchRate = if ($null -ne $class.'branch-rate') { [double] $class.'branch-rate' } else { 0.0 }
        $classCoverage += [pscustomobject]@{
            Report = $report.Name
            Class = $name
            File = $filename
            LinePercent = [math]::Round($lineRate * 100.0, 2)
            BranchPercent = [math]::Round($branchRate * 100.0, 2)
        }
    }
}

$focusRows = [Collections.Generic.List[object]]::new()
foreach ($entry in $focusAreas.GetEnumerator()) {
    $matches = @($classCoverage | Where-Object { $_.Class -match $entry.Value -or $_.File -match $entry.Value })
    if ($matches.Count -eq 0) {
        $focusRows.Add([pscustomobject]@{
            Area = $entry.Key
            Status = 'No matching class in selected coverage reports'
            BestLinePercent = $null
            BestBranchPercent = $null
            ExampleClass = ''
        })
        continue
    }

    $best = $matches | Sort-Object LinePercent, BranchPercent -Descending | Select-Object -First 1
    $focusRows.Add([pscustomobject]@{
        Area = $entry.Key
        Status = if ($best.LinePercent -eq 0) { 'Matched, effectively uncovered' } else { 'Matched' }
        BestLinePercent = $best.LinePercent
        BestBranchPercent = $best.BranchPercent
        ExampleClass = $best.Class
    })
}

$zeroCoverageClasses = @(
    $classCoverage |
        Where-Object { $_.LinePercent -eq 0 } |
        Sort-Object Class -Unique |
        Select-Object -First 100
)

$summary = [pscustomobject]@{
    ProjectReports = @($projectCoverage)
    FocusAreas = @($focusRows)
    ZeroCoverageClassSample = @($zeroCoverageClasses)
    FailedProjects = @($failures)
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactsRoot 'coverage-summary.json') -Encoding utf8

$summaryLines = [Collections.Generic.List[string]]::new()
$summaryLines.Add('## Fork-focused .NET coverage')
$summaryLines.Add('')
$summaryLines.Add('Coverage is informational only. There is deliberately **no repository-wide percentage gate**.')
$summaryLines.Add('')
$summaryLines.Add('| Test project | Line | Branch |')
$summaryLines.Add('| --- | ---: | ---: |')
foreach ($row in $projectCoverage) {
    $summaryLines.Add("| ``$($row.Project)`` | $($row.LinePercent)% | $($row.BranchPercent)% |")
}
$summaryLines.Add('')
$summaryLines.Add('### Fork-sensitive areas')
$summaryLines.Add('')
$summaryLines.Add('| Area | Result | Best line | Best branch | Example matching class |')
$summaryLines.Add('| --- | --- | ---: | ---: | --- |')
foreach ($row in $focusRows) {
    $line = if ($null -eq $row.BestLinePercent) { '—' } else { "$($row.BestLinePercent)%" }
    $branch = if ($null -eq $row.BestBranchPercent) { '—' } else { "$($row.BestBranchPercent)%" }
    $example = if ([string]::IsNullOrWhiteSpace($row.ExampleClass)) { '—' } else { "``$($row.ExampleClass)``" }
    $summaryLines.Add("| $($row.Area) | $($row.Status) | $line | $branch | $example |")
}
$summaryLines.Add('')
$summaryLines.Add('Branch coverage is reported when the Microsoft Code Coverage collector emits Cobertura branch data; it is especially useful for transition/state-machine code, but remains non-blocking.')
$summaryLines | Set-Content -LiteralPath (Join-Path $artifactsRoot 'coverage-summary.md') -Encoding utf8

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summaryLines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) coverage test project(s) failed or produced no Cobertura report: $($failures -join ', ')"
}

Write-Host "Produced $($projectCoverage.Count) Cobertura reports with fork-focused coverage summary."
