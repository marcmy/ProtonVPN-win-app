from pathlib import Path

for name in ('scripts/Install-ProtonVPNPatch.ps1', 'scripts/Install-ProtonVPNCompletePatch.ps1'):
    path = Path(name)
    text = path.read_text(encoding='utf-8-sig')
    old = '''    if ($null -ne $ExpectedSize) {
        $actualSize = (Get-Item -LiteralPath $Destination -Force).Length
        if ($actualSize -ne $ExpectedSize.Value) {
            throw "Trusted staging size mismatch for '$Destination'. Expected $($ExpectedSize.Value), found $actualSize."
        }
    }
'''
    new = '''    if ($null -ne $ExpectedSize) {
        $actualSize = (Get-Item -LiteralPath $Destination -Force).Length
        $expectedSizeValue = [long] $ExpectedSize
        if ($actualSize -ne $expectedSizeValue) {
            throw "Trusted staging size mismatch for '$Destination'. Expected $expectedSizeValue, found $actualSize."
        }
    }
'''
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{name}: expected one nullable-size block, found {count}')
    text = text.replace(old, new, 1)
    if '$ExpectedSize.Value' in text:
        raise RuntimeError(f'{name}: stale ExpectedSize.Value remains')
    path.write_text(text, encoding='utf-8', newline='\n')

print('FastPatch bootstrap ExpectedSize handling corrected for PowerShell nullable unboxing.')
