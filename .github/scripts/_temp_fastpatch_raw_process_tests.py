from pathlib import Path
import re

p = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = p.read_text(encoding='utf-8').replace('\r\n', '\n')

anchor = '''function Assert-Condition {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}
'''
helper = anchor + '''
function Invoke-TestProcessCapture {
    param(
        [Parameter(Mandatory = $true)] [string] $FilePath,
        [Parameter(Mandatory = $true)] [string] $Arguments,
        [string] $WorkingDirectory = ''
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
        $startInfo.WorkingDirectory = $WorkingDirectory
    }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Could not start test child process: $FilePath" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $exitCode = [int] $process.ExitCode
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $output = @()
        if (-not [string]::IsNullOrWhiteSpace($stdout)) { $output += $stdout.TrimEnd() }
        if (-not [string]::IsNullOrWhiteSpace($stderr)) { $output += $stderr.TrimEnd() }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
    } finally {
        $process.Dispose()
    }
}
'''
if s.count(anchor) != 1:
    raise RuntimeError(f'Assert-Condition anchor count: {s.count(anchor)}')
s = s.replace(anchor, helper)

# Replace the Start-Process observer used by the mutated-installer SFX negative test.
pat = re.compile(r"    \$stdoutPath = Join-Path \$fixtureRoot 'loader\.stdout\.txt'\n.*?    if \(Test-Path -LiteralPath \$stderrPath -PathType Leaf\) \{ \$output \+= Get-Content -LiteralPath \$stderrPath \}\n", re.S)
rep = '''    $child = Invoke-TestProcessCapture `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -Arguments "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded" `
        -WorkingDirectory $fixtureRoot
    $exitCode = $child.ExitCode
    $output = @($child.Output)
'''
s, n = pat.subn(rep, s, count=1)
if n != 1:
    raise RuntimeError(f'loader capture replacement count: {n}')

# Replace the standalone base installer E2E observer.
pat = re.compile(r"    \$stdoutPath = Join-Path \$fixtureRoot 'base\.stdout\.txt'\n.*?    if \(Test-Path -LiteralPath \$stderrPath -PathType Leaf\) \{ \$output \+= Get-Content -LiteralPath \$stderrPath \}\n", re.S)
rep = '''    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass' +
        ' -File "' + $baseInstallerScript + '"' +
        ' -PatchPath "' + $payload + '"' +
        ' -InstallRoot "' + $installRoot + '"' +
        ' -TargetVersion 5.1.5' +
        ' -BackupRoot "' + $backupRoot + '"' +
        ' -NoRestart'
    $child = Invoke-TestProcessCapture -FilePath (Join-Path $PSHOME 'powershell.exe') -Arguments $arguments
    $exitCode = $child.ExitCode
    $output = @($child.Output)
'''
s, n = pat.subn(rep, s, count=1)
if n != 1:
    raise RuntimeError(f'base E2E capture replacement count: {n}')

# Replace the complete installer E2E observer.
pat = re.compile(r"    \$stdoutPath = Join-Path \$fixtureRoot 'complete\.stdout\.txt'\n.*?    if \(Test-Path -LiteralPath \$stderrPath -PathType Leaf\) \{ \$output \+= Get-Content -LiteralPath \$stderrPath \}\n", re.S)
rep = '''    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass' +
        ' -File "' + $completeInstallerScript + '"' +
        ' -PatchPath "' + $payload + '"' +
        ' -InstallRoot "' + $installRoot + '"' +
        ' -TargetVersion 5.1.5' +
        ' -BackupRoot "' + $backupRoot + '"' +
        ' -NoRestart'
    $child = Invoke-TestProcessCapture -FilePath (Join-Path $PSHOME 'powershell.exe') -Arguments $arguments
    $exitCode = $child.ExitCode
    $output = @($child.Output)
'''
s, n = pat.subn(rep, s, count=1)
if n != 1:
    raise RuntimeError(f'complete E2E capture replacement count: {n}')

# Replace the temporary nested raw-process probe with the shared capture helper.
pat = re.compile(r"function Test-CompressedBootstrapExitPropagation \{\n.*?\n\}\n\nfunction Test-BootstrapSuccessAndAcl", re.S)
rep = '''function Test-CompressedBootstrapExitPropagation {
    $controlEncoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes('[Environment]::Exit(7)'))
    $control = Invoke-TestProcessCapture `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -Arguments "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $controlEncoded"
    Assert-Condition ($control.ExitCode -eq 7) `
        ("Raw Windows PowerShell process control could not observe Environment.Exit(7); saw {0}." -f $control.ExitCode)

    $encoded = ConvertTo-CompressedEncodedCommand -ScriptText "throw 'compressed-bootstrap-probe'"
    $decoder = Invoke-TestProcessCapture `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -Arguments "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    Assert-Condition ($decoder.ExitCode -eq 1) `
        ("Compressed bootstrap decoder flattened a terminating failure to exit {0}. Output:`n{1}" -f $decoder.ExitCode, ($decoder.Output -join [Environment]::NewLine))
}

function Test-BootstrapSuccessAndAcl'''
s, n = pat.subn(rep, s, count=1)
if n != 1:
    raise RuntimeError(f'compressed bootstrap probe replacement count: {n}')

p.write_text(s, encoding='utf-8')
