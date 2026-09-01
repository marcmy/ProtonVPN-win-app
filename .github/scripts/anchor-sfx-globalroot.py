from pathlib import Path


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)

sfx = Path('scripts/New-ProtonVPNPatchSfx.ps1')
text = sfx.read_text(encoding='utf-8-sig')
old = "    $sfxLaunchCommand = '\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader\n"
new = "    # Resolve through the system object-manager root rather than a user-overridable\n    # environment variable such as %SystemRoot%. GLOBALROOT is a Win32 namespace\n    # alias for the true system-wide object-manager root.\n    $sfxLaunchCommand = '\"\\\\?\\GLOBALROOT\\SystemRoot\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader\n"
text = replace_once(text, old, new, 'SFX builder: immutable system PowerShell anchor')
sfx.write_text(text, encoding='utf-8', newline='\n')

security = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = security.read_text(encoding='utf-8-sig')
old_runtime = '''    $previousProbe = $env:PROTONVPN_FASTPATCH_SFX_PROBE
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
'''
new_runtime = '''    $previousProbe = $env:PROTONVPN_FASTPATCH_SFX_PROBE
    $previousSystemRoot = $env:SystemRoot
    $fakeSystemRoot = Join-Path $fixtureRoot 'attacker-system-root'
    $fakePowerShellDirectory = Join-Path $fakeSystemRoot 'System32\\WindowsPowerShell\\v1.0'
    New-Item -ItemType Directory -Force -Path $fakePowerShellDirectory | Out-Null
    # If the SFX still expands %SystemRoot%, this renamed cmd.exe receives the PowerShell
    # arguments and the smoke probe never gets written. GLOBALROOT must bypass it entirely.
    Copy-Item `
        -LiteralPath (Join-Path $previousSystemRoot 'System32\\cmd.exe') `
        -Destination (Join-Path $fakePowerShellDirectory 'powershell.exe') `
        -Force
    try {
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $probe
        $env:SystemRoot = $fakeSystemRoot
        $process = Start-Process -FilePath $output -PassThru
        if (-not $process.WaitForExit(90000)) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            throw 'IExpress SFX smoke run did not terminate within 90 seconds.'
        }
        Assert-Condition ($process.ExitCode -eq 0) "IExpress SFX smoke run failed with exit code $($process.ExitCode)."
    } finally {
        $env:SystemRoot = $previousSystemRoot
        $env:PROTONVPN_FASTPATCH_SFX_PROBE = $previousProbe
    }
'''
s = replace_once(s, old_runtime, new_runtime, 'security test: poisoned SystemRoot IExpress smoke')
needle = "    Assert-Condition ($sfx.Contains('AppLaunched=$sfxLaunchCommand')) `\n        'IExpress is not anchored to the encoded system-PowerShell verifier.'\n"
addition = needle + "    Assert-Condition (-not $sfx.Contains('%SystemRoot%\\System32\\WindowsPowerShell')) `\n        'IExpress verifier still trusts the user-overridable SystemRoot environment variable.'\n    Assert-Condition ($sfx.Contains('\\\\?\\GLOBALROOT\\SystemRoot\\System32\\WindowsPowerShell\\v1.0\\powershell.exe')) `\n        'IExpress verifier is not anchored to the immutable GLOBALROOT system PowerShell path.'\n"
s = replace_once(s, needle, addition, 'security test: GLOBALROOT static contracts')
security.write_text(s, encoding='utf-8', newline='\n')

print('IExpress verifier now resolves system PowerShell through GLOBALROOT with environment-poisoning regression coverage.')
