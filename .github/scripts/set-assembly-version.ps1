[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TargetVersion,

    [string] $AssemblyInfoPath = 'src/GlobalAssemblyInfo.cs',

    [string] $InformationalVersion = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$version = $TargetVersion.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "TargetVersion must be a numeric 3- or 4-part version such as 5.1.5 or 5.1.5.0. Received '$TargetVersion'."
}

$parsedVersion = [Version]::Parse($version)
$releaseVersion = '{0}.{1}.{2}' -f $parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build
$fileRevision = if ($parsedVersion.Revision -ge 0) { $parsedVersion.Revision } else { 0 }
$fileVersion = "$releaseVersion.$fileRevision"
$assemblyVersion = "$releaseVersion.0"

if ([string]::IsNullOrWhiteSpace($InformationalVersion)) {
    $informationalRevision = [Math]::Max(1, $fileRevision)
    $InformationalVersion = "$releaseVersion.$informationalRevision-marcmy-split-tunnel"
} else {
    $InformationalVersion = $InformationalVersion.Trim()
}

if ($InformationalVersion -match '["\r\n]' -or [string]::IsNullOrWhiteSpace($InformationalVersion)) {
    throw 'InformationalVersion must be a non-empty single-line value without double quotes.'
}

if (-not (Test-Path -LiteralPath $AssemblyInfoPath -PathType Leaf)) {
    throw "Assembly info file was not found: $AssemblyInfoPath"
}

$content = Get-Content -LiteralPath $AssemblyInfoPath -Raw

$replacements = [ordered]@{
    '\[assembly:\s*AssemblyVersion\("[^"]+"\)\]' = "[assembly: AssemblyVersion(`"$assemblyVersion`")]"
    '\[assembly:\s*AssemblyFileVersion\("[^"]+"\)\]' = "[assembly: AssemblyFileVersion(`"$fileVersion`")]"
    '\[assembly:\s*AssemblyInformationalVersion\("[^"]+"\)\]' = "[assembly: AssemblyInformationalVersion(`"$InformationalVersion`")]"
}

foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not [regex]::IsMatch($content, $replacement.Key)) {
        throw "Expected version attribute was not found in $AssemblyInfoPath using pattern: $($replacement.Key)"
    }

    $content = [regex]::Replace($content, $replacement.Key, $replacement.Value, 1)
}

Set-Content -LiteralPath $AssemblyInfoPath -Value $content -NoNewline
Write-Host "Set $AssemblyInfoPath version metadata: AssemblyVersion=$assemblyVersion; AssemblyFileVersion=$fileVersion; AssemblyInformationalVersion=$InformationalVersion."
