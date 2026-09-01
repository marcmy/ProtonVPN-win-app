from pathlib import Path


def read(path):
    return Path(path).read_text(encoding='utf-8').replace('\r\n', '\n')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8')


def replace_exact(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected exactly one match, found {count}')
    return text.replace(old, new)

trusted_stage_old = '''    if (-not (Test-IsAdministrator)) {
        $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
            -ArgumentList $bootstrapArguments `
            -Verb RunAs `
            -Wait `
            -PassThru
    } else {
        $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
            -ArgumentList $bootstrapArguments `
            -NoNewWindow `
            -Wait `
            -PassThru
    }

    return $process.ExitCode
'''
trusted_stage_new = '''    if (-not (Test-IsAdministrator)) {
        $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
            -ArgumentList $bootstrapArguments `
            -Verb RunAs `
            -PassThru
    } else {
        $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
            -ArgumentList $bootstrapArguments `
            -NoNewWindow `
            -PassThru
    }

    $process.WaitForExit()
    $process.Refresh()
    return [int] $process.ExitCode
'''

decoder_old = '''& ([ScriptBlock]::Create(`$decodedScript))
"@
'''
decoder_new = '''try {
    & ([ScriptBlock]::Create(`$decodedScript))
    exit 0
} catch {
    Write-Error -Message `$_.Exception.Message -ErrorAction Continue
    exit 1
}
"@
'''

bootstrap_exit_old = '''exit $exitCode
'@
'''
bootstrap_exit_new = '''if ($exitCode -ne 0) {
    throw "Trusted staged FastPatch child failed with exit code $exitCode."
}
'@
'''

for path in ('scripts/Install-ProtonVPNPatch.ps1', 'scripts/Install-ProtonVPNCompletePatch.ps1'):
    text = read(path)
    text = replace_exact(text, decoder_old, decoder_new, f'{path} compressed bootstrap decoder failure propagation')
    text = replace_exact(text, bootstrap_exit_old, bootstrap_exit_new, f'{path} trusted-stage bootstrap failure contract')
    text = replace_exact(text, trusted_stage_old, trusted_stage_new, f'{path} trusted-stage exit propagation')
    text = replace_exact(text, '''    $process.WaitForExit()\n    $exitCode = $process.ExitCode\n} finally {\n''', '''    $process.WaitForExit()\n    $process.Refresh()\n    $exitCode = [int] $process.ExitCode\n} finally {\n''', f'{path} bootstrap child exit propagation')
    write(path, text)

complete_path = 'scripts/Install-ProtonVPNCompletePatch.ps1'
complete = read(complete_path)
complete = replace_exact(complete, '''    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `\n        -ArgumentList ($arguments -join ' ') `\n        -NoNewWindow `\n        -Wait `\n        -PassThru\n    return $process.ExitCode\n''', '''    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `\n        -ArgumentList ($arguments -join ' ') `\n        -NoNewWindow `\n        -PassThru\n    $process.WaitForExit()\n    $process.Refresh()\n    return [int] $process.ExitCode\n''', 'complete base-helper exit propagation')
write(complete_path, complete)
