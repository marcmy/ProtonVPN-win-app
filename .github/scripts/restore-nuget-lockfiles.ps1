#requires -Version 7
$ErrorActionPreference = 'Stop'
$projects = @(
  'src/Client/ProtonVPN.Client/ProtonVPN.Client.csproj',
  'src/ProtonVPN.Service/ProtonVPN.Service.csproj',
  'src/ProtonVPN.Launcher/ProtonVPN.Launcher.csproj'
)
$arguments = @('--runtime','win-x64','-p:Platform=x64',
  '-p:RestorePackagesWithLockFile=true','-p:NuGetAuditMode=all',
  '--disable-parallel','-m:1')
foreach ($project in $projects) {
  if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Missing supported project: $project"
  }
  Write-Host "Restoring $project with NuGet lockfiles..."
  & dotnet restore $project @arguments
  if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
}
$locks = @(Get-ChildItem -LiteralPath src -Filter packages.lock.json -Recurse -File)
if ($locks.Count -lt 3) { throw "Expected >=3 locks, got $($locks.Count)" }
Write-Host "Generated $($locks.Count) resolved NuGet lockfiles"
