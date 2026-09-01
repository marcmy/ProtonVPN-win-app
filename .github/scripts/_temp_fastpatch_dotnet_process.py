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

helper_anchor = '''function Get-WindowsPowerShellPath {
    return Get-SystemExecutablePath -RelativePath 'WindowsPowerShell\\v1.0\\powershell.exe'
}
'''
helper = helper_anchor + '''
function Invoke-ProcessAndWait {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string] $ArgumentText,
        [switch] $RunAs
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $ArgumentText
    if ($RunAs) {
        $startInfo.UseShellExecute = $true
        $startInfo.Verb = 'runas'
    } else {
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $false
    }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start trusted FastPatch child process: $FilePath"
        }
        $process.WaitForExit()
        return [int] $process.ExitCode
    } finally {
        $process.Dispose()
    }
}
'''

for path in ('scripts/Install-ProtonVPNPatch.ps1', 'scripts/Install-ProtonVPNCompletePatch.ps1'):
    text = read(path)
    text = replace_exact(text, helper_anchor, helper, f'{path} process helper insertion')

    # This is the block after _temp_fastpatch_exit_propagation.py has made explicit waits.
    outer_old = '''    if (-not (Test-IsAdministrator)) {
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
    outer_new = '''    if (-not (Test-IsAdministrator)) {
        return Invoke-ProcessAndWait `
            -FilePath (Get-WindowsPowerShellPath) `
            -ArgumentText $bootstrapArguments `
            -RunAs
    }

    return Invoke-ProcessAndWait `
        -FilePath (Get-WindowsPowerShellPath) `
        -ArgumentText $bootstrapArguments
'''
    text = replace_exact(text, outer_old, outer_new, f'{path} UAC/bootstrap process observer')

    bootstrap_old = '''    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) -ArgumentList $argumentText -NoNewWindow -PassThru
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = [int] $process.ExitCode
} finally {
'''
    bootstrap_new = '''    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = Get-WindowsPowerShellPath
    $startInfo.Arguments = $argumentText
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start trusted staged FastPatch installer: $($startInfo.FileName)"
        }
        $process.WaitForExit()
        $exitCode = [int] $process.ExitCode
    } finally {
        $process.Dispose()
    }
} finally {
'''
    text = replace_exact(text, bootstrap_old, bootstrap_new, f'{path} staged child process observer')
    write(path, text)

complete_path = 'scripts/Install-ProtonVPNCompletePatch.ps1'
complete = read(complete_path)
base_helper_old = '''    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
        -ArgumentList ($arguments -join ' ') `
        -NoNewWindow `
        -PassThru
    $process.WaitForExit()
    $process.Refresh()
    return [int] $process.ExitCode
'''
base_helper_new = '''    return Invoke-ProcessAndWait `
        -FilePath (Get-WindowsPowerShellPath) `
        -ArgumentText ($arguments -join ' ')
'''
complete = replace_exact(complete, base_helper_old, base_helper_new, 'complete base-helper process observer')
write(complete_path, complete)

# Permanent static contracts: no status-bearing Start-Process remains in either installer.
test_path = '.github/scripts/test-fast-patch-secure-staging.ps1'
test = read(test_path)
static_anchor = '''    Assert-Condition ($base.Contains("Join-Path `$windowsPowerShellHome 'Modules\\CimCmdlets\\CimCmdlets.psd1'")) `
        'Base FastPatch does not import CimCmdlets from the protected Windows PowerShell module directory.'
'''
static_new = static_anchor + '''    Assert-Condition ($base.Contains('function Invoke-ProcessAndWait')) `
        'Base FastPatch does not use the raw process-status helper.'
    Assert-Condition ($complete.Contains('function Invoke-ProcessAndWait')) `
        'Complete FastPatch does not use the raw process-status helper.'
    Assert-Condition (-not $base.Contains('Start-Process -FilePath (Get-WindowsPowerShellPath)')) `
        'Base FastPatch still relies on Start-Process for a privilege-critical child status.'
    Assert-Condition (-not $complete.Contains('Start-Process -FilePath (Get-WindowsPowerShellPath)')) `
        'Complete FastPatch still relies on Start-Process for a privilege-critical child status.'
'''
test = replace_exact(test, static_anchor, static_new, 'raw process static contracts')
write(test_path, test)
