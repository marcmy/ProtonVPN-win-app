from pathlib import Path

path = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
text = path.read_text(encoding='utf-8-sig')
old = "    Invoke-Expression $functionAst.Extent.Text\n"
new = "    $definition = $functionAst.Extent.Text\n    $scriptScopedDefinition = [regex]::Replace(\n        $definition,\n        '^function\\s+' + [regex]::Escape($Name),\n        ('function script:' + $Name),\n        [Text.RegularExpressions.RegexOptions]::IgnoreCase)\n    Invoke-Expression $scriptScopedDefinition\n"
count = text.count(old)
if count != 1:
    raise RuntimeError(f'expected one importer invocation, found {count}')
text = text.replace(old, new, 1)
path.write_text(text, encoding='utf-8', newline='\n')
print('FastPatch secure-staging function importer now persists imported functions in script scope.')
