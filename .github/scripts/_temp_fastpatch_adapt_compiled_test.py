from pathlib import Path

path = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
content = path.read_text(encoding='utf-8').replace('\r\n', '\n')

start = content.index('function Test-IExpressVerifiedInMemoryEntry {')
end = content.index('function Test-StaticSecurityContracts {', start)
replacement = r'''function Test-CompiledSfxVerifiedInMemoryEntry {
    $fixtureRoot = Join-Path $testRoot 'sfx-real-compiled'
    $patch = Join-Path $fixtureRoot 'patch'
    $output = Join-Path $fixtureRoot 'ProtonVPN-Test-Patch.exe'
    $fakeInstaller = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.ps1'
    $fakeLauncher = Join-Path $fixtureRoot 'Install-ProtonVPNPatch.cmd'
    $probe = Join-Path $fixtureRoot 'probe.json'
    $attackerMarker = Join-Path $fixtureRoot 'attacker-marker.txt'
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
function Dummy-ElevationWait {
    $process = Start-Process -FilePath 'cmd.exe' `
        -ArgumentList '/c exit 0' `
        -Wait `
        -PassThru
    return $process.ExitCode
}
function Test-SfxWriteDenied {
    param([Parameter(Mandatory = $true)] [string] $Path)
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
        return $false
    } catch [IO.IOException] {
        return $true
    } catch [UnauthorizedAccessException] {
        return $true
    } finally {
        if ($null -ne $stream) { $stream.Dispose() }
    }
}
function Test-SfxDeleteDenied {
    param([Parameter(Mandatory = $true)] [string] $Path)
    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        return $false
    } catch [IO.IOException] {
        return $true
    } catch [UnauthorizedAccessException] {
        return $true
    }
}
if ($ValidateOnly) { exit 0 }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$resourceInstallerPath = Join-Path (Get-Location).Path 'Install-ProtonVPNPatch.ps1'
$trustedArchiveBytes = [byte[]] (Get-Variable -Name 'ProtonVpnFastPatchVerifiedSfxArchiveBytes' -Scope Global -ErrorAction Stop).Value
$archiveSha = [Security.Cryptography.SHA256]::Create()
try {
    $actualTrustedArchiveHash = ([BitConverter]::ToString($archiveSha.ComputeHash($trustedArchiveBytes))).Replace('-', '').ToLowerInvariant()
} finally {
    $archiveSha.Dispose()
}
$result = [ordered]@{
    IsAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Is64BitProcess = [Environment]::Is64BitProcess
    PSCommandPath = [string] $PSCommandPath
    DefinitionContainsProbe = ([string] $MyInvocation.MyCommand.Definition).Contains('DefinitionContainsProbe')
    PatchPath = [IO.Path]::GetFullPath($PatchPath)
    InstallerResourcePath = [IO.Path]::GetFullPath($resourceInstallerPath)
    ExpectedPatchArchiveSha256 = $ExpectedPatchArchiveSha256
    ActualPatchArchiveSha256 = $actualTrustedArchiveHash
    PayloadWriteDenied = Test-SfxWriteDenied -Path $PatchPath
    PayloadDeleteDenied = Test-SfxDeleteDenied -Path $PatchPath
    InstallerWriteDenied = Test-SfxWriteDenied -Path $resourceInstallerPath
    InstallerDeleteDenied = Test-SfxDeleteDenied -Path $resourceInstallerPath
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $env:PROTONVPN_FASTPATCH_SFX_PROBE -Encoding UTF8
exit 0
'@

    $stagesBefore = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'ProtonVPNFastPatchSfx-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    & $sfxBuilderScript `
        -PatchPath $patch `
        -OutputPath $output `
        -InstallerScriptPath $fakeInstaller `
        -LauncherPath $fakeLauncher
    Assert-Condition (Test-Path -LiteralPath $output -PathType Leaf) 'Compiled FastPatch SFX smoke fixture was not built.'

    Write-TestText $fakeInstaller @'
param([string] $PatchPath, [string] $TargetVersion, [switch] $RestartClient, [switch] $PauseBeforeExit, [switch] $ValidateOnly, [string] $ExpectedPatchArchiveSha256 = '')
Set-Content -LiteralPath $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER -Value 'attacker replacement executed'
exit 0
'@
    Write-TestText (Join-Path $patch 'ProtonVPN.Client.dll') 'attacker-replaced-build-input'

    $previousProbe = $env:PROTONVPN_FASTPATCH_SFX_PROBE
    $previousAttackerMarker = $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER
    try {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $probe
        $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER = $attackerMarker
        $process = Start-Process -FilePath $output -PassThru
        if (-not $process.WaitForExit(90000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw 'Compiled FastPatch SFX smoke run did not terminate within 90 seconds.'
        }
        Assert-Condition ($process.ExitCode -eq 0) "Compiled FastPatch SFX smoke run failed with exit code $($process.ExitCode)."
    } finally {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $previousProbe
        $env:PROTONVPN_FASTPATCH_SFX_ATTACKER_MARKER = $previousAttackerMarker
    }

    Assert-Condition (-not (Test-Path -LiteralPath $attackerMarker -PathType Leaf)) `
        'Compiled FastPatch SFX executed the mutable original installer after packaging.'
    Assert-Condition (Test-Path -LiteralPath $probe -PathType Leaf) `
        'Compiled FastPatch SFX did not execute the embedded hash-pinned in-memory installer entry.'
    $result = Get-Content -LiteralPath $probe -Raw | ConvertFrom-Json
    Assert-Condition ([bool] $result.IsAdministrator) 'Compiled SFX smoke installer did not inherit the administrator test token.'
    Assert-Condition ([bool] $result.Is64BitProcess) 'Compiled SFX did not launch native 64-bit Windows PowerShell.'
    Assert-Condition ([string]::IsNullOrWhiteSpace([string] $result.PSCommandPath)) `
        'Compiled SFX directly executed a mutable installer path instead of verified in-memory bytes.'
    Assert-Condition ([bool] $result.DefinitionContainsProbe) `
        'Embedded verified installer bytes were not executed as the expected in-memory script block.'
    Assert-Condition ([string] $result.ExpectedPatchArchiveSha256 -eq [string] $result.ActualPatchArchiveSha256) `
        'Compiled SFX did not bind the verified in-memory payload archive to its build-time SHA-256.'
    Assert-Condition ([bool] $result.PayloadWriteDenied) `
        'Compiled SFX did not keep the extracted payload archive write-locked for the installer lifetime.'
    Assert-Condition ([bool] $result.PayloadDeleteDenied) `
        'Compiled SFX did not keep the extracted payload archive delete-locked for the installer lifetime.'
    Assert-Condition ([bool] $result.InstallerWriteDenied) `
        'Compiled SFX did not keep the extracted installer resource write-locked for the installer lifetime.'
    Assert-Condition ([bool] $result.InstallerDeleteDenied) `
        'Compiled SFX did not keep the extracted installer resource delete-locked for the installer lifetime.'
    Assert-Condition (-not (Test-Path -LiteralPath ([string] $result.PatchPath))) `
        'Compiled SFX left its extracted payload resource behind after the installer exited.'
    Assert-Condition (-not (Test-Path -LiteralPath ([string] $result.InstallerResourcePath))) `
        'Compiled SFX left its extracted installer resource behind after the installer exited.'

    $stagesAfter = @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'ProtonVPNFastPatchSfx-*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $newStages = @($stagesAfter | Where-Object { $stagesBefore -notcontains $_ })
    Assert-Condition ($newStages.Count -eq 0) `
        ("Compiled SFX leaked runtime staging directories: {0}" -f ($newStages -join ', '))
}

'''
content = content[:start] + replacement + content[end:]

contract_start = content.index("    Assert-Condition (-not $sfx.Contains('AppLaunched=$launcherFileName'))")
contract_end = content.index('    foreach ($content in @($base, $complete)) {', contract_start)
contracts = r'''    Assert-Condition (-not $sfx.Contains('IEXPRESS')) `
        'FastPatch SFX still depends on IExpress extraction-before-execution semantics.'
    Assert-Condition (-not $sfx.Contains('AppLaunched=')) `
        'FastPatch SFX still carries an IExpress AppLaunched trust boundary.'
    Assert-Condition ($sfx.Contains('GetSystemDirectory')) `
        'Compiled SFX does not derive the protected Windows system directory through Win32.'
    Assert-Condition ($sfx.Contains('GetManifestResourceStream')) `
        'Compiled SFX does not load installer, payload, and loader bytes from its own executable resources.'
    Assert-Condition ($sfx.Contains('FileMode.CreateNew')) `
        'Compiled SFX does not create its resource copies without replacing attacker-controlled files.'
    Assert-Condition ($sfx.Contains('FileShare.Read')) `
        'Compiled SFX parent does not deny write/delete sharing while child PowerShell consumes resource copies.'
    Assert-Condition ($sfx.Contains('[IO.FileShare]::ReadWrite')) `
        'Compiled SFX loader cannot read resources while the parent write-lock handle remains open.'
    Assert-Condition ($sfx.Contains('ProtonVpnFastPatchVerifiedSfxArchiveBytes')) `
        'Compiled SFX does not bind verified payload archive bytes into installer process memory.'
    Assert-Condition ($sfx.Contains('Encoding.Unicode.GetBytes(loaderText)')) `
        'Compiled SFX does not encode the verified embedded loader bytes directly for Windows PowerShell.'
    Assert-Condition ($sfx.Contains('-EncodedCommand " + encodedCommand')) `
        'Compiled SFX does not execute the verified embedded loader through direct system PowerShell EncodedCommand.'
    Assert-Condition ($sfx.Contains('encodedCommand.Length > 30000')) `
        'Compiled SFX does not enforce the Windows command-line budget for its in-memory loader.'
    Assert-Condition (-not $sfx.Contains('RedirectStandardInput = true')) `
        'Compiled SFX still relies on the failed PowerShell stdin loader path.'
    foreach ($resourceName in @('FastPatch.Loader', 'FastPatch.Installer', 'FastPatch.Payload')) {
        Assert-Condition ($sfx.Contains($resourceName)) `
            "Compiled SFX does not embed required resource '$resourceName'."
    }
    Assert-Condition ($sfx.Contains('[ScriptBlock]::Create($scriptText)')) `
        'Compiled SFX loader does not execute only the hash-pinned installer bytes in memory.'
    Assert-Condition ($sfx.Contains("-ExpectedPatchArchiveSha256 '__PAYLOAD_HASH__'")) `
        'Compiled SFX loader does not bind payload.zip to its build-time SHA-256.'

'''
content = content[:contract_start] + contracts + content[contract_end:]
content = content.replace('Test-IExpressVerifiedInMemoryEntry', 'Test-CompiledSfxVerifiedInMemoryEntry')
path.write_text(content, encoding='utf-8')
