from pathlib import Path
import re


def replace_exact(text, old, new, label, count=1):
    actual = text.count(old)
    if actual != count:
        raise RuntimeError(f"{label}: expected {count} occurrence(s), found {actual}")
    return text.replace(old, new)


def transform_installer(path):
    text = path.read_text(encoding="utf-8-sig")

    text = replace_exact(
        text,
        "    [string] $TrustedStagePath = '',\n\n    [switch] $PauseBeforeExit",
        "    [string] $ExpectedPatchArchiveSha256 = '',\n\n    [string] $TrustedStagePath = '',\n\n    [switch] $PauseBeforeExit",
        f"{path}: archive hash parameter")

    text = replace_exact(
        text,
        "$ErrorActionPreference = 'Stop'\n",
        "$ErrorActionPreference = 'Stop'\n\n"
        "$script:FastPatchInvocationScriptText = ''\n"
        "$scriptContentsProperty = $MyInvocation.MyCommand.PSObject.Properties['ScriptContents']\n"
        "if ($null -ne $scriptContentsProperty -and\n"
        "    -not [string]::IsNullOrWhiteSpace([string] $scriptContentsProperty.Value)) {\n"
        "    $script:FastPatchInvocationScriptText = [string] $scriptContentsProperty.Value\n"
        "}\n"
        "if ([string]::IsNullOrWhiteSpace($script:FastPatchInvocationScriptText)) {\n"
        "    $verifiedSfxScript = Get-Variable `\n"
        "        -Name 'ProtonVpnFastPatchVerifiedSfxScriptText' `\n"
        "        -Scope Global `\n"
        "        -ErrorAction SilentlyContinue\n"
        "    if ($null -ne $verifiedSfxScript -and\n"
        "        -not [string]::IsNullOrWhiteSpace([string] $verifiedSfxScript.Value)) {\n"
        "        $script:FastPatchInvocationScriptText = [string] $verifiedSfxScript.Value\n"
        "    }\n"
        "}\n"
        "if ([string]::IsNullOrWhiteSpace($script:FastPatchInvocationScriptText)) {\n"
        "    throw 'FastPatch could not capture the installer script bytes for trusted staging.'\n"
        "}\n",
        f"{path}: invocation script capture")

    archive_pattern = re.compile(
        r"(?P<i>        )\$expandedPath = Join-Path \$WorkingDirectory 'ExpandedPatch'\n"
        r"(?P=i)New-Item -ItemType Directory -Path \$expandedPath -Force \| Out-Null\n"
        r"(?P=i)Expand-Archive -LiteralPath \$resolvedPatchPath -DestinationPath \$expandedPath -Force\n"
        r"(?P=i)return Resolve-PayloadRoot -Root \$expandedPath")
    match = archive_pattern.search(text)
    if not match:
        raise RuntimeError(f"{path}: archive expansion block not found")
    indent = match.group('i')
    replacement = f"""{indent}$archiveGuard = $null
{indent}try {{
{indent}    if (-not [string]::IsNullOrWhiteSpace($ExpectedPatchArchiveSha256)) {{
{indent}        $expectedArchiveHash = $ExpectedPatchArchiveSha256.Trim().ToLowerInvariant()
{indent}        if ($expectedArchiveHash -notmatch '^[0-9a-f]{{64}}$') {{
{indent}            throw \"ExpectedPatchArchiveSha256 is invalid: $ExpectedPatchArchiveSha256\"
{indent}        }}

{indent}        # Keep a read-only sharing handle open through Expand-Archive. Other readers are
{indent}        # allowed, but same-user writers/deleters cannot replace the hash-pinned archive
{indent}        # between verification and extraction.
{indent}        $archiveGuard = [IO.File]::Open(
{indent}            $resolvedPatchPath,
{indent}            [IO.FileMode]::Open,
{indent}            [IO.FileAccess]::Read,
{indent}            [IO.FileShare]::Read)
{indent}        $sha256 = [Security.Cryptography.SHA256]::Create()
{indent}        try {{
{indent}            $actualArchiveHash = ([BitConverter]::ToString(
{indent}                $sha256.ComputeHash($archiveGuard))).Replace('-', '').ToLowerInvariant()
{indent}        }} finally {{
{indent}            $sha256.Dispose()
{indent}        }}
{indent}        if (-not $actualArchiveHash.Equals($expectedArchiveHash, [StringComparison]::OrdinalIgnoreCase)) {{
{indent}            throw \"FastPatch archive hash mismatch. Expected $expectedArchiveHash, found $actualArchiveHash.\"
{indent}        }}
{indent}    }}

{indent}    $expandedPath = Join-Path $WorkingDirectory 'ExpandedPatch'
{indent}    New-Item -ItemType Directory -Path $expandedPath -Force | Out-Null
{indent}    Expand-Archive -LiteralPath $resolvedPatchPath -DestinationPath $expandedPath -Force
{indent}    return Resolve-PayloadRoot -Root $expandedPath
{indent}}} finally {{
{indent}    if ($null -ne $archiveGuard) {{ $archiveGuard.Dispose() }}
{indent}}}"""
    text = text[:match.start()] + replacement + text[match.end():]

    text = replace_exact(
        text,
        "-Content ([string] $MyInvocation.MyCommand.ScriptContents)",
        "-Content $script:FastPatchInvocationScriptText",
        f"{path}: trusted snapshot source")

    text = replace_exact(
        text,
        "$process = Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentText -NoNewWindow -Wait -PassThru\n    $exitCode = $process.ExitCode",
        "$process = Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentText -NoNewWindow -PassThru\n"
        "    $process.WaitForExit()\n"
        "    $exitCode = $process.ExitCode",
        f"{path}: protected child wait")

    path.write_text(text, encoding="utf-8", newline="\n")


for installer in (
    Path('scripts/Install-ProtonVPNPatch.ps1'),
    Path('scripts/Install-ProtonVPNCompletePatch.ps1'),
):
    transform_installer(installer)

sfx = Path('scripts/New-ProtonVPNPatchSfx.ps1')
text = sfx.read_text(encoding='utf-8-sig')

loader_function = r'''
function Get-SfxLoaderScript {
    param(
        [Parameter(Mandatory = $true)] [string] $InstallerFileName,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $InstallerHash,
        [Parameter(Mandatory = $true)] [string] $PayloadFileName,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $PayloadHash,
        [Parameter(Mandatory = $true)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $TargetVersion
    )

    $template = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function D([string] $Value) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value)) }
function H([byte[]] $Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
$root = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd('\', '/')
$installer = Join-Path $root (D '__INSTALLER__')
$payload = Join-Path $root (D '__PAYLOAD__')
foreach ($path in @($root, $installer, $payload)) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "IExpress FastPatch source contains a reparse point: $path"
    }
}
$source = [IO.File]::Open($installer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$memory = New-Object IO.MemoryStream
try {
    $source.CopyTo($memory)
    $bytes = $memory.ToArray()
} finally {
    $memory.Dispose()
    $source.Dispose()
}
$actualInstallerHash = H $bytes
if (-not $actualInstallerHash.Equals('__INSTALLER_HASH__', [StringComparison]::OrdinalIgnoreCase)) {
    throw "IExpress FastPatch installer hash mismatch. Expected __INSTALLER_HASH__, found $actualInstallerHash."
}
$scriptText = [Text.Encoding]::UTF8.GetString($bytes)
if ($scriptText.Length -gt 0 -and $scriptText[0] -eq [char]0xFEFF) { $scriptText = $scriptText.Substring(1) }
if (-not (IsAdmin)) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FastPatchConsoleWindow {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
'@
    [FastPatchConsoleWindow]::ShowWindow([FastPatchConsoleWindow]::GetConsoleWindow(), 0) | Out-Null
}
$global:ProtonVpnFastPatchVerifiedSfxScriptText = $scriptText
& ([ScriptBlock]::Create($scriptText)) `
    -PatchPath $payload `
    -TargetVersion (D '__TARGET__') `
    -RestartClient `
    -PauseBeforeExit `
    -ExpectedPatchArchiveSha256 '__PAYLOAD_HASH__'
'@

    $utf8 = [Text.Encoding]::UTF8
    return $template.Replace('__INSTALLER__', [Convert]::ToBase64String($utf8.GetBytes($InstallerFileName))).Replace(
        '__INSTALLER_HASH__', $InstallerHash.Trim().ToLowerInvariant()).Replace(
        '__PAYLOAD__', [Convert]::ToBase64String($utf8.GetBytes($PayloadFileName))).Replace(
        '__PAYLOAD_HASH__', $PayloadHash.Trim().ToLowerInvariant()).Replace(
        '__TARGET__', [Convert]::ToBase64String($utf8.GetBytes($TargetVersion)))
}
'''

needle = "$windowsPowerShellPath = Join-Path $env:SystemRoot 'System32\\WindowsPowerShell\\v1.0\\powershell.exe'"
if needle not in text:
    raise RuntimeError('SFX builder: Windows PowerShell marker not found')
text = text.replace(needle, loader_function + "\n" + needle, 1)

old_vars = """$payloadPath = Join-Path $workingDirectory 'payload.zip'
$installerFileName = 'Install-ProtonVPNPatch.ps1'
$launcherFileName = 'Install-ProtonVPNPatch.cmd'
$packagedInstallerScriptPath = Join-Path $workingDirectory $installerFileName
$packagedLauncherPath = Join-Path $workingDirectory $launcherFileName
$iexpressConfigPath = Join-Path $workingDirectory 'ProtonVPNPatch.sed'"""
new_vars = """$payloadFileName = 'payload.zip'
$payloadPath = Join-Path $workingDirectory $payloadFileName
$installerFileName = 'Install-ProtonVPNPatch.ps1'
$packagedInstallerScriptPath = Join-Path $workingDirectory $installerFileName
$iexpressConfigPath = Join-Path $workingDirectory 'ProtonVPNPatch.sed'"""
text = replace_exact(text, old_vars, new_vars, 'SFX builder: working paths')

text = replace_exact(
    text,
    "    Copy-Item -LiteralPath $resolvedInstallerScriptPath -Destination $packagedInstallerScriptPath -Force\n    Copy-Item -LiteralPath $resolvedLauncherPath -Destination $packagedLauncherPath -Force",
    "    Copy-Item -LiteralPath $resolvedInstallerScriptPath -Destination $packagedInstallerScriptPath -Force",
    'SFX builder: extracted launcher copy')

launcher_block = r'''    $payloadInvocation = '-PatchPath \"%PAYLOAD%\" -TargetVersion \"{0}\" -RestartClient -PauseBeforeExit' -f $manifestTargetVersion
    $fallbackInvocation = '-File \"%SCRIPT%\" -TargetVersion \"{0}\" -RestartClient -PauseBeforeExit' -f $manifestTargetVersion
    $launcherLines = @(
        foreach ($line in Get-Content -LiteralPath $packagedLauncherPath) {
            if ($line.Trim() -ieq 'pause') {
                continue
            }

            if ($line.Contains('-PatchPath \"%PAYLOAD%\"')) {
                $line = $line.Replace('-PatchPath \"%PAYLOAD%\"', $payloadInvocation)
            } elseif ($line.Contains('-File \"%SCRIPT%\"')) {
                $line = $line.Replace('-File \"%SCRIPT%\"', $fallbackInvocation)
            }

            $line
        }
    )
    Set-Content -LiteralPath $packagedLauncherPath -Value $launcherLines -Encoding Ascii

'''
# The source contains literal double quotes, not backslash escapes from the raw Python string above.
launcher_block = launcher_block.replace('\\"', '"')
text = replace_exact(text, launcher_block, '', 'SFX builder: launcher rewrite')

payload_end = """    $payloadLength = (Get-Item -LiteralPath $payloadPath).Length

    $sourceDirectoryForSed = $workingDirectory.TrimEnd('\\') + '\\'"""
payload_new = """    $payloadLength = (Get-Item -LiteralPath $payloadPath).Length
    $installerHash = (Get-FileHash -LiteralPath $packagedInstallerScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $loaderScript = Get-SfxLoaderScript `
        -InstallerFileName $installerFileName `
        -InstallerHash $installerHash `
        -PayloadFileName $payloadFileName `
        -PayloadHash $payloadHash `
        -TargetVersion $manifestTargetVersion
    $encodedLoader = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($loaderScript))
    $sfxLaunchCommand = '\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader

    $sourceDirectoryForSed = $workingDirectory.TrimEnd('\\') + '\\'"""
text = replace_exact(text, payload_end, payload_new, 'SFX builder: loader generation')

text = replace_exact(text, 'AppLaunched=$launcherFileName', 'AppLaunched=$sfxLaunchCommand', 'SFX builder: AppLaunched')
text = replace_exact(text, 'AdminQuietInstCmd=$launcherFileName', 'AdminQuietInstCmd=$sfxLaunchCommand', 'SFX builder: admin quiet command')
text = replace_exact(text, 'UserQuietInstCmd=$launcherFileName', 'UserQuietInstCmd=$sfxLaunchCommand', 'SFX builder: user quiet command')
text = replace_exact(text, '%FILE2%=\n', '', 'SFX builder: FILE2 source entry')
text = replace_exact(text, 'FILE2="$launcherFileName"\n', '', 'SFX builder: FILE2 string')

sfx.write_text(text, encoding='utf-8', newline='\n')

# Update stale patch-tooling source-order contract to the new protected-stage handoff.
test_tooling = Path('.github/scripts/test-patch-tooling.ps1')
t = test_tooling.read_text(encoding='utf-8-sig')
old_contract = r'''    $elevationGuardIndex = $installerSource.IndexOf(
        'if (-not $ValidateOnly -and -not (Test-IsAdministrator)) {',
        [StringComparison]::Ordinal)
    $preflightValidationIndex = $installerSource.IndexOf(
        'Test-PatchPayload',
        $elevationGuardIndex,
        [StringComparison]::Ordinal)
    $elevationIndex = $installerSource.IndexOf(
        'Restart-Elevated',
        $elevationGuardIndex,
        [StringComparison]::Ordinal)

    Assert-Condition (
        $elevationGuardIndex -ge 0 -and
        $preflightValidationIndex -gt $elevationGuardIndex -and
        $elevationIndex -gt $preflightValidationIndex
    ) 'Installer payload validation must occur before requesting elevation.'
'''
new_contract = r'''    $stagingGuardIndex = $installerSource.IndexOf(
        'if (-not $ValidateOnly -and [string]::IsNullOrWhiteSpace($TrustedStagePath)) {',
        [StringComparison]::Ordinal)
    $preflightValidationIndex = $installerSource.IndexOf(
        'Test-PatchPayload',
        $stagingGuardIndex,
        [StringComparison]::Ordinal)
    $stagingIndex = $installerSource.IndexOf(
        'Invoke-TrustedStage',
        $preflightValidationIndex,
        [StringComparison]::Ordinal)

    Assert-Condition (
        $stagingGuardIndex -ge 0 -and
        $preflightValidationIndex -gt $stagingGuardIndex -and
        $stagingIndex -gt $preflightValidationIndex
    ) 'Installer payload validation must occur before protected privileged staging.'
'''
t = replace_exact(t, old_contract, new_contract, 'patch tooling: staging order contract')
test_tooling.write_text(t, encoding='utf-8', newline='\n')

# Extend the security regression with direct SFX mutation rejection and a real IExpress smoke run.
security_test = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
t = security_test.read_text(encoding='utf-8-sig')
t = replace_exact(
    t,
    "$completeInstallerScript = Join-Path $repositoryRoot 'scripts\\Install-ProtonVPNCompletePatch.ps1'\n",
    "$completeInstallerScript = Join-Path $repositoryRoot 'scripts\\Install-ProtonVPNCompletePatch.ps1'\n"
    "$sfxBuilderScript = Join-Path $repositoryRoot 'scripts\\New-ProtonVPNPatchSfx.ps1'\n"
    "$installerLauncher = Join-Path $repositoryRoot 'scripts\\Install-ProtonVPNPatch.cmd'\n",
    'security test: SFX paths')
t = replace_exact(
    t,
    "Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'\n",
    "Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'\n"
    "Import-FunctionDefinition -ScriptPath $sfxBuilderScript -Name 'Get-SfxLoaderScript'\n",
    'security test: SFX loader import')

new_tests = r'''
function Test-SfxLoaderRejectsMutatedInstaller {
    $fixtureRoot = Join-Path $testRoot 'sfx-mutated-installer'
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $installer = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.ps1'
    $payload = Join-Path $fixtureRoot 'payload.zip'
    Write-TestText $installer 'param() exit 0'
    [IO.File]::WriteAllBytes($payload, [byte[]]@(1, 2, 3, 4))
    $installerHash = Get-TextHash -Path $installer
    $payloadHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    $loader = Get-SfxLoaderScript `
        -InstallerFileName (Split-Path -Leaf $installer) `
        -InstallerHash $installerHash `
        -PayloadFileName (Split-Path -Leaf $payload) `
        -PayloadHash $payloadHash `
        -TargetVersion '5.1.5'

    Write-TestText $installer 'param() Write-Output "attacker replacement"; exit 0'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($loader))
    Push-Location $fixtureRoot
    try {
        $output = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    Assert-Condition ($exitCode -ne 0) 'Hash-pinned SFX loader executed a mutated extracted installer.'
    Assert-Condition (($output -join [Environment]::NewLine) -match 'installer hash mismatch') `
        'SFX loader rejected the mutated installer for an unexpected reason.'
}

function Test-IExpressVerifiedInMemoryEntry {
    $fixtureRoot = Join-Path $testRoot 'sfx-real-iexpress'
    $patch = Join-Path $fixtureRoot 'patch'
    $output = Join-Path $fixtureRoot 'ProtonVPN-Test-Patch.exe'
    $fakeInstaller = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.ps1'
    $fakeLauncher = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.cmd'
    $probe = Join-Path $fixtureRoot 'probe.json'
    New-Item -ItemType Directory -Force -Path $patch | Out-Null
    Write-TestText (Join-Path $patch 'ProtonVPN.Client.dll') 'sfx-payload'
    $payloadFile = Get-Item -LiteralPath (Join-Path $patch 'ProtonVPN.Client.dll')
    $manifest = [ordered]@{
        schemaVersion = 1
        targetVersion = '5.1.5'
        buildMode = 'client'
        sourceCommit = 'sfx-entry-smoke'
        files = @([ordered]@{
            path = 'ProtonVPN.Client.dll'
            size = $payloadFile.Length
            sha256 = (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    Write-TestText (Join-Path $patch 'patch-manifest.json') ($manifest | ConvertTo-Json -Depth 6)
    Write-TestText $fakeLauncher '@echo off'
    Write-TestText $fakeInstaller @'
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $PatchPath,
    [string] $TargetVersion,
    [switch] $RestartClient,
    [switch] $PauseBeforeExit,
    [switch] $ValidateOnly,
    [string] $ExpectedPatchArchiveSha256 = ''
)
# This dormant function deliberately preserves the packaged-installer process-wait rewrite seam.
function Dummy-ElevationWait {
    $process = Start-Process -FilePath 'cmd.exe' `
        -ArgumentList '/c exit 0' `
        -Wait `
        -PassThru
    return $process.ExitCode
}
if ($ValidateOnly) { exit 0 }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$result = [ordered]@{
    IsAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Is64BitProcess = [Environment]::Is64BitProcess
    PSCommandPath = [string] $PSCommandPath
    DefinitionContainsProbe = ([string] $MyInvocation.MyCommand.Definition).Contains('DefinitionContainsProbe')
    PatchPath = [IO.Path]::GetFullPath($PatchPath)
    ExpectedPatchArchiveSha256 = $ExpectedPatchArchiveSha256
    ActualPatchArchiveSha256 = (Get-FileHash -LiteralPath $PatchPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $env:PROTONVPN_FASTPATCH_SFX_PROBE -Encoding UTF8
exit 0
'@

    & $sfxBuilderScript `
        -PatchPath $patch `
        -OutputPath $output `
        -InstallerScriptPath $fakeInstaller `
        -LauncherPath $fakeLauncher
    Assert-Condition (Test-Path -LiteralPath $output -PathType Leaf) 'IExpress SFX smoke fixture was not built.'

    $previousProbe = $env:PROTONVPN_FASTPATCH_SFX_PROBE
    try {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $probe
        $process = Start-Process -FilePath $output -PassThru
        if (-not $process.WaitForExit(90000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw 'IExpress SFX smoke run did not terminate within 90 seconds.'
        }
        Assert-Condition ($process.ExitCode -eq 0) "IExpress SFX smoke run failed with exit code $($process.ExitCode)."
    } finally {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $previousProbe
    }

    Assert-Condition (Test-Path -LiteralPath $probe -PathType Leaf) 'IExpress did not execute the hash-pinned in-memory installer entry.'
    $result = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    Assert-Condition ([bool] $result.IsAdministrator) 'IExpress smoke installer did not inherit the administrator test token.'
    Assert-Condition ([bool] $result.Is64BitProcess) 'IExpress did not launch native 64-bit Windows PowerShell.'
    Assert-Condition ([string]::IsNullOrWhiteSpace([string] $result.PSCommandPath)) `
        'IExpress directly executed the extracted mutable installer path instead of verified in-memory bytes.'
    Assert-Condition ([bool] $result.DefinitionContainsProbe) 'Verified installer bytes were not executed as the expected in-memory script block.'
    Assert-Condition ([string] $result.ExpectedPatchArchiveSha256 -eq [string] $result.ActualPatchArchiveSha256) `
        'IExpress did not bind the extracted payload archive to its build-time SHA-256.'
}

'''
marker = 'function Test-StaticSecurityContracts {'
if marker not in t:
    raise RuntimeError('security test: static contract marker missing')
t = t.replace(marker, new_tests + marker, 1)

t = replace_exact(
    t,
    "    $base = Get-Content -LiteralPath $baseInstallerScript -Raw\n    $complete = Get-Content -LiteralPath $completeInstallerScript -Raw\n",
    "    $base = Get-Content -LiteralPath $baseInstallerScript -Raw\n"
    "    $complete = Get-Content -LiteralPath $completeInstallerScript -Raw\n"
    "    $sfx = Get-Content -LiteralPath $sfxBuilderScript -Raw\n",
    'security test: SFX static source')
static_add = r'''
    Assert-Condition (-not $sfx.Contains('AppLaunched=$launcherFileName')) `
        'IExpress still launches the extracted mutable CMD file directly.'
    Assert-Condition ($sfx.Contains('AppLaunched=$sfxLaunchCommand')) `
        'IExpress is not anchored to the encoded system-PowerShell verifier.'
    Assert-Condition ($sfx.Contains('[ScriptBlock]::Create($scriptText)')) `
        'IExpress verifier does not execute only the hash-pinned installer bytes in memory.'
    Assert-Condition ($sfx.Contains("-ExpectedPatchArchiveSha256 '__PAYLOAD_HASH__'")) `
        'IExpress verifier does not bind payload.zip to its build-time SHA-256.'
    foreach ($content in @($base, $complete)) {
        Assert-Condition ($content.Contains('[IO.FileShare]::Read)')) `
            'FastPatch installer does not hold a no-write/no-delete read-sharing lock while expanding a hash-pinned archive.'
        Assert-Condition ($content.Contains('$ExpectedPatchArchiveSha256')) `
            'FastPatch installer cannot receive the SFX build-time payload archive hash.'
    }
'''
insert_point = "    Assert-Condition ($complete.Contains(\"`$arguments += '-TrustedStagePath'\")) `\n        'Complete FastPatch does not forward the protected-stage trust context to the base helper.'\n"
t = replace_exact(t, insert_point, insert_point + static_add, 'security test: SFX static assertions')

t = replace_exact(
    t,
    "    Test-MutatedInstallerRejected\n    Test-ReparseSourceRejected\n",
    "    Test-MutatedInstallerRejected\n    Test-SfxLoaderRejectsMutatedInstaller\n    Test-IExpressVerifiedInMemoryEntry\n    Test-ReparseSourceRejected\n",
    'security test: SFX test calls')
security_test.write_text(t, encoding='utf-8', newline='\n')

print('FastPatch SFX trust-boundary transformation applied successfully.')
