from pathlib import Path

builder = Path('scripts/New-ProtonVPNPatchSfx.ps1')
text = builder.read_text(encoding='utf-8').replace('\r\n', '\n')
old_open = "$source = [IO.File]::Open($installer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)"
new_open = "$source = [IO.File]::Open($installer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)"
if text.count(old_open) != 1:
    raise RuntimeError(f'Expected one installer loader open, found {text.count(old_open)}')
text = text.replace(old_open, new_open)
marker = '''if (-not $actualInstallerHash.Equals('__INSTALLER_HASH__', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Compiled FastPatch SFX installer hash mismatch. Expected __INSTALLER_HASH__, found $actualInstallerHash."
}
$scriptText = [Text.Encoding]::UTF8.GetString($bytes)
'''
replacement = '''if (-not $actualInstallerHash.Equals('__INSTALLER_HASH__', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Compiled FastPatch SFX installer hash mismatch. Expected __INSTALLER_HASH__, found $actualInstallerHash."
}
$payloadSource = [IO.File]::Open($payload, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
$payloadMemory = New-Object IO.MemoryStream
try {
    $payloadSource.CopyTo($payloadMemory)
    $payloadBytes = $payloadMemory.ToArray()
} finally {
    $payloadMemory.Dispose()
    $payloadSource.Dispose()
}
$actualPayloadHash = Get-FastPatchBytesSha256 $payloadBytes
if (-not $actualPayloadHash.Equals('__PAYLOAD_HASH__', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Compiled FastPatch SFX payload hash mismatch. Expected __PAYLOAD_HASH__, found $actualPayloadHash."
}
$global:ProtonVpnFastPatchVerifiedSfxArchiveBytes = $payloadBytes
$scriptText = [Text.Encoding]::UTF8.GetString($bytes)
'''
if text.count(marker) != 1:
    raise RuntimeError(f'Expected one loader hash marker, found {text.count(marker)}')
builder.write_text(text.replace(marker, replacement), encoding='utf-8')

top_old = "$script:TrustedArchiveManifestText = ''\n$script:FastPatchInvocationScriptText = ''"
top_new = '''$script:TrustedArchiveManifestText = ''
$script:TrustedSfxArchiveBytes = $null
$trustedSfxArchiveVariable = Get-Variable `
    -Name 'ProtonVpnFastPatchVerifiedSfxArchiveBytes' `
    -Scope Global `
    -ErrorAction SilentlyContinue
if ($null -ne $trustedSfxArchiveVariable -and $trustedSfxArchiveVariable.Value -is [byte[]]) {
    $script:TrustedSfxArchiveBytes = [byte[]] $trustedSfxArchiveVariable.Value
}
$script:FastPatchInvocationScriptText = '' '''.rstrip()

helper = r'''function Expand-TrustedSfxArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory
    )

    if ($null -eq $script:TrustedSfxArchiveBytes) {
        throw 'Trusted SFX archive bytes are not available.'
    }
    if ([string]::IsNullOrWhiteSpace($ExpectedPatchArchiveSha256)) {
        throw 'Trusted SFX archive bytes require the build-time archive SHA-256.'
    }
    $expectedArchiveHash = $ExpectedPatchArchiveSha256.Trim().ToLowerInvariant()
    if ($expectedArchiveHash -notmatch '^[0-9a-f]{64}$') {
        throw "ExpectedPatchArchiveSha256 is invalid: $ExpectedPatchArchiveSha256"
    }
    $actualArchiveHash = Get-Sha256HexFromBytes -Bytes $script:TrustedSfxArchiveBytes
    if (-not $actualArchiveHash.Equals($expectedArchiveHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "FastPatch in-memory SFX archive hash mismatch. Expected $expectedArchiveHash, found $actualArchiveHash."
    }

    $expandedPath = Join-Path $WorkingDirectory 'ExpandedPatch'
    New-Item -ItemType Directory -Path $expandedPath -Force | Out-Null
    $expandedFull = [IO.Path]::GetFullPath($expandedPath).TrimEnd('\', '/')
    $expandedPrefix = $expandedFull + [IO.Path]::DirectorySeparatorChar
    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    $memory = [IO.MemoryStream]::new($script:TrustedSfxArchiveBytes, $false)
    $archive = [IO.Compression.ZipArchive]::new($memory, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $manifestEntries = @(
            $archive.Entries | Where-Object {
                $_.FullName.Replace('\', '/').Trim('/').Equals('patch-manifest.json', [StringComparison]::OrdinalIgnoreCase)
            }
        )
        if ($manifestEntries.Count -ne 1) {
            throw "Verified FastPatch archive must contain exactly one patch-manifest.json entry; found $($manifestEntries.Count)."
        }
        $manifestStream = $manifestEntries[0].Open()
        $manifestReader = [IO.StreamReader]::new($manifestStream, [Text.UTF8Encoding]::new($false), $true)
        try {
            $script:TrustedArchiveManifestText = $manifestReader.ReadToEnd()
        } finally {
            $manifestReader.Dispose()
        }
        if ([string]::IsNullOrWhiteSpace($script:TrustedArchiveManifestText)) {
            throw 'Verified FastPatch archive contains an empty patch manifest.'
        }

        foreach ($entry in $archive.Entries) {
            $relativePath = $entry.FullName.Replace('/', '\').TrimStart('\')
            if ([string]::IsNullOrWhiteSpace($relativePath)) { continue }
            $segments = $relativePath.Split([char[]] @('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)
            if ([IO.Path]::IsPathRooted($relativePath) -or $relativePath.Contains(':') -or $segments -contains '..') {
                throw "Verified FastPatch archive contains an unsafe entry path: $($entry.FullName)"
            }
            $destination = [IO.Path]::GetFullPath((Join-Path $expandedFull $relativePath))
            if (-not $destination.StartsWith($expandedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Verified FastPatch archive entry escapes the extraction root: $($entry.FullName)"
            }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                continue
            }
            New-Item -ItemType Directory -Path (Split-Path -Path $destination -Parent) -Force | Out-Null
            $entryStream = $entry.Open()
            $destinationStream = $null
            try {
                $destinationStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $entryStream.CopyTo($destinationStream)
                $destinationStream.Flush()
            } finally {
                if ($null -ne $destinationStream) { $destinationStream.Dispose() }
                $entryStream.Dispose()
            }
        }
    } finally {
        $archive.Dispose()
        $memory.Dispose()
    }
    return Resolve-PayloadRoot -Root $expandedPath
}

'''
resolve_old = "$script:TrustedArchiveManifestText = ''\n    $resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path"
resolve_new = "$script:TrustedArchiveManifestText = ''\n    if ($null -ne $script:TrustedSfxArchiveBytes) {\n        return Expand-TrustedSfxArchive -WorkingDirectory $WorkingDirectory\n    }\n    $resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path"

for name in ('scripts/Install-ProtonVPNPatch.ps1', 'scripts/Install-ProtonVPNCompletePatch.ps1'):
    path = Path(name)
    content = path.read_text(encoding='utf-8').replace('\r\n', '\n')
    if content.count(top_old) != 1:
        raise RuntimeError(f'{name}: top trust marker count {content.count(top_old)}')
    content = content.replace(top_old, top_new)
    if content.count('function Resolve-PatchSource {') != 1:
        raise RuntimeError(f'{name}: unexpected Resolve-PatchSource count')
    content = content.replace('function Resolve-PatchSource {', helper + 'function Resolve-PatchSource {')
    if content.count(resolve_old) != 1:
        raise RuntimeError(f'{name}: Resolve-PatchSource reset marker count {content.count(resolve_old)}')
    content = content.replace(resolve_old, resolve_new)
    path.write_text(content, encoding='utf-8')

adapter = Path('.github/workflows/adapt-fastpatch-compiled-sfx-tests.yml')
atext = adapter.read_text(encoding='utf-8').replace('\r\n', '\n')
old_actual = "              ActualPatchArchiveSha256 = (Get-FileHash -LiteralPath $PatchPath -Algorithm SHA256).Hash.ToLowerInvariant()"
new_actual = """              ActualPatchArchiveSha256 = $actualTrustedArchiveHash"""
if atext.count(old_actual) != 1:
    raise RuntimeError(f'adapter actual archive hash marker count {atext.count(old_actual)}')
probe_marker = """          $resourceInstallerPath = Join-Path (Get-Location).Path 'Install-ProtonVPNPatch.ps1'
          $result = [ordered]@{
"""
probe_replacement = """          $resourceInstallerPath = Join-Path (Get-Location).Path 'Install-ProtonVPNPatch.ps1'
          $trustedArchiveBytes = [byte[]] (Get-Variable -Name 'ProtonVpnFastPatchVerifiedSfxArchiveBytes' -Scope Global -ErrorAction Stop).Value
          $archiveSha = [Security.Cryptography.SHA256]::Create()
          try {
              $actualTrustedArchiveHash = ([BitConverter]::ToString($archiveSha.ComputeHash($trustedArchiveBytes))).Replace('-', '').ToLowerInvariant()
          } finally {
              $archiveSha.Dispose()
          }
          $result = [ordered]@{
"""
if atext.count(probe_marker) != 1:
    raise RuntimeError(f'adapter probe marker count {atext.count(probe_marker)}')
atext = atext.replace(probe_marker, probe_replacement).replace(old_actual, new_actual)
contract_marker = """              Assert-Condition ($sfx.Contains('Encoding.Unicode.GetBytes(loaderText)')) `
                  'Compiled SFX does not encode the verified embedded loader bytes directly for Windows PowerShell.'
"""
contract_replacement = """              Assert-Condition ($sfx.Contains('ProtonVpnFastPatchVerifiedSfxArchiveBytes')) `
                  'Compiled SFX does not bind verified payload archive bytes into installer process memory.'
              Assert-Condition ($sfx.Contains('[IO.FileShare]::ReadWrite')) `
                  'Compiled SFX loader cannot read resources while the parent write-lock handle remains open.'
              Assert-Condition ($sfx.Contains('Encoding.Unicode.GetBytes(loaderText)')) `
                  'Compiled SFX does not encode the verified embedded loader bytes directly for Windows PowerShell.'
"""
if atext.count(contract_marker) != 1:
    raise RuntimeError(f'adapter static contract marker count {atext.count(contract_marker)}')
atext = atext.replace(contract_marker, contract_replacement)
adapter.write_text(atext, encoding='utf-8')
