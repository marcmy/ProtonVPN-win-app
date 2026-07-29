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

    [switch] $RepeatServerHealth
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$logsDir = [System.IO.Path]::GetFullPath($LogsDirectory)
$testOutputRoot = [System.IO.Path]::GetFullPath($TestOutputDirectory)
New-Item -ItemType Directory -Force -Path $logsDir | Out-Null
New-Item -ItemType Directory -Force -Path $testOutputRoot | Out-Null

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

    if (-not (Test-Path -LiteralPath $ProjectPath -PathType Leaf)) {
        throw "Fork regression test project was not found: $ProjectPath"
    }

    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $logName = "$projectName$LogSuffix"
    $textLogPath = Join-Path $logsDir "$logName.log"
    $trxName = "$logName.trx"
    $testOutputDir = Join-Path $testOutputRoot $logName

    Remove-Item -LiteralPath $testOutputDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $testOutputDir | Out-Null

    Write-Host "Running fork regression project: $ProjectPath"
    dotnet test $ProjectPath `
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
