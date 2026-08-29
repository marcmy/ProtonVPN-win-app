[CmdletBinding()]
param(
    [ValidateSet('Client', 'Service', 'All')]
    [string] $Scope = 'All',

    [ValidateSet('All', 'Ui', 'Core', 'Integration')]
    [string] $TestGroup = 'All',

    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Platform = 'x64',

    [ValidateNotNullOrEmpty()]
    [string] $LogsDirectory = 'artifacts/logs',

    # Kept for compatibility with existing callers. Regression tests now use
    # normal project output paths so MSBuild can reuse shared dependencies.
    [ValidateNotNullOrEmpty()]
    [string] $TestOutputDirectory = 'artifacts/test-bin',

    [string] $RepositoryRoot = '',

    [switch] $RepeatServerHealth
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

function Resolve-RepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

$scriptRepositoryCandidate = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$repositoryRoot = if (-not [string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    [System.IO.Path]::GetFullPath($RepositoryRoot)
} elseif ((Test-Path -LiteralPath (Join-Path $scriptRepositoryCandidate 'src') -PathType Container) -and
          (Test-Path -LiteralPath (Join-Path $scriptRepositoryCandidate '.github') -PathType Container)) {
    $scriptRepositoryCandidate
} else {
    [System.IO.Path]::GetFullPath([System.Environment]::CurrentDirectory)
}

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'src') -PathType Container)) {
    throw "Repository root does not contain the Proton VPN source tree: $repositoryRoot"
}

$logsDir = Resolve-RepositoryPath $LogsDirectory
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null

if ($Scope -in @('Client', 'All')) {
    $sidebarSearchPath = Resolve-RepositoryPath 'src/Client/ProtonVPN.Client/UI/Main/Sidebar/SidebarComponentViewModel.cs'
    $sidebarSearchContent = Get-Content -LiteralPath $sidebarSearchPath -Raw
    if ($sidebarSearchContent.Contains('.Wait()')) {
        throw 'Sidebar search must not synchronously wait on async work; this can deadlock the WinUI synchronization context.'
    }

    $globalSearchPath = Resolve-RepositoryPath 'src/Client/Logic/Searches/ProtonVPN.Client.Logic.Searches/GlobalSearch.cs'
    $globalSearchContent = Get-Content -LiteralPath $globalSearchPath -Raw
    if ($globalSearchContent.Contains('Task.WaitAll(')) {
        throw 'GlobalSearch must not block on Task.WaitAll; await the search tasks asynchronously instead.'
    }
}

$uiProjects = @(
    'src/Client/Localization/ProtonVPN.Client.Localization.Tests/ProtonVPN.Client.Localization.Tests.csproj',
    'src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj'
)

$coreClientProjects = @(
    'src/Client/Logic/Searches/ProtonVPN.Client.Logic.Searches.Tests/ProtonVPN.Client.Logic.Searches.Tests.csproj',
    'src/Client/Logic/Servers/ProtonVPN.Client.Logic.Servers.Tests/ProtonVPN.Client.Logic.Servers.Tests.csproj',
    'src/Client/Logic/Servers/ProtonVPN.Client.Logic.Servers.Mappers.Tests/ProtonVPN.Client.Logic.Servers.Mappers.Tests.csproj',
    'src/Client/Logic/Connection/ProtonVPN.Client.Logic.Connection.Tests/ProtonVPN.Client.Logic.Connection.Tests.csproj'
)

$serviceProjects = @(
    'src/Tests/ProtonVPN.Vpn.Tests/ProtonVPN.Vpn.Tests.csproj',
    'src/Tests/ProtonVPN.Service.Tests/ProtonVPN.Service.Tests.csproj',
    'src/Tests/ProtonVPN.Update.Tests/ProtonVPN.Update.Tests.csproj'
)

$integrationProjects = @(
    'src/Tests/ProtonVPN.Integration.Tests/ProtonVPN.Integration.Tests.csproj'
)

$projects = @()
switch ($TestGroup) {
    'Ui' {
        if ($Scope -in @('Client', 'All')) {
            $projects += $uiProjects
        }
    }
    'Core' {
        if ($Scope -in @('Client', 'All')) {
            $projects += $coreClientProjects
        }
        if ($Scope -in @('Service', 'All')) {
            $projects += $serviceProjects
        }
    }
    'Integration' {
        $projects += $integrationProjects
    }
    default {
        if ($Scope -in @('Client', 'All')) {
            $projects += $uiProjects
            $projects += $coreClientProjects
        }
        if ($Scope -in @('Service', 'All')) {
            $projects += $serviceProjects
        }
        $projects += $integrationProjects
    }
}
$projects = @($projects | Select-Object -Unique)

if ($projects.Count -eq 0) {
    throw "No regression projects selected for scope '$Scope' and test group '$TestGroup'."
}

function Invoke-TestProject {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectPath,

        [string] $LogSuffix = '',

        [switch] $NoBuild
    )

    $resolvedProjectPath = Resolve-RepositoryPath $ProjectPath
    if (-not (Test-Path -LiteralPath $resolvedProjectPath -PathType Leaf)) {
        throw "Fork regression test project was not found: $resolvedProjectPath"
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $logName = "$projectName$LogSuffix"
    $textLogPath = Join-Path $logsDir "$logName.log"
    $trxName = "$logName.trx"

    $dotnetArguments = @(
        'test',
        $resolvedProjectPath,
        '--configuration', $Configuration,
        "-p:Platform=$Platform",
        '-p:RestoreUseStaticGraphEvaluation=true',
        '--logger', "trx;LogFileName=$trxName",
        '--results-directory', $logsDir,
        '--nologo',
        '--verbosity', 'minimal'
    )

    if ($NoBuild) {
        $dotnetArguments += '--no-build'
        $dotnetArguments += '--no-restore'
    }

    Write-Host "Running fork regression project: $ProjectPath"
    & dotnet @dotnetArguments *>&1 |
        Tee-Object -FilePath $textLogPath

    if ($LASTEXITCODE -ne 0) {
        throw "Fork regression project failed with exit code $LASTEXITCODE`: $ProjectPath"
    }
}

foreach ($project in $projects) {
    Invoke-TestProject -ProjectPath $project
}

if ($RepeatServerHealth -and $Scope -in @('Client', 'All') -and $TestGroup -in @('All', 'Ui')) {
    Invoke-TestProject `
        -ProjectPath 'src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj' `
        -LogSuffix '-repeat' `
        -NoBuild
}

Write-Host "Completed $($projects.Count) fork regression test projects for scope $Scope and test group $TestGroup."
