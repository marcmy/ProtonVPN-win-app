[CmdletBinding()]
param(
    [ValidateSet('Client', 'Service', 'All')]
    [string] $Scope = 'All',

    [ValidateNotNullOrEmpty()]
    [string] $Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string] $Platform = 'x64',

    [ValidateNotNullOrEmpty()]
    [string] $LogsDirectory = 'artifacts/logs',

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
$testOutputRoot = Resolve-RepositoryPath $TestOutputDirectory
$normalizedRepositoryRoot = $repositoryRoot.TrimEnd('\', '/')
$normalizedTestOutputRoot = $testOutputRoot.TrimEnd('\', '/')
$testOutputPrefix = "$normalizedRepositoryRoot$([System.IO.Path]::DirectorySeparatorChar)"
$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src')).TrimEnd('\', '/')
$sourcePrefix = "$sourceRoot$([System.IO.Path]::DirectorySeparatorChar)"
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\', '/')
$temporaryPrefix = "$temporaryRoot$([System.IO.Path]::DirectorySeparatorChar)"

if (-not $normalizedTestOutputRoot.StartsWith(
        $testOutputPrefix,
        [System.StringComparison]::OrdinalIgnoreCase) -and
    -not $normalizedTestOutputRoot.StartsWith(
        $temporaryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Test output must be below the repository root or temporary directory: $testOutputRoot"
}

if ($normalizedTestOutputRoot.Equals(
        $normalizedRepositoryRoot,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    $normalizedTestOutputRoot.Equals(
        $temporaryRoot,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Test output must not be a broad cleanup root: $testOutputRoot"
}

if ($normalizedTestOutputRoot.StartsWith(
        $sourcePrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Test output must not be written inside the source tree: $testOutputRoot"
}

New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
New-Item -ItemType Directory -Force -Path $testOutputRoot | Out-Null

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

$clientProjects = @(
    'src/Client/Localization/ProtonVPN.Client.Localization.Tests/ProtonVPN.Client.Localization.Tests.csproj',
    'src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj',
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

$crossCuttingProjects = @(
    'src/Tests/ProtonVPN.Integration.Tests/ProtonVPN.Integration.Tests.csproj'
)

$projects = @()
if ($Scope -in @('Client', 'All')) {
    $projects += $clientProjects
}
if ($Scope -in @('Service', 'All')) {
    $projects += $serviceProjects
}
$projects += $crossCuttingProjects
$projects = @($projects | Select-Object -Unique)

function Invoke-TestProject {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ProjectPath,

        [string] $LogSuffix = ''
    )

    $resolvedProjectPath = Resolve-RepositoryPath $ProjectPath
    if (-not (Test-Path -LiteralPath $resolvedProjectPath -PathType Leaf)) {
        throw "Fork regression test project was not found: $resolvedProjectPath"
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $logName = "$projectName$LogSuffix"
    $textLogPath = Join-Path $logsDir "$logName.log"
    $trxName = "$logName.trx"
    $testOutputDir = Join-Path $testOutputRoot $logName

    Remove-Item -LiteralPath $testOutputDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $testOutputDir | Out-Null

    Write-Host "Running fork regression project: $ProjectPath"
    dotnet test $resolvedProjectPath `
        --configuration $Configuration `
        -p:Platform=$Platform `
        -p:OutputPath=$testOutputDir `
        -p:AppendTargetFrameworkToOutputPath=false `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $logsDir `
        --nologo `
        --verbosity minimal *>&1 |
        Tee-Object -FilePath $textLogPath

    if ($LASTEXITCODE -ne 0) {
        throw "Fork regression project failed with exit code $LASTEXITCODE`: $ProjectPath"
    }
}

foreach ($project in $projects) {
    Invoke-TestProject -ProjectPath $project
}

if ($RepeatServerHealth -and $Scope -in @('Client', 'All')) {
    Invoke-TestProject `
        -ProjectPath 'src/Client/Common/ProtonVPN.Client.Common.UI.Tests/ProtonVPN.Client.Common.UI.Tests.csproj' `
        -LogSuffix '-repeat'
}

Write-Host "Completed $($projects.Count) fork regression test projects for scope $Scope."
