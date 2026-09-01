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

for path in ('scripts/Install-ProtonVPNPatch.ps1', 'scripts/Install-ProtonVPNCompletePatch.ps1'):
    text = read(path)
    text = replace_exact(text, trusted_stage_old, trusted_stage_new, f'{path} trusted-stage exit propagation')
    # The generated bootstrap already waits explicitly; Refresh makes its child exit read equally deterministic.
    text = replace_exact(text,
'''    $process.WaitForExit()
    $exitCode = $process.ExitCode
} finally {
''',
'''    $process.WaitForExit()
    $process.Refresh()
    $exitCode = [int] $process.ExitCode
} finally {
''', f'{path} bootstrap child exit propagation')
    write(path, text)

complete_path = 'scripts/Install-ProtonVPNCompletePatch.ps1'
complete = read(complete_path)
complete = replace_exact(complete,
'''    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
        -ArgumentList ($arguments -join ' ') `
        -NoNewWindow `
        -Wait `
        -PassThru
    return $process.ExitCode
''',
'''    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
        -ArgumentList ($arguments -join ' ') `
        -NoNewWindow `
        -PassThru
    $process.WaitForExit()
    $process.Refresh()
    return [int] $process.ExitCode
''', 'complete base-helper exit propagation')
write(complete_path, complete)
