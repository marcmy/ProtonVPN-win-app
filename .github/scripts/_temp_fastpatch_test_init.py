from pathlib import Path
p = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = p.read_text(encoding='utf-8').replace('\r\n','\n')
old = "$script:TrustedArchiveManifestText = ''\n$script:ValidatedManifestText = ''"
new = "$script:TrustedArchiveManifestText = ''\n$script:TrustedSfxArchiveBytes = $null\n$script:ValidatedManifestText = ''"
if s.count(old) != 1:
    raise RuntimeError(f'expected one test state block, found {s.count(old)}')
p.write_text(s.replace(old,new), encoding='utf-8')
