from pathlib import Path


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)

for name in ('scripts/Install-ProtonVPNPatch.ps1', 'scripts/Install-ProtonVPNCompletePatch.ps1'):
    path = Path(name)
    text = path.read_text(encoding='utf-8-sig')
    old = """                            Where-Object {
                                $_.Name.Equals('patch-manifest.json', [StringComparison]::OrdinalIgnoreCase)
                            }
"""
    new = """                            Where-Object {
                                $_.FullName.Replace('\\', '/').Equals(
                                    'patch-manifest.json',
                                    [StringComparison]::OrdinalIgnoreCase)
                            }
"""
    text = replace_once(text, old, new, f'{name}: root manifest entry match')
    path.write_text(text, encoding='utf-8', newline='\n')

security = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = security.read_text(encoding='utf-8-sig')
needle = "$testRoot = Join-Path ([IO.Path]::GetTempPath()) (\"protonvpn-fastpatch-secure-stage-{0}\" -f [Guid]::NewGuid().ToString('N'))\n"
addition = needle + "\n# Production functions imported below normally receive these from the installer param block.\n# The regression imports the functions without executing that block, so initialize the same\n# script-scope state explicitly.\n$script:PatchPath = ''\n$script:ExpectedPatchArchiveSha256 = ''\n$script:TrustedArchiveManifestText = ''\n$script:ValidatedManifestText = ''\n"
s = replace_once(s, needle, addition, 'security test: imported installer state')
security.write_text(s, encoding='utf-8', newline='\n')

print('FastPatch archive race regression state initialized and root manifest matching tightened.')
