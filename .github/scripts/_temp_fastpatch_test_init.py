from pathlib import Path
p = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = p.read_text(encoding='utf-8').replace('\r\n','\n')
old = "$script:TrustedArchiveManifestText = ''\n$script:ValidatedManifestText = ''"
new = "$script:TrustedArchiveManifestText = ''\n$script:TrustedSfxArchiveBytes = $null\n$script:ValidatedManifestText = ''"
if s.count(old) != 1:
    raise RuntimeError(f'expected one test state block, found {s.count(old)}')
s = s.replace(old,new)

old_capture = '''    Push-Location $fixtureRoot
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    Pop-Location

    Assert-Condition ($exitCode -ne 0) 'Hash-pinned SFX loader executed a mutated extracted installer.'
    $loaderOutputText = $output -join [Environment]::NewLine
'''
new_capture = '''    $stdoutPath = Join-Path $fixtureRoot 'loader.stdout.txt'
    $stderrPath = Join-Path $fixtureRoot 'loader.stderr.txt'
    $process = Start-Process `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) `
        -WorkingDirectory $fixtureRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -NoNewWindow `
        -PassThru
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = [int] $process.ExitCode
    $output = @()
    if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) { $output += Get-Content -LiteralPath $stdoutPath }
    if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { $output += Get-Content -LiteralPath $stderrPath }

    Assert-Condition ($exitCode -ne 0) 'Hash-pinned SFX loader executed a mutated extracted installer.'
    $loaderOutputText = $output -join [Environment]::NewLine
'''
if s.count(old_capture) != 1:
    raise RuntimeError(f'expected one SFX loader capture block, found {s.count(old_capture)}')
s = s.replace(old_capture,new_capture)

base_old = '''    $beforeStages = @(Get-CurrentStageDirectories)
    $output = @(& powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $baseInstallerScript `
        -PatchPath $payload `
        -InstallRoot $installRoot `
        -TargetVersion '5.1.5' `
        -BackupRoot $backupRoot `
        -NoRestart 2>&1)
    $exitCode = $LASTEXITCODE
'''
base_new = '''    $beforeStages = @(Get-CurrentStageDirectories)
    $stdoutPath = Join-Path $fixtureRoot 'base.stdout.txt'
    $stderrPath = Join-Path $fixtureRoot 'base.stderr.txt'
    $process = Start-Process `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', ('"' + $baseInstallerScript + '"'),
            '-PatchPath', ('"' + $payload + '"'),
            '-InstallRoot', ('"' + $installRoot + '"'),
            '-TargetVersion', '5.1.5',
            '-BackupRoot', ('"' + $backupRoot + '"'),
            '-NoRestart') `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -NoNewWindow `
        -PassThru
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = [int] $process.ExitCode
    $output = @()
    if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) { $output += Get-Content -LiteralPath $stdoutPath }
    if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { $output += Get-Content -LiteralPath $stderrPath }
'''
if s.count(base_old) != 1:
    raise RuntimeError(f'expected one base E2E native capture block, found {s.count(base_old)}')
s = s.replace(base_old,base_new)

complete_old = '''    $beforeStages = @(Get-CurrentStageDirectories)
    $output = @(& powershell.exe `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $completeInstallerScript `
        -PatchPath $payload `
        -InstallRoot $installRoot `
        -TargetVersion '5.1.5' `
        -BackupRoot $backupRoot `
        -NoRestart 2>&1)
    $exitCode = $LASTEXITCODE
'''
complete_new = '''    $beforeStages = @(Get-CurrentStageDirectories)
    $stdoutPath = Join-Path $fixtureRoot 'complete.stdout.txt'
    $stderrPath = Join-Path $fixtureRoot 'complete.stderr.txt'
    $process = Start-Process `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList @(
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', ('"' + $completeInstallerScript + '"'),
            '-PatchPath', ('"' + $payload + '"'),
            '-InstallRoot', ('"' + $installRoot + '"'),
            '-TargetVersion', '5.1.5',
            '-BackupRoot', ('"' + $backupRoot + '"'),
            '-NoRestart') `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -NoNewWindow `
        -PassThru
    $process.WaitForExit()
    $process.Refresh()
    $exitCode = [int] $process.ExitCode
    $output = @()
    if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) { $output += Get-Content -LiteralPath $stdoutPath }
    if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { $output += Get-Content -LiteralPath $stderrPath }
'''
if s.count(complete_old) != 1:
    raise RuntimeError(f'expected one complete E2E native capture block, found {s.count(complete_old)}')
s = s.replace(complete_old,complete_new)

old_assert = '''    Assert-Condition ($exitCode -eq 0) "Protected standalone FastPatch install failed: $($output -join [Environment]::NewLine)"
    Assert-Condition ((Get-Content -LiteralPath (Join-Path $target 'ProtonVPN.Client.dll') -Raw) -eq 'new-client') `
        'Standalone FastPatch did not install from the protected staged payload.'
'''
new_assert = '''    Assert-Condition ($exitCode -eq 0) "Protected standalone FastPatch install failed: $($output -join [Environment]::NewLine)"
    $actualClientText = Get-Content -LiteralPath (Join-Path $target 'ProtonVPN.Client.dll') -Raw
    Assert-Condition ($actualClientText -eq 'new-client') `
        ("Standalone FastPatch did not install from the protected staged payload. Actual: '{0}'. Child output:`n{1}" -f $actualClientText, ($output -join [Environment]::NewLine))
'''
if s.count(old_assert) != 1:
    raise RuntimeError(f'expected one base E2E assertion block, found {s.count(old_assert)}')
s = s.replace(old_assert,new_assert)

complete_assert_old = "    Assert-Condition ($exitCode -ne 0) 'Synthetic base-installer failure unexpectedly produced a successful complete install.'\n"
complete_assert_new = "    Assert-Condition ($exitCode -ne 0) (\"Synthetic base-installer failure unexpectedly produced a successful complete install. Child output:`n{0}\" -f ($output -join [Environment]::NewLine))\n"
if s.count(complete_assert_old) != 1:
    raise RuntimeError(f'expected one complete E2E exit assertion, found {s.count(complete_assert_old)}')
s = s.replace(complete_assert_old, complete_assert_new)

marker = '''function Test-BootstrapSuccessAndAcl {
'''
probe = '''function Test-CompressedBootstrapExitPropagation {
    $encoded = ConvertTo-CompressedEncodedCommand -ScriptText 'exit 42'
    $process = Start-Process `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) `
        -NoNewWindow `
        -PassThru
    $process.WaitForExit()
    $process.Refresh()
    Assert-Condition ([int] $process.ExitCode -eq 42) `
        ("Compressed bootstrap decoder flattened exit 42 to exit {0}." -f [int] $process.ExitCode)
}

function Test-BootstrapSuccessAndAcl {
'''
if s.count(marker) != 1:
    raise RuntimeError(f'expected bootstrap success marker once, found {s.count(marker)}')
s = s.replace(marker, probe)

call_marker = '''    Test-StaticSecurityContracts
    Test-BootstrapSuccessAndAcl
'''
call_new = '''    Test-StaticSecurityContracts
    Test-CompressedBootstrapExitPropagation
    Test-BootstrapSuccessAndAcl
'''
if s.count(call_marker) != 1:
    raise RuntimeError(f'expected bootstrap call marker once, found {s.count(call_marker)}')
s = s.replace(call_marker, call_new)

p.write_text(s, encoding='utf-8')
