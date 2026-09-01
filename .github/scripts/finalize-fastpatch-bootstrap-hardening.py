from pathlib import Path


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'{label}: expected 1 occurrence, found {count}')
    return text.replace(old, new, 1)

compression_function = r'''
function ConvertTo-CompressedEncodedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $ScriptText
    )

    $scriptBytes = [Text.UTF8Encoding]::new($false).GetBytes($ScriptText)
    $compressedStream = [IO.MemoryStream]::new()
    try {
        $gzip = [IO.Compression.GZipStream]::new(
            $compressedStream,
            [IO.Compression.CompressionMode]::Compress,
            $true)
        try {
            $gzip.Write($scriptBytes, 0, $scriptBytes.Length)
        } finally {
            $gzip.Dispose()
        }
        $compressedBase64 = [Convert]::ToBase64String($compressedStream.ToArray())
    } finally {
        $compressedStream.Dispose()
    }

    $decoder = @"
Set-StrictMode -Version Latest
`$ErrorActionPreference = 'Stop'
`$compressed = [Convert]::FromBase64String('$compressedBase64')
`$inputStream = [IO.MemoryStream]::new(`$compressed)
`$gzipStream = [IO.Compression.GZipStream]::new(`$inputStream, [IO.Compression.CompressionMode]::Decompress)
`$reader = [IO.StreamReader]::new(`$gzipStream, [Text.UTF8Encoding]::new(`$false))
try {
    `$decodedScript = `$reader.ReadToEnd()
} finally {
    `$reader.Dispose()
    `$gzipStream.Dispose()
    `$inputStream.Dispose()
}
& ([ScriptBlock]::Create(`$decodedScript))
"@

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($decoder))
    if ($encodedCommand.Length -gt 30000) {
        throw "Compressed FastPatch bootstrap exceeds the safe Windows command-line budget: $($encodedCommand.Length) characters."
    }
    return $encodedCommand
}
'''

base64_function = r'''function ConvertTo-Base64Utf8 {
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Value)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}
'''

raw_encode = "    $encodedBootstrap = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))\n    $bootstrapArguments = \"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedBootstrap\""
compressed_encode = "    $encodedBootstrap = ConvertTo-CompressedEncodedCommand -ScriptText $bootstrap\n    $bootstrapArguments = \"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedBootstrap\""

for name in ('scripts/Install-ProtonVPNPatch.ps1', 'scripts/Install-ProtonVPNCompletePatch.ps1'):
    path = Path(name)
    text = path.read_text(encoding='utf-8-sig')
    text = replace_once(text, base64_function, base64_function + compression_function, f'{name}: compression helper')
    text = replace_once(text, raw_encode, compressed_encode, f'{name}: compressed bootstrap handoff')
    path.write_text(text, encoding='utf-8', newline='\n')

# Avoid CurrentUser PSModulePath autoload at the privileged service-discovery boundary.
base = Path('scripts/Install-ProtonVPNPatch.ps1')
text = base.read_text(encoding='utf-8')
old_cim = r'''    $escapedTarget = [Regex]::Escape($TargetDirectory)
    return @(
        Get-CimInstance -ClassName Win32_Service |
'''
new_cim = r'''    $escapedTarget = [Regex]::Escape($TargetDirectory)
    $windowsPowerShellHome = Split-Path -Path (Get-WindowsPowerShellPath) -Parent
    $cimModulePath = Join-Path $windowsPowerShellHome 'Modules\CimCmdlets\CimCmdlets.psd1'
    $cimModule = Get-Item -LiteralPath $cimModulePath -Force -ErrorAction Stop
    if ($cimModule.PSIsContainer -or
        (($cimModule.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Trusted CimCmdlets module path is unsafe: $cimModulePath"
    }
    Microsoft.PowerShell.Core\Import-Module -Name $cimModule.FullName -Force -ErrorAction Stop | Out-Null

    return @(
        CimCmdlets\Get-CimInstance -ClassName Win32_Service |
'''
text = replace_once(text, old_cim, new_cim, 'base installer: trusted CimCmdlets import')
base.write_text(text, encoding='utf-8', newline='\n')

# The older tooling test imports Invoke-Robocopy in isolation; import its new trusted-path dependency too.
tooling = Path('.github/scripts/test-patch-tooling.ps1')
t = tooling.read_text(encoding='utf-8-sig')
old_import = r'''    $robocopyFunctionAst = $installerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Invoke-Robocopy'
    }, $true)
    Assert-Condition ($null -ne $robocopyFunctionAst) `
        'Installer does not define Invoke-Robocopy.'
    . ([ScriptBlock]::Create($robocopyFunctionAst.Extent.Text))
'''
new_import = r'''    $systemExecutableFunctionAst = $installerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Get-SystemExecutablePath'
    }, $true)
    Assert-Condition ($null -ne $systemExecutableFunctionAst) `
        'Installer does not define Get-SystemExecutablePath.'
    . ([ScriptBlock]::Create($systemExecutableFunctionAst.Extent.Text))

    $robocopyFunctionAst = $installerAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Invoke-Robocopy'
    }, $true)
    Assert-Condition ($null -ne $robocopyFunctionAst) `
        'Installer does not define Invoke-Robocopy.'
    . ([ScriptBlock]::Create($robocopyFunctionAst.Extent.Text))
'''
t = replace_once(t, old_import, new_import, 'patch tooling: robocopy dependency import')
tooling.write_text(t, encoding='utf-8', newline='\n')

security = Path('.github/scripts/test-fast-patch-secure-staging.ps1')
s = security.read_text(encoding='utf-8-sig')
s = replace_once(
    s,
    "Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'ConvertTo-Base64Utf8'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'\n",
    "Import-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'ConvertTo-Base64Utf8'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'ConvertTo-CompressedEncodedCommand'\nImport-FunctionDefinition -ScriptPath $baseInstallerScript -Name 'Get-TrustedStageBootstrap'\n",
    'security test: import compressed encoder')
s = replace_once(
    s,
    "    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))\n\n    $output = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)",
    "    $encoded = ConvertTo-CompressedEncodedCommand -ScriptText $bootstrap\n    Assert-Condition ($encoded.Length -le 30000) 'Compressed trusted-stage bootstrap exceeded the safe command-line budget.'\n\n    $output = @(& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded 2>&1)",
    'security test: production compressed bootstrap')
needle = "        Assert-Condition ($item.Content.Contains('Get-WindowsPowerShellPath')) `\n            \"$($item.Name) installer does not anchor privileged PowerShell launches to the protected Windows system directory.\"\n"
addition = needle + "        Assert-Condition ($item.Content.Contains('ConvertTo-CompressedEncodedCommand -ScriptText $bootstrap')) `\n            \"$($item.Name) installer does not compress its inline elevation bootstrap before -EncodedCommand.\"\n"
s = replace_once(s, needle, addition, 'security test: compressed bootstrap static contract')
needle = "    Assert-Condition ($base.Contains(\"Get-SystemExecutablePath -RelativePath 'robocopy.exe'\")) `\n        'Base FastPatch does not anchor robocopy to the protected Windows system directory.'\n"
addition = needle + "    Assert-Condition ($base.Contains('CimCmdlets\\Get-CimInstance -ClassName Win32_Service')) `\n        'Base FastPatch does not qualify privileged CIM service discovery to the trusted CimCmdlets module.'\n    Assert-Condition ($base.Contains(\"Join-Path `$windowsPowerShellHome 'Modules\\CimCmdlets\\CimCmdlets.psd1'\")) `\n        'Base FastPatch does not import CimCmdlets from the protected Windows PowerShell module directory.'\n"
s = replace_once(s, needle, addition, 'security test: trusted CimCmdlets contract')
security.write_text(s, encoding='utf-8', newline='\n')

# Guard IExpress's own encoded verifier command as well; its real smoke test exercises the result.
sfx = Path('scripts/New-ProtonVPNPatchSfx.ps1')
sfx_text = sfx.read_text(encoding='utf-8-sig')
old_sfx = "    $sfxLaunchCommand = '\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader\n\n    $sourceDirectoryForSed"
new_sfx = "    $sfxLaunchCommand = '\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader\n    if ($sfxLaunchCommand.Length -gt 30000) {\n        throw \"IExpress FastPatch verifier exceeds the safe Windows command-line budget: $($sfxLaunchCommand.Length) characters.\"\n    }\n\n    $sourceDirectoryForSed"
sfx_text = replace_once(sfx_text, old_sfx, new_sfx, 'SFX builder: command-line budget guard')
sfx.write_text(sfx_text, encoding='utf-8', newline='\n')

print('FastPatch compressed bootstrap, trusted CIM loading, and test dependencies finalized.')
