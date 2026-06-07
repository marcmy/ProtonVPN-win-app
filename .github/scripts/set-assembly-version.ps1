[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $TargetVersion,

    [string] $AssemblyInfoPath = 'src/GlobalAssemblyInfo.cs'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$version = $TargetVersion.Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "TargetVersion must be a numeric 3- or 4-part version such as 4.4.1 or 4.4.1.0. Received '$TargetVersion'."
}

$fileVersion = $version
if (($fileVersion.ToCharArray() | Where-Object { $_ -eq '.' }).Count -eq 2) {
    $fileVersion = "$fileVersion.0"
}

$assemblyVersion = [regex]::Replace($fileVersion, '\.\d+$', '.0')

if (-not (Test-Path -LiteralPath $AssemblyInfoPath -PathType Leaf)) {
    throw "Assembly info file was not found: $AssemblyInfoPath"
}

$content = Get-Content -LiteralPath $AssemblyInfoPath -Raw

$replacements = [ordered]@{
    '\[assembly:\s*AssemblyVersion\("[^"]+"\)\]' = "[assembly: AssemblyVersion(`"$assemblyVersion`")]"
    '\[assembly:\s*AssemblyFileVersion\("[^"]+"\)\]' = "[assembly: AssemblyFileVersion(`"$fileVersion`")]"
    '\[assembly:\s*AssemblyInformationalVersion\("[^"]+"\)\]' = "[assembly: AssemblyInformationalVersion(`"$version`")]"
}

foreach ($replacement in $replacements.GetEnumerator()) {
    if (-not [regex]::IsMatch($content, $replacement.Key)) {
        throw "Expected version attribute was not found in $AssemblyInfoPath using pattern: $($replacement.Key)"
    }

    $content = [regex]::Replace($content, $replacement.Key, $replacement.Value, 1)
}

Set-Content -LiteralPath $AssemblyInfoPath -Value $content -NoNewline
Write-Host "Set $AssemblyInfoPath assembly version metadata to $version ($assemblyVersion)."
