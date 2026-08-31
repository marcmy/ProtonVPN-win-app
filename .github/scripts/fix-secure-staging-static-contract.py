from pathlib import Path
p = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = p.read_text(encoding='utf-8-sig')
old = '''        Assert-Condition ($item.Content.Contains('$MyInvocation.MyCommand.ScriptContents')) `
            "$($item.Name) installer does not snapshot the already-parsed script bytes."
'''
new = '''        Assert-Condition ($item.Content.Contains("PSObject.Properties['ScriptContents']")) `
            "$($item.Name) installer does not snapshot the already-parsed external-script bytes."
        Assert-Condition ($item.Content.Contains("'ProtonVpnFastPatchVerifiedSfxScriptText'")) `
            "$($item.Name) installer does not accept the hash-pinned in-memory SFX script snapshot."
'''
if s.count(old) != 1:
    raise RuntimeError(f'expected one stale ScriptContents contract, found {s.count(old)}')
s = s.replace(old, new, 1)
p.write_text(s, encoding='utf-8', newline='\n')
print('Updated secure-staging static script-capture contract.')
