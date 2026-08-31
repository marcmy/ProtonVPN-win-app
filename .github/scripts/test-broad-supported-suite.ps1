[CmdletBinding()]
param(
    [ValidateSet('Client', 'Libraries', 'Service', 'Integration', 'All')]
    [string] $TestGroup = 'All',

    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Platform = 'x64',

    [ValidateNotNullOrEmpty()]
    [string] $ArtifactsDirectory = 'artifacts/broad-tests',

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

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'src') -PathType Container)) {
    throw "Repository root does not contain the Proton VPN source tree: $repositoryRoot"
}

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

function Convert-ToRepositoryRelativePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    return [IO.Path]::GetRelativePath($repositoryRoot, $Path).Replace('\', '/')
}

$focusedProjects = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
@(
    'src/Client/Localization/ProtonVPN.Client.Localization.Tests/ProtonVPN.Client.Localization.Tests.csproj',
    'src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj',
    'src/Client/Logic/Searches/ProtonVPN.Client.Logic.Searches.Tests/ProtonVPN.Client.Logic.Searches.Tests.csproj',
    'src/Client/Logic/Servers/ProtonVPN.Client.Logic.Servers.Tests/ProtonVPN.Client.Logic.Servers.Tests.csproj',
    'src/Client/Logic/Servers/ProtonVPN.Client.Logic.Servers.Mappers.Tests/ProtonVPN.Client.Logic.Servers.Mappers.Tests.csproj',
    'src/Client/Logic/Connection/ProtonVPN.Client.Logic.Connection.Tests/ProtonVPN.Client.Logic.Connection.Tests.csproj',
    'src/Tests/ProtonVPN.Vpn.Tests/ProtonVPN.Vpn.Tests.csproj',
    'src/Tests/ProtonVPN.Service.Tests/ProtonVPN.Service.Tests.csproj',
    'src/Tests/ProtonVPN.Update.Tests/ProtonVPN.Update.Tests.csproj',
    'src/Tests/ProtonVPN.Integration.Tests/ProtonVPN.Integration.Tests.csproj'
) | ForEach-Object { [void] $focusedProjects.Add($_) }

# Deliberate exclusions are surfaced in the generated inventory rather than silently skipped.
# The two legacy off-solution projects were discovered by the first broad Actions run and do
# not restore against the repository's current central-package graph. The credentialed FlaUI
# suite is upstream's dedicated production E2E lane, not a hosted-safe unit/component test.
$excludedProjects = @{
    'src/ProcessCommunication/ProtonVPN.ProcessCommunication.Common.Tests/ProtonVPN.ProcessCommunication.Common.Tests.csproj' = 'Legacy off-solution project; references Grpc.Core but the current central package graph no longer defines that package, so the project does not restore.'
    'src/Tests/ProtonVPN.Core.Tests/ProtonVPN.Core.Tests.csproj' = 'Legacy off-solution project; references System.Configuration.ConfigurationManager but the current central package graph no longer defines that package, so the project does not restore.'
    'src/Tests/ProtonVPN.UI.Tests/ProtonVPN.UI.Tests.csproj' = 'Dedicated FlaUI production E2E suite; requires an installed client plus production/test account credentials and network access.'
}

function Get-ProjectGroup {
    param([Parameter(Mandatory = $true)][string] $RelativePath)

    if ($RelativePath -eq 'src/Tests/ProtonVPN.Integration.Tests/ProtonVPN.Integration.Tests.csproj') {
        return 'Integration'
    }

    if ($RelativePath.StartsWith('src/Client/', [StringComparison]::OrdinalIgnoreCase)) {
        return 'Client'
    }

    if ($RelativePath.StartsWith('src/Tests/', [StringComparison]::OrdinalIgnoreCase)) {
        return 'Service'
    }

    return 'Libraries'
}

function Get-TargetFramework {
    param([Parameter(Mandatory = $true)][string] $ProjectPath)

    try {
        [xml] $project = Get-Content -LiteralPath $ProjectPath -Raw
        $framework = [string] $project.Project.PropertyGroup.TargetFramework
        if ([string]::IsNullOrWhiteSpace($framework)) {
            $framework = [string] $project.Project.PropertyGroup.TargetFrameworks
        }
        return $framework
    } catch {
        return ''
    }
}

$solutionPath = Resolve-RepositoryPath 'ProtonVPN.slnx'
$solutionText = if (Test-Path -LiteralPath $solutionPath -PathType Leaf) {
    Get-Content -LiteralPath $solutionPath -Raw
} else {
    ''
}

$testProjects = @(
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.BaseName.EndsWith('.Tests', [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            $relativePath = Convert-ToRepositoryRelativePath $_.FullName
            $isExcluded = $excludedProjects.ContainsKey($relativePath)
            $group = Get-ProjectGroup $relativePath
            [pscustomobject]@{
                Project = $relativePath
                Group = $group
                TargetFramework = Get-TargetFramework $_.FullName
                InSolution = $solutionText.Contains($relativePath, [StringComparison]::OrdinalIgnoreCase)
                InFocusedSuite = $focusedProjects.Contains($relativePath)
                SupportedHostedCI = -not $isExcluded
                ExclusionReason = if ($isExcluded) { $excludedProjects[$relativePath] } else { '' }
            }
        } |
        Sort-Object Project
)

if ($testProjects.Count -eq 0) {
    throw 'No test projects were discovered under src.'
}

$artifactsRoot = Resolve-RepositoryPath $ArtifactsDirectory
$inventoryDirectory = Join-Path $artifactsRoot 'inventory'
New-Item -ItemType Directory -Force -Path $inventoryDirectory | Out-Null

$inventoryJsonPath = Join-Path $inventoryDirectory 'test-project-inventory.json'
$testProjects | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inventoryJsonPath -Encoding utf8

$inventoryMarkdownPath = Join-Path $inventoryDirectory 'test-project-inventory.md'
$inventoryLines = [Collections.Generic.List[string]]::new()
$inventoryLines.Add('# Proton VPN test-project inventory')
$inventoryLines.Add('')
$inventoryLines.Add("Discovered **$($testProjects.Count)** test projects under `src`: **$(@($testProjects | Where-Object SupportedHostedCI).Count)** hosted-supported and **$(@($testProjects | Where-Object { -not $_.SupportedHostedCI }).Count)** explicitly excluded.")
$inventoryLines.Add('')
$inventoryLines.Add('| Project | Group | In solution | Focused PR suite | Hosted nightly | Notes |')
$inventoryLines.Add('| --- | --- | :---: | :---: | :---: | --- |')
foreach ($project in $testProjects) {
    $notes = if ($project.SupportedHostedCI) { '' } else { $project.ExclusionReason.Replace('|', '\|') }
    $inventoryLines.Add("| ``$($project.Project)`` | $($project.Group) | $($project.InSolution) | $($project.InFocusedSuite) | $($project.SupportedHostedCI) | $notes |")
}
$inventoryLines.Add('')
$inventoryLines.Add('`ProtonVPN.Tests.Common` is a shared test-support library, not a test project, and is therefore not counted above.')
$inventoryLines | Set-Content -LiteralPath $inventoryMarkdownPath -Encoding utf8

$selectedProjects = @(
    $testProjects | Where-Object {
        $_.SupportedHostedCI -and ($TestGroup -eq 'All' -or $_.Group -eq $TestGroup)
    }
)

if ($selectedProjects.Count -eq 0) {
    throw "No hosted-supported test projects selected for group '$TestGroup'."
}

$groupDirectory = Join-Path $artifactsRoot $TestGroup
New-Item -ItemType Directory -Force -Path $groupDirectory | Out-Null

$failures = [Collections.Generic.List[string]]::new()
$projectResults = [Collections.Generic.List[object]]::new()
$groupStopwatch = [Diagnostics.Stopwatch]::StartNew()

foreach ($project in $selectedProjects) {
    $projectPath = Resolve-RepositoryPath $project.Project
    $projectName = [IO.Path]::GetFileNameWithoutExtension($project.Project)
    $projectDirectory = Join-Path $groupDirectory $projectName
    New-Item -ItemType Directory -Force -Path $projectDirectory | Out-Null

    $textLogPath = Join-Path $projectDirectory "$projectName.log"
    $trxName = "$projectName.trx"
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()

    Write-Host "Running broad supported test project: $($project.Project)"
    & dotnet test $projectPath `
        --configuration $Configuration `
        "-p:Platform=$Platform" `
        -p:RestoreUseStaticGraphEvaluation=true `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $projectDirectory `
        --nologo `
        --verbosity minimal *>&1 |
        Tee-Object -FilePath $textLogPath

    $exitCode = $LASTEXITCODE
    $stopwatch.Stop()

    $trxPath = Join-Path $projectDirectory $trxName
    $total = 0
    $passed = 0
    $failed = 0
    $skipped = 0
    if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
        try {
            [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
            $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
            if ($null -ne $counters) {
                $total = [int] $counters.total
                $passed = [int] $counters.passed
                $failed = [int] $counters.failed
                $skipped = [int] $counters.notExecuted
            }
        } catch {
            Write-Warning "Could not parse TRX counters for $($project.Project): $($_.Exception.Message)"
        }
    }

    $projectResults.Add([pscustomobject]@{
        Project = $project.Project
        ExitCode = $exitCode
        Total = $total
        Passed = $passed
        Failed = $failed
        Skipped = $skipped
        DurationSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 1)
    })

    if ($exitCode -ne 0) {
        $failures.Add($project.Project)
        Write-Error "Broad supported test project failed with exit code $exitCode`: $($project.Project)" -ErrorAction Continue
    }
}

$groupStopwatch.Stop()
$summary = [pscustomobject]@{
    Group = $TestGroup
    ProjectCount = $selectedProjects.Count
    TotalTests = ($projectResults | Measure-Object -Property Total -Sum).Sum
    Passed = ($projectResults | Measure-Object -Property Passed -Sum).Sum
    Failed = ($projectResults | Measure-Object -Property Failed -Sum).Sum
    Skipped = ($projectResults | Measure-Object -Property Skipped -Sum).Sum
    DurationSeconds = [math]::Round($groupStopwatch.Elapsed.TotalSeconds, 1)
    FailedProjects = @($failures)
    Projects = @($projectResults)
}

$summaryJsonPath = Join-Path $groupDirectory 'summary.json'
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryJsonPath -Encoding utf8

$summaryMarkdownPath = Join-Path $groupDirectory 'summary.md'
$summaryLines = @(
    "## Broad tests — $TestGroup",
    '',
    "- Projects: **$($summary.ProjectCount)**",
    "- Tests: **$($summary.TotalTests)** total, **$($summary.Passed)** passed, **$($summary.Failed)** failed, **$($summary.Skipped)** skipped",
    "- Wall time: **$($summary.DurationSeconds) seconds**",
    "- Failed projects: **$($summary.FailedProjects.Count)**",
    '',
    '| Project | Tests | Passed | Failed | Skipped | Seconds |',
    '| --- | ---: | ---: | ---: | ---: | ---: |'
)
foreach ($result in $projectResults) {
    $summaryLines += "| ``$($result.Project)`` | $($result.Total) | $($result.Passed) | $($result.Failed) | $($result.Skipped) | $($result.DurationSeconds) |"
}
$summaryLines | Set-Content -LiteralPath $summaryMarkdownPath -Encoding utf8

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    Get-Content -LiteralPath $summaryMarkdownPath | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) broad supported test project(s) failed: $($failures -join ', ')"
}

Write-Host "Completed $($selectedProjects.Count) hosted-supported test projects for group $TestGroup."
