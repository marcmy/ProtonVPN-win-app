from pathlib import Path


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)

helper = r'''function Get-SystemExecutablePath {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string] $RelativePath
    )

    $systemDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    if ([string]::IsNullOrWhiteSpace($systemDirectory)) {
        throw 'Could not resolve the protected Windows system directory.'
    }

    $systemDirectory = [IO.Path]::GetFullPath($systemDirectory).TrimEnd('\', '/')
    $systemDirectoryItem = Get-Item -LiteralPath $systemDirectory -Force -ErrorAction Stop
    if (($systemDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Windows system directory is a reparse point: $systemDirectory"
    }

    $path = [IO.Path]::GetFullPath((Join-Path $systemDirectory $RelativePath))
    $systemPrefix = $systemDirectory + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($systemPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "System executable path escapes the protected Windows system directory: $path"
    }

    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Trusted Windows system executable path is unsafe: $path"
    }

    return $item.FullName
}

function Get-WindowsPowerShellPath {
    return Get-SystemExecutablePath -RelativePath 'WindowsPowerShell\v1.0\powershell.exe'
}
'''

bootstrap_helper = r'''function Get-SystemExecutablePath([string] $RelativePath) {
    $systemDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    if ([string]::IsNullOrWhiteSpace($systemDirectory)) {
        throw 'Could not resolve the protected Windows system directory.'
    }
    $systemDirectory = [IO.Path]::GetFullPath($systemDirectory).TrimEnd('\', '/')
    $systemDirectoryItem = Get-Item -LiteralPath $systemDirectory -Force -ErrorAction Stop
    if (($systemDirectoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Windows system directory is a reparse point: $systemDirectory"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $systemDirectory $RelativePath))
    $systemPrefix = $systemDirectory + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($systemPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "System executable path escapes the protected Windows system directory: $path"
    }
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Trusted Windows system executable path is unsafe: $path"
    }
    return $item.FullName
}

function Get-WindowsPowerShellPath {
    return Get-SystemExecutablePath 'WindowsPowerShell\v1.0\powershell.exe'
}
'''

admin_block = r'''function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
'''

bootstrap_marker = "$template = @'\nSet-StrictMode -Version Latest\n$ErrorActionPreference = 'Stop'\n\nfunction Decode-Utf8([string] $Value) {"

expected_bare_counts = {
    'scripts/Install-ProtonVPNPatch.ps1': 3,
    'scripts/Install-ProtonVPNCompletePatch.ps1': 4,
}

for name, expected_count in expected_bare_counts.items():
    path = Path(name)
    text = path.read_text(encoding='utf-8-sig')
    text = replace_once(text, admin_block, admin_block + '\n' + helper, f'{name}: trusted system helper')
    if text.count(bootstrap_marker) != 1:
        raise RuntimeError(f'{name}: bootstrap marker count was {text.count(bootstrap_marker)}, expected 1')
    bootstrap_replacement = "$template = @'\nSet-StrictMode -Version Latest\n$ErrorActionPreference = 'Stop'\n\n" + bootstrap_helper + "\nfunction Decode-Utf8([string] $Value) {"
    text = text.replace(bootstrap_marker, bootstrap_replacement, 1)

    bare = "Start-Process -FilePath 'powershell.exe'"
    actual_count = text.count(bare)
    if actual_count != expected_count:
        raise RuntimeError(f'{name}: bare powershell launch count was {actual_count}, expected {expected_count}')
    text = text.replace(bare, 'Start-Process -FilePath (Get-WindowsPowerShellPath)')
    if bare in text:
        raise RuntimeError(f'{name}: a bare powershell launch remains')

    path.write_text(text, encoding='utf-8', newline='\n')

base = Path('scripts/Install-ProtonVPNPatch.ps1')
text = base.read_text(encoding='utf-8')
text = replace_once(
    text,
    '    & robocopy.exe @arguments\n',
    "    $robocopyPath = Get-SystemExecutablePath -RelativePath 'robocopy.exe'\n    & $robocopyPath @arguments\n",
    'base installer: robocopy absolute path')
base.write_text(text, encoding='utf-8', newline='\n')

security_test = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
test = security_test.read_text(encoding='utf-8-sig')
needle = "        Assert-Condition ($item.Content.Contains('-EncodedCommand')) `\n            \"$($item.Name) installer does not carry an inline bootstrap across elevation.\"\n"
addition = needle + "        Assert-Condition (-not $item.Content.Contains(\"Start-Process -FilePath 'powershell.exe'\")) `\n            \"$($item.Name) installer still resolves PowerShell through a mutable executable search path.\"\n        Assert-Condition ($item.Content.Contains('Get-WindowsPowerShellPath')) `\n            \"$($item.Name) installer does not anchor privileged PowerShell launches to the protected Windows system directory.\"\n"
test = replace_once(test, needle, addition, 'security test: PowerShell path contract')
needle = "    Assert-Condition ($complete.Contains(\"`$arguments += '-TrustedStagePath'\")) `\n        'Complete FastPatch does not forward the protected-stage trust context to the base helper.'\n"
addition = needle + "    Assert-Condition (-not $base.Contains('& robocopy.exe @arguments')) `\n        'Base FastPatch still resolves robocopy through a mutable executable search path.'\n    Assert-Condition ($base.Contains(\"Get-SystemExecutablePath -RelativePath 'robocopy.exe'\")) `\n        'Base FastPatch does not anchor robocopy to the protected Windows system directory.'\n"
test = replace_once(test, needle, addition, 'security test: robocopy path contract')
security_test.write_text(test, encoding='utf-8', newline='\n')

print('FastPatch privileged native executable resolution hardened.')
