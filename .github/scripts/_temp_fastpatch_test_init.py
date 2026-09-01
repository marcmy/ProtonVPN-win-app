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
        -FilePath (Get-WindowsPowerShellPath) `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) `
        -WorkingDirectory $fixtureRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -NoNewWindow `
        -Wait `
        -PassThru
    $exitCode = $process.ExitCode
    $output = @()
    if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) { $output += Get-Content -LiteralPath $stdoutPath }
    if (Test-Path -LiteralPath $stderrPath -PathType Leaf) { $output += Get-Content -LiteralPath $stderrPath }

    Assert-Condition ($exitCode -ne 0) 'Hash-pinned SFX loader executed a mutated extracted installer.'
    $loaderOutputText = $output -join [Environment]::NewLine
'''
if s.count(old_capture) != 1:
    raise RuntimeError(f'expected one SFX loader capture block, found {s.count(old_capture)}')
s = s.replace(old_capture,new_capture)
p.write_text(s, encoding='utf-8')
