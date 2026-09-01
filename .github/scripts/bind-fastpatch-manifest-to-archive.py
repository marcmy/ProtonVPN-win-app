from pathlib import Path


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)

archive_hash_block = r'''                if (-not $actualArchiveHash.Equals($expectedArchiveHash, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "FastPatch archive hash mismatch. Expected $expectedArchiveHash, found $actualArchiveHash."
                }
'''
archive_hash_bound = archive_hash_block + r'''

                # The verified ZIP is the SFX trust anchor. Capture the manifest from that exact
                # archive stream before extracting anything into ordinary-user-writable temp.
                # Validation must never trust a post-extraction replacement manifest.
                $archiveGuard.Position = 0
                Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
                $zipArchive = [IO.Compression.ZipArchive]::new(
                    $archiveGuard,
                    [IO.Compression.ZipArchiveMode]::Read,
                    $true)
                try {
                    $manifestEntries = @(
                        $zipArchive.Entries |
                            Where-Object {
                                $_.Name.Equals('patch-manifest.json', [StringComparison]::OrdinalIgnoreCase)
                            }
                    )
                    if ($manifestEntries.Count -ne 1) {
                        throw "Verified FastPatch archive must contain exactly one patch-manifest.json entry; found $($manifestEntries.Count)."
                    }

                    $manifestStream = $manifestEntries[0].Open()
                    $manifestReader = [IO.StreamReader]::new(
                        $manifestStream,
                        [Text.UTF8Encoding]::new($false),
                        $true)
                    try {
                        $script:TrustedArchiveManifestText = $manifestReader.ReadToEnd()
                    } finally {
                        $manifestReader.Dispose()
                    }
                    if ([string]::IsNullOrWhiteSpace($script:TrustedArchiveManifestText)) {
                        throw 'Verified FastPatch archive contains an empty patch manifest.'
                    }
                } finally {
                    $zipArchive.Dispose()
                }
'''

for name, test_function in (
    ('scripts/Install-ProtonVPNPatch.ps1', 'Test-PatchPayload'),
    ('scripts/Install-ProtonVPNCompletePatch.ps1', 'Test-CompletePatchPayload'),
):
    path = Path(name)
    text = path.read_text(encoding='utf-8-sig')
    text = replace_once(
        text,
        "$ErrorActionPreference = 'Stop'\n\n$script:FastPatchInvocationScriptText = ''",
        "$ErrorActionPreference = 'Stop'\n\n$script:TrustedArchiveManifestText = ''\n$script:FastPatchInvocationScriptText = ''",
        f'{name}: trusted archive manifest state')
    text = replace_once(text, archive_hash_block, archive_hash_bound, f'{name}: archive manifest capture')
    text = replace_once(
        text,
        "    $resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path\n",
        "    $script:TrustedArchiveManifestText = ''\n    $resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path\n",
        f'{name}: clear archive manifest state')
    manifest_read = r'''    try {
        $manifestJson = Get-Content -LiteralPath $manifestPath -Raw
        $manifest = $manifestJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
'''
    manifest_bound = r'''    try {
        $manifestJson = if (-not [string]::IsNullOrWhiteSpace($script:TrustedArchiveManifestText)) {
            $script:TrustedArchiveManifestText
        } else {
            Get-Content -LiteralPath $manifestPath -Raw
        }
        $manifest = $manifestJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
'''
    text = replace_once(text, manifest_read, manifest_bound, f'{name}: trusted manifest validation')
    path.write_text(text, encoding='utf-8', newline='\n')

security = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = security.read_text(encoding='utf-8-sig')
s = replace_once(
    s,
    "Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'ConvertTo-CompressedEncodedCommand'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'\n",
    "Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'ConvertTo-CompressedEncodedCommand'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Resolve-PayloadRoot'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Resolve-PatchSource'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Test-PatchPayload'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'\n",
    'security test: archive-validation imports')

new_test = r'''
function Test-VerifiedArchiveManifestSurvivesExtractionRace {
    $fixtureRoot = Join-Path $testRoot 'verified-archive-manifest-race'
    $source = Join-Path $fixtureRoot 'source'
    $archive = Join-Path $fixtureRoot 'payload.zip'
    $working = Join-Path $fixtureRoot 'working'
    New-Item -ItemType Directory -Force -Path $source | Out-Null
    New-Item -ItemType Directory -Force -Path $working | Out-Null

    Write-TestText (Join-Path $source 'ProtonVPN.Client.dll') 'verified-archive-client'
    $files = [Collections.ArrayList]::new()
    Add-ManifestFile -List $files -Root $source -RelativePath 'ProtonVPN.Client.dll'
    $trustedManifest = [ordered]@{
        schemaVersion = 1
        targetVersion = '5.1.5'
        buildMode = 'client'
        sourceCommit = 'verified-archive-race-test'
        files = @($files)
    }
    Write-TestText (Join-Path $source 'patch-manifest.json') ($trustedManifest | ConvertTo-Json -Depth 6)
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath $archive -CompressionLevel Optimal -Force
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()

    $previousPatchPath = $script:PatchPath
    $previousExpectedArchiveHash = $script:ExpectedPatchArchiveSha256
    $previousTrustedManifest = $script:TrustedArchiveManifestText
    try {
        $script:PatchPath = $archive
        $script:ExpectedPatchArchiveSha256 = $archiveHash
        $payloadRoot = Resolve-PatchSource -WorkingDirectory $working
        Assert-Condition (-not [string]::IsNullOrWhiteSpace($script:TrustedArchiveManifestText)) `
            'Hash-pinned archive did not retain its manifest as the validation trust anchor.'

        # Simulate the exact race: after extraction, replace BOTH payload and manifest with a
        # self-consistent malicious pair. Reading the temp manifest would accept this pair.
        Write-TestText (Join-Path $payloadRoot 'ProtonVPN.Client.dll') 'attacker-client'
        $maliciousFiles = [Collections.ArrayList]::new()
        Add-ManifestFile -List $maliciousFiles -Root $payloadRoot -RelativePath 'ProtonVPN.Client.dll'
        $maliciousManifest = [ordered]@{
            schemaVersion = 1
            targetVersion = '5.1.5'
            buildMode = 'client'
            sourceCommit = 'attacker-replacement'
            files = @($maliciousFiles)
        }
        Write-TestText (Join-Path $payloadRoot 'patch-manifest.json') ($maliciousManifest | ConvertTo-Json -Depth 6)

        $rejected = $false
        try {
            Test-PatchPayload -PayloadRoot $payloadRoot -ExpectedTargetVersion '5.1.5' | Out-Null
        } catch {
            if ($_.Exception.Message -match 'size mismatch|hash mismatch') {
                $rejected = $true
            } else {
                throw
            }
        }
        Assert-Condition $rejected `
            'FastPatch accepted a malicious manifest+payload pair substituted after verified ZIP extraction.'
    } finally {
        $script:PatchPath = $previousPatchPath
        $script:ExpectedPatchArchiveSha256 = $previousExpectedArchiveHash
        $script:TrustedArchiveManifestText = $previousTrustedManifest
    }
}

'''
marker = 'function Test-SfxLoaderRejectsMutatedInstaller {'
if s.count(marker) != 1:
    raise RuntimeError(f'security test: SFX loader marker count was {s.count(marker)}, expected 1')
s = s.replace(marker, new_test + marker, 1)

needle = "    foreach ($content in @($base, $complete)) {\n        Assert-Condition ($content.Contains('[IO.FileShare]::Read)')) `\n            'FastPatch installer does not hold a no-write/no-delete read-sharing lock while expanding a hash-pinned archive.'\n        Assert-Condition ($content.Contains('$ExpectedPatchArchiveSha256')) `\n            'FastPatch installer cannot receive the SFX build-time payload archive hash.'\n    }\n"
addition = "    foreach ($content in @($base, $complete)) {\n        Assert-Condition ($content.Contains('[IO.FileShare]::Read)')) `\n            'FastPatch installer does not hold a no-write/no-delete read-sharing lock while expanding a hash-pinned archive.'\n        Assert-Condition ($content.Contains('$ExpectedPatchArchiveSha256')) `\n            'FastPatch installer cannot receive the SFX build-time payload archive hash.'\n        Assert-Condition ($content.Contains('$script:TrustedArchiveManifestText = $manifestReader.ReadToEnd()')) `\n            'FastPatch does not bind manifest validation to the already verified ZIP stream.'\n        Assert-Condition ($content.Contains('$script:TrustedArchiveManifestText')) `\n            'FastPatch does not retain the verified archive manifest across extraction.'\n    }\n"
s = replace_once(s, needle, addition, 'security test: archive trust static contracts')
s = replace_once(
    s,
    "    Test-MutatedInstallerRejected\n    Test-SfxLoaderRejectsMutatedInstaller\n",
    "    Test-MutatedInstallerRejected\n    Test-VerifiedArchiveManifestSurvivesExtractionRace\n    Test-SfxLoaderRejectsMutatedInstaller\n",
    'security test: archive race invocation')
security.write_text(s, encoding='utf-8', newline='\n')

print('FastPatch manifest validation is now bound to the verified archive, with deterministic race coverage.')
