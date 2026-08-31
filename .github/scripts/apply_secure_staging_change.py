from pathlib import Path
import re

ROOT = Path.cwd()

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig").replace("\r\n", "\n")

def write(path, text):
    (ROOT / path).write_text(text, encoding="utf-8", newline="\n")

def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one exact match, found {count}")
    return text.replace(old, new, 1)

def regex_once(text, pattern, replacement, label):
    new, count = re.subn(pattern, replacement, text, count=1, flags=re.MULTILINE | re.DOTALL)
    if count != 1:
        raise RuntimeError(f"{label}: expected one regex match, found {count}")
    return new

SHARED = r'''function ConvertTo-Base64Utf8 {
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Value)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

function Get-Sha256HexFromBytes {
    param([Parameter(Mandatory = $true)] [byte[]] $Bytes)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

function Write-TrustedSnapshot {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Content
    )

    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
    [IO.File]::WriteAllBytes($Path, $bytes)
    return Get-Sha256HexFromBytes -Bytes $bytes
}

function Get-TrustedStageBootstrap {
    param(
        [Parameter(Mandatory = $true)] [string] $PayloadRoot,
        [Parameter(Mandatory = $true)] [string] $InstallerSnapshotPath,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $InstallerHash,
        [Parameter(Mandatory = $true)] [string] $ManifestSnapshotPath,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $ManifestHash,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[A-Za-z0-9._-]+\.ps1$')] [string] $InstallerFileName,
        [AllowEmptyString()] [string] $ForwardedArgumentText = '',
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{32}$')] [string] $StageId
    )

    $template = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Decode-Utf8([string] $Value) {
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

function Get-HashHex([string] $Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        } finally {
            $sha256.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

$AdministratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$SystemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')

function New-LockedDirectorySecurity {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($AdministratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($AdministratorsSid, $SystemSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $null = $security.AddAccessRule($rule)
    }
    return $security
}

function New-LockedFileSecurity {
    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetOwner($AdministratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($AdministratorsSid, $SystemSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)
        $null = $security.AddAccessRule($rule)
    }
    return $security
}

function New-LockedDirectory([string] $Path) {
    if (Test-Path -LiteralPath $Path) {
        throw "Trusted staging path already exists: $Path"
    }
    $directory = [IO.DirectoryInfo]::new($Path)
    $directory.Create((New-LockedDirectorySecurity))
}

function Ensure-LockedDirectory([string] $Root, [string] $Directory) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $directoryFull = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if ($directoryFull.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    $rootPrefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $directoryFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Trusted staging directory escapes its root: $Directory"
    }

    $relative = $directoryFull.Substring($rootPrefix.Length)
    $current = $rootFull
    foreach ($segment in $relative.Split([char[]] @('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer -or
                (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Trusted staging directory is unsafe: $current"
            }
        } else {
            New-LockedDirectory -Path $current
        }
    }
}

function Assert-SourcePathNoReparse([string] $Root, [string] $Path) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $pathFull = [IO.Path]::GetFullPath($Path)
    $rootPrefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $pathFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source path escapes its root: $Path"
    }

    $rootItem = Get-Item -LiteralPath $rootFull -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Source root is a reparse point: $rootFull"
    }

    $relative = $pathFull.Substring($rootPrefix.Length)
    $current = $rootFull
    foreach ($segment in $relative.Split([char[]] @('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source path contains a reparse point: $current"
        }
    }
}

function Copy-LockedFile(
    [string] $SourceRoot,
    [string] $SourcePath,
    [string] $Destination,
    [string] $ExpectedHash,
    [Nullable[long]] $ExpectedSize
) {
    Assert-SourcePathNoReparse -Root $SourceRoot -Path $SourcePath
    $parent = Split-Path -Path $Destination -Parent
    Ensure-LockedDirectory -Root $StageRoot -Directory $parent

    $source = [IO.File]::Open(
        $SourcePath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $destinationStream = $null
    try {
        $destinationStream = [IO.FileStream]::new(
            $Destination,
            [IO.FileMode]::CreateNew,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::SequentialScan,
            (New-LockedFileSecurity))
        $source.CopyTo($destinationStream)
        $destinationStream.Flush()
    } finally {
        if ($null -ne $destinationStream) { $destinationStream.Dispose() }
        $source.Dispose()
    }

    if ($null -ne $ExpectedSize) {
        $actualSize = (Get-Item -LiteralPath $Destination -Force).Length
        if ($actualSize -ne $ExpectedSize.Value) {
            throw "Trusted staging size mismatch for '$Destination'. Expected $($ExpectedSize.Value), found $actualSize."
        }
    }

    $actualHash = Get-HashHex -Path $Destination
    if (-not $actualHash.Equals($ExpectedHash.Trim().ToLowerInvariant(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Trusted staging hash mismatch for '$Destination'. Expected $ExpectedHash, found $actualHash."
    }
}

function Quote-Argument([string] $Value) {
    if ($Value.Contains('"')) {
        throw "Arguments containing quote characters are not supported: $Value"
    }
    return '"' + $Value + '"'
}

$SourcePayloadRoot = Decode-Utf8 '__PAYLOAD_ROOT__'
$InstallerSnapshotPath = Decode-Utf8 '__INSTALLER_SNAPSHOT__'
$ManifestSnapshotPath = Decode-Utf8 '__MANIFEST_SNAPSHOT__'
$InstallerHash = '__INSTALLER_HASH__'
$ManifestHash = '__MANIFEST_HASH__'
$InstallerFileName = Decode-Utf8 '__INSTALLER_FILENAME__'
$ForwardedArgumentText = Decode-Utf8 '__FORWARDED_ARGUMENTS__'
$StageId = '__STAGE_ID__'

$programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
if ([string]::IsNullOrWhiteSpace($programFiles)) {
    throw 'Could not resolve the Windows Program Files directory for trusted staging.'
}
$programFiles = [IO.Path]::GetFullPath($programFiles).TrimEnd('\', '/')
$programFilesItem = Get-Item -LiteralPath $programFiles -Force
if (($programFilesItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Program Files trusted staging parent is a reparse point: $programFiles"
}

$StageRoot = Join-Path $programFiles ('.ProtonVPNFastPatchStage-' + $StageId)
$stagePayload = Join-Path $StageRoot 'Payload'
$stageInstaller = Join-Path $StageRoot $InstallerFileName
$stageManifest = Join-Path $stagePayload 'patch-manifest.json'
$exitCode = 1

try {
    New-LockedDirectory -Path $StageRoot
    Ensure-LockedDirectory -Root $StageRoot -Directory $stagePayload

    Copy-LockedFile `
        -SourceRoot (Split-Path -Path $InstallerSnapshotPath -Parent) `
        -SourcePath $InstallerSnapshotPath `
        -Destination $stageInstaller `
        -ExpectedHash $InstallerHash `
        -ExpectedSize $null

    Copy-LockedFile `
        -SourceRoot (Split-Path -Path $ManifestSnapshotPath -Parent) `
        -SourcePath $ManifestSnapshotPath `
        -Destination $stageManifest `
        -ExpectedHash $ManifestHash `
        -ExpectedSize $null

    try {
        $manifest = Get-Content -LiteralPath $stageManifest -Raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Trusted manifest snapshot is not valid JSON: $($_.Exception.Message)"
    }

    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -eq 0) {
        throw 'Trusted manifest snapshot does not declare any payload files.'
    }

    $declaredPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sourceRoot = [IO.Path]::GetFullPath($SourcePayloadRoot).TrimEnd('\', '/')
    $sourceRootPrefix = $sourceRoot + [IO.Path]::DirectorySeparatorChar
    $stagePayloadFull = [IO.Path]::GetFullPath($stagePayload).TrimEnd('\', '/')
    $stagePayloadPrefix = $stagePayloadFull + [IO.Path]::DirectorySeparatorChar

    foreach ($file in $manifestFiles) {
        foreach ($required in @('path', 'size', 'sha256')) {
            if ($null -eq $file.PSObject.Properties[$required]) {
                throw "Trusted manifest file entry is missing '$required'."
            }
        }

        $relativePath = ([string] $file.path).Trim().Replace('/', '\')
        $segments = $relativePath.Split([char[]] @('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains(':') -or
            $segments -contains '..' -or
            $relativePath.Equals('patch-manifest.json', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Trusted manifest contains an unsafe payload path: $($file.path)"
        }
        if (-not $declaredPaths.Add($relativePath)) {
            throw "Trusted manifest declares the payload path more than once: $relativePath"
        }

        $expectedHash = ([string] $file.sha256).Trim().ToLowerInvariant()
        if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
            throw "Trusted manifest contains an invalid SHA-256 value for '$relativePath'."
        }
        $expectedSize = [long] $file.size
        if ($expectedSize -lt 0) {
            throw "Trusted manifest contains an invalid size for '$relativePath'."
        }

        $sourcePath = [IO.Path]::GetFullPath((Join-Path $sourceRoot $relativePath))
        if (-not $sourcePath.StartsWith($sourceRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Trusted manifest source path escapes the payload root: $relativePath"
        }
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Trusted source payload file is missing: $relativePath"
        }

        $destination = [IO.Path]::GetFullPath((Join-Path $stagePayloadFull $relativePath))
        if (-not $destination.StartsWith($stagePayloadPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Trusted manifest destination path escapes the staged payload root: $relativePath"
        }

        Copy-LockedFile `
            -SourceRoot $sourceRoot `
            -SourcePath $sourcePath `
            -Destination $destination `
            -ExpectedHash $expectedHash `
            -ExpectedSize ([Nullable[long]] $expectedSize)
    }

    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', (Quote-Argument $stageInstaller),
        '-PatchPath', (Quote-Argument $stagePayload),
        '-TrustedStagePath', (Quote-Argument $StageRoot)
    )
    $argumentText = $arguments -join ' '
    if (-not [string]::IsNullOrWhiteSpace($ForwardedArgumentText)) {
        $argumentText += ' ' + $ForwardedArgumentText
    }

    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentText -NoNewWindow -Wait -PassThru
    $exitCode = $process.ExitCode
} finally {
    if (Test-Path -LiteralPath $StageRoot -PathType Container) {
        try {
            Remove-Item -LiteralPath $StageRoot -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Error -Message "Could not clean trusted FastPatch staging directory '$StageRoot': $($_.Exception.Message)" -ErrorAction Continue
            if ($exitCode -eq 0) { $exitCode = 1 }
        }
    }
}

exit $exitCode
'@

    $result = $template
    $result = $result.Replace('__PAYLOAD_ROOT__', (ConvertTo-Base64Utf8 -Value ([IO.Path]::GetFullPath($PayloadRoot))))
    $result = $result.Replace('__INSTALLER_SNAPSHOT__', (ConvertTo-Base64Utf8 -Value ([IO.Path]::GetFullPath($InstallerSnapshotPath))))
    $result = $result.Replace('__MANIFEST_SNAPSHOT__', (ConvertTo-Base64Utf8 -Value ([IO.Path]::GetFullPath($ManifestSnapshotPath))))
    $result = $result.Replace('__INSTALLER_HASH__', $InstallerHash.ToLowerInvariant())
    $result = $result.Replace('__MANIFEST_HASH__', $ManifestHash.ToLowerInvariant())
    $result = $result.Replace('__INSTALLER_FILENAME__', (ConvertTo-Base64Utf8 -Value $InstallerFileName))
    $result = $result.Replace('__FORWARDED_ARGUMENTS__', (ConvertTo-Base64Utf8 -Value $ForwardedArgumentText))
    $result = $result.Replace('__STAGE_ID__', $StageId.ToLowerInvariant())
    return $result
}

function Invoke-TrustedStage {
    param(
        [Parameter(Mandatory = $true)] [string] $PayloadRoot,
        [Parameter(Mandatory = $true)] [string] $InstallerSnapshotPath,
        [Parameter(Mandatory = $true)] [string] $InstallerHash,
        [Parameter(Mandatory = $true)] [string] $ManifestSnapshotPath,
        [Parameter(Mandatory = $true)] [string] $ManifestHash,
        [Parameter(Mandatory = $true)] [string] $InstallerFileName,
        [AllowEmptyString()] [string] $ForwardedArgumentText = ''
    )

    $stageId = [Guid]::NewGuid().ToString('N')
    $bootstrap = Get-TrustedStageBootstrap `
        -PayloadRoot $PayloadRoot `
        -InstallerSnapshotPath $InstallerSnapshotPath `
        -InstallerHash $InstallerHash `
        -ManifestSnapshotPath $ManifestSnapshotPath `
        -ManifestHash $ManifestHash `
        -InstallerFileName $InstallerFileName `
        -ForwardedArgumentText $ForwardedArgumentText `
        -StageId $stageId
    $encodedBootstrap = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bootstrap))
    $bootstrapArguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedBootstrap"

    if (-not (Test-IsAdministrator)) {
        $process = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList $bootstrapArguments `
            -Verb RunAs `
            -Wait `
            -PassThru
    } else {
        $process = Start-Process -FilePath 'powershell.exe' `
            -ArgumentList $bootstrapArguments `
            -NoNewWindow `
            -Wait `
            -PassThru
    }

    return $process.ExitCode
}

function Assert-TrustedStage {
    param(
        [Parameter(Mandatory = $true)] [string] $StagePath,
        [Parameter(Mandatory = $true)] [string] $PayloadPath
    )

    if (-not (Test-IsAdministrator)) {
        throw 'Trusted FastPatch staging can only be consumed by an administrator process.'
    }

    $programFiles = [IO.Path]::GetFullPath(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    ).TrimEnd('\', '/')
    $resolvedStage = [IO.Path]::GetFullPath($StagePath).TrimEnd('\', '/')
    if (-not (Split-Path -Path $resolvedStage -Parent).Equals($programFiles, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Path $resolvedStage -Leaf) -notmatch '^\.ProtonVPNFastPatchStage-[0-9a-fA-F]{32}$') {
        throw "Trusted FastPatch stage is not a direct protected Program Files child: $resolvedStage"
    }

    $stagePrefix = $resolvedStage + [IO.Path]::DirectorySeparatorChar
    foreach ($candidate in @($PSCommandPath, $PayloadPath)) {
        $fullCandidate = [IO.Path]::GetFullPath($candidate)
        if (-not $fullCandidate.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Trusted FastPatch consumer path is outside the protected stage: $fullCandidate"
        }
    }

    $administratorSid = 'S-1-5-32-544'
    $systemSid = 'S-1-5-18'
    $items = @(
        Get-Item -LiteralPath $resolvedStage -Force
        Get-ChildItem -LiteralPath $resolvedStage -Recurse -Force
    )

    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Trusted FastPatch stage contains a reparse point: $($item.FullName)"
        }

        $acl = Get-Acl -LiteralPath $item.FullName
        if (-not $acl.AreAccessRulesProtected) {
            throw "Trusted FastPatch stage ACL inheritance is not protected: $($item.FullName)"
        }

        $ownerSid = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($ownerSid -ne $administratorSid) {
            throw "Trusted FastPatch stage object is not owned by Administrators: $($item.FullName)"
        }

        $fullControlSids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
                continue
            }
            $sid = $rule.IdentityReference.Value
            if ($sid -notin @($administratorSid, $systemSid)) {
                throw "Trusted FastPatch stage grants access to unexpected identity '$sid': $($item.FullName)"
            }
            if (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq
                [Security.AccessControl.FileSystemRights]::FullControl) {
                $null = $fullControlSids.Add($sid)
            }
        }

        if (-not $fullControlSids.Contains($administratorSid) -or
            -not $fullControlSids.Contains($systemSid)) {
            throw "Trusted FastPatch stage object is missing required Administrator/SYSTEM full control: $($item.FullName)"
        }
    }
}
'''

def inject_shared(text, label):
    return regex_once(
        text,
        r"^function Restart-Elevated \{.*?^\}\n\n(?=function Get-VersionSortValue)",
        SHARED + "\n\n",
        label + " replace elevation function",
    )

def add_trusted_param(text, label):
    old = "    [switch] $ValidateOnly,\n\n    [switch] $PauseBeforeExit"
    new = "    [switch] $ValidateOnly,\n\n    [string] $TrustedStagePath = '',\n\n    [switch] $PauseBeforeExit"
    return replace_once(text, old, new, label + " add TrustedStagePath")

def capture_manifest_text(text, complete):
    old = r'''    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Patch manifest is not valid JSON: $($_.Exception.Message)"
    }
'''
    new = r'''    try {
        $manifestJson = Get-Content -LiteralPath $manifestPath -Raw
        $manifest = $manifestJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Patch manifest is not valid JSON: $($_.Exception.Message)"
    }
'''
    text = replace_once(text, old, new, "capture validated manifest text")
    marker = "    return $manifest\n}\n\nfunction "
    idx = text.find(marker)
    if idx < 0:
        raise RuntimeError("capture validated manifest: return marker not found")
    text = text[:idx] + "    $script:ValidatedManifestText = $manifestJson\n" + text[idx:]
    return text

BASE_PREFLIGHT = r'''if (-not $ValidateOnly -and [string]::IsNullOrWhiteSpace($TrustedStagePath)) {
    $preflightDirectory = Join-Path (
        [System.IO.Path]::GetTempPath()
    ) ("ProtonVPNPatchPreflight-{0}" -f [Guid]::NewGuid().ToString('N'))
    $stageExitCode = 1

    try {
        New-Item -ItemType Directory -Path $preflightDirectory -Force | Out-Null
        $preflightPayloadRoot = Resolve-PatchSource -WorkingDirectory $preflightDirectory
        $null = Test-PatchPayload `
            -PayloadRoot $preflightPayloadRoot `
            -ExpectedTargetVersion $TargetVersion

        $installerSnapshot = Join-Path $preflightDirectory 'Install-ProtonVPNPatch.snapshot.ps1'
        $manifestSnapshot = Join-Path $preflightDirectory 'patch-manifest.snapshot.json'
        $installerHash = Write-TrustedSnapshot `
            -Path $installerSnapshot `
            -Content ([string] $MyInvocation.MyCommand.ScriptContents)
        $manifestHash = Write-TrustedSnapshot `
            -Path $manifestSnapshot `
            -Content $script:ValidatedManifestText

        $forwardedArguments = @(
            '-InstallRoot',
            (ConvertTo-QuotedProcessArgument -Value ([IO.Path]::GetFullPath($InstallRoot))),
            '-BackupRetentionCount',
            [string] $BackupRetentionCount
        )
        if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
            $forwardedArguments += '-TargetVersion'
            $forwardedArguments += ConvertTo-QuotedProcessArgument -Value $TargetVersion
        }
        if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
            $forwardedArguments += '-BackupRoot'
            $forwardedArguments += ConvertTo-QuotedProcessArgument -Value ([IO.Path]::GetFullPath($BackupRoot))
        }
        if ($NoRestart) { $forwardedArguments += '-NoRestart' }
        if ($RestartClient) { $forwardedArguments += '-RestartClient' }
        if ($PauseBeforeExit) { $forwardedArguments += '-PauseBeforeExit' }
        if ($WhatIfPreference) { $forwardedArguments += '-WhatIf' }

        Write-Host 'Patch payload validation succeeded; staging immutable bytes for administrator use.'
        $stageExitCode = Invoke-TrustedStage `
            -PayloadRoot $preflightPayloadRoot `
            -InstallerSnapshotPath $installerSnapshot `
            -InstallerHash $installerHash `
            -ManifestSnapshotPath $manifestSnapshot `
            -ManifestHash $manifestHash `
            -InstallerFileName 'Install-ProtonVPNPatch.ps1' `
            -ForwardedArgumentText ($forwardedArguments -join ' ')
    } finally {
        if (Test-Path -LiteralPath $preflightDirectory -PathType Container) {
            Remove-Item -LiteralPath $preflightDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    exit $stageExitCode
}

if (-not $ValidateOnly) {
    Assert-TrustedStage -StagePath $TrustedStagePath -PayloadPath $PatchPath
}
'''

COMPLETE_SECURE_HELPERS = r'''
function New-AdministratorOnlyDirectorySecurity {
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($administratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach ($sid in @($administratorsSid, $systemSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        $null = $security.AddAccessRule($rule)
    }
    return $security
}

function New-AdministratorOnlyFileSecurity {
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $security = [Security.AccessControl.FileSecurity]::new()
    $security.SetOwner($administratorsSid)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($administratorsSid, $systemSid)) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)
        $null = $security.AddAccessRule($rule)
    }
    return $security
}

function New-AdministratorOnlyDirectory {
    param([Parameter(Mandatory = $true)] [string] $Path)

    if (Test-Path -LiteralPath $Path) {
        throw "Protected staging directory already exists: $Path"
    }
    [IO.DirectoryInfo]::new($Path).Create((New-AdministratorOnlyDirectorySecurity))
}

function Ensure-AdministratorOnlySubdirectory {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath)) { return }
    $current = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    foreach ($segment in $RelativePath.Split([char[]] @('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer -or
                (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
                throw "Protected staging directory is unsafe: $current"
            }
        } else {
            New-AdministratorOnlyDirectory -Path $current
        }
    }
}

function Copy-AdministratorOnlyFile {
    param(
        [Parameter(Mandatory = $true)] [string] $SourcePath,
        [Parameter(Mandatory = $true)] [string] $DestinationPath,
        [Parameter(Mandatory = $true)] [string] $ExpectedHash
    )

    $source = [IO.File]::Open(
        $SourcePath,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    $destination = $null
    try {
        $destination = [IO.FileStream]::new(
            $DestinationPath,
            [IO.FileMode]::CreateNew,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [IO.FileShare]::None,
            65536,
            [IO.FileOptions]::SequentialScan,
            (New-AdministratorOnlyFileSecurity))
        $source.CopyTo($destination)
        $destination.Flush()
    } finally {
        if ($null -ne $destination) { $destination.Dispose() }
        $source.Dispose()
    }

    $actualHash = (Get-FileHash -LiteralPath $DestinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $actualHash.Equals($ExpectedHash.Trim().ToLowerInvariant(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Protected version payload hash mismatch for '$DestinationPath'."
    }
}

function Write-AdministratorOnlyTextFile {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Content
    )

    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
    $destination = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::CreateNew,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [IO.FileShare]::None,
        65536,
        [IO.FileOptions]::SequentialScan,
        (New-AdministratorOnlyFileSecurity))
    try {
        $destination.Write($bytes, 0, $bytes.Length)
        $destination.Flush()
    } finally {
        $destination.Dispose()
    }
}
'''

COMPLETE_STAGE_BLOCK = r'''    if (-not $ValidateOnly -and [string]::IsNullOrWhiteSpace($TrustedStagePath)) {
        $installerSnapshot = Join-Path $workingDirectory 'Install-ProtonVPNCompletePatch.snapshot.ps1'
        $manifestSnapshot = Join-Path $workingDirectory 'patch-manifest.snapshot.json'
        $installerHash = Write-TrustedSnapshot `
            -Path $installerSnapshot `
            -Content ([string] $MyInvocation.MyCommand.ScriptContents)
        $manifestHash = Write-TrustedSnapshot `
            -Path $manifestSnapshot `
            -Content $script:ValidatedManifestText

        $forwardedArguments = @(
            '-InstallRoot',
            (ConvertTo-QuotedProcessArgument -Value ([IO.Path]::GetFullPath($InstallRoot))),
            '-BackupRetentionCount',
            [string] $BackupRetentionCount
        )
        if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
            $forwardedArguments += '-TargetVersion'
            $forwardedArguments += ConvertTo-QuotedProcessArgument -Value $TargetVersion
        }
        if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
            $forwardedArguments += '-BackupRoot'
            $forwardedArguments += ConvertTo-QuotedProcessArgument -Value ([IO.Path]::GetFullPath($BackupRoot))
        }
        if ($NoRestart) { $forwardedArguments += '-NoRestart' }
        if ($RestartClient) { $forwardedArguments += '-RestartClient' }
        if ($PauseBeforeExit) { $forwardedArguments += '-PauseBeforeExit' }
        if ($WhatIfPreference) { $forwardedArguments += '-WhatIf' }

        Write-Host 'Complete FastPatch payload validation succeeded; staging immutable bytes for administrator use.'
        $stageExitCode = Invoke-TrustedStage `
            -PayloadRoot $payloadRoot `
            -InstallerSnapshotPath $installerSnapshot `
            -InstallerHash $installerHash `
            -ManifestSnapshotPath $manifestSnapshot `
            -ManifestHash $manifestHash `
            -InstallerFileName 'Install-ProtonVPNCompletePatch.ps1' `
            -ForwardedArgumentText ($forwardedArguments -join ' ')
        exit $stageExitCode
    }

    if (-not $ValidateOnly) {
        Assert-TrustedStage -StagePath $TrustedStagePath -PayloadPath $PatchPath
    }

'''

def patch_base():
    path = "scripts/Install-ProtonVPNPatch.ps1"
    text = read(path)
    text = add_trusted_param(text, "base")
    text = inject_shared(text, "base")
    text = capture_manifest_text(text, False)
    start = text.index("if (-not $ValidateOnly -and -not (Test-IsAdministrator)) {")
    end = text.index("\n$mutex = New-Object", start)
    text = text[:start] + BASE_PREFLIGHT + text[end:]
    if "Restart-Elevated" in text:
        raise RuntimeError("base: stale Restart-Elevated reference remains")
    write(path, text)

def patch_complete():
    path = "scripts/Install-ProtonVPNCompletePatch.ps1"
    text = read(path)
    text = add_trusted_param(text, "complete")
    text = inject_shared(text, "complete")
    text = capture_manifest_text(text, True)

    marker = "\nfunction New-VersionPayload {"
    text = replace_once(text, marker, COMPLETE_SECURE_HELPERS + marker, "complete secure version helpers")

    old_sig = r'''        [Parameter(Mandatory = $true)] [string] $Destination
    )

    Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
'''
    new_sig = r'''        [Parameter(Mandatory = $true)] [string] $Destination,
        [switch] $TrustedDestination
    )

    Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
    if ($TrustedDestination) {
        New-AdministratorOnlyDirectory -Path $Destination
    } else {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }
'''
    text = replace_once(text, old_sig, new_sig, "complete trusted New-VersionPayload signature")

    old_copy = r'''        $sourcePath = Join-Path $PayloadRoot $relativePath
        $targetPath = Join-Path $Destination $relativePath
        New-Item -ItemType Directory -Force -Path (Split-Path -Path $targetPath -Parent) | Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
'''
    new_copy = r'''        $sourcePath = Join-Path $PayloadRoot $relativePath
        $targetPath = Join-Path $Destination $relativePath
        if ($TrustedDestination) {
            $relativeDirectory = Split-Path -Path $relativePath -Parent
            Ensure-AdministratorOnlySubdirectory -Root $Destination -RelativePath $relativeDirectory
            Copy-AdministratorOnlyFile `
                -SourcePath $sourcePath `
                -DestinationPath $targetPath `
                -ExpectedHash ([string] $file.sha256)
        } else {
            New-Item -ItemType Directory -Force -Path (Split-Path -Path $targetPath -Parent) | Out-Null
            Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
        }
'''
    text = replace_once(text, old_copy, new_copy, "complete protected version file copy")

    old_manifest_write = r'''    $versionManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Destination 'patch-manifest.json') -Encoding utf8
'''
    new_manifest_write = r'''    $versionManifestJson = $versionManifest | ConvertTo-Json -Depth 6
    $versionManifestPath = Join-Path $Destination 'patch-manifest.json'
    if ($TrustedDestination) {
        Write-AdministratorOnlyTextFile -Path $versionManifestPath -Content $versionManifestJson
    } else {
        $versionManifestJson | Set-Content -LiteralPath $versionManifestPath -Encoding utf8
    }
'''
    text = replace_once(text, old_manifest_write, new_manifest_write, "complete protected version manifest")

    old_flow = r'''    $resolvedTargetVersion = ([string] $manifest.targetVersion).Trim().TrimStart([char[]] @('v', 'V'))

    $versionPayload = Join-Path $workingDirectory 'VersionPayload'
    New-VersionPayload -PayloadRoot $payloadRoot -Manifest $manifest -Destination $versionPayload
    $baseInstallerPath = Join-Path $payloadRoot 'Tools\Install-ProtonVPNPatch.base.ps1'

    if ($ValidateOnly) {
'''
    new_flow = r'''    $resolvedTargetVersion = ([string] $manifest.targetVersion).Trim().TrimStart([char[]] @('v', 'V'))

''' + COMPLETE_STAGE_BLOCK + r'''    $versionPayload = if ($ValidateOnly) {
        Join-Path $workingDirectory 'VersionPayload'
    } else {
        Join-Path $TrustedStagePath 'VersionPayload'
    }
    New-VersionPayload `
        -PayloadRoot $payloadRoot `
        -Manifest $manifest `
        -Destination $versionPayload `
        -TrustedDestination:(-not $ValidateOnly)
    $baseInstallerPath = Join-Path $payloadRoot 'Tools\Install-ProtonVPNPatch.base.ps1'

    if ($ValidateOnly) {
'''
    text = replace_once(text, old_flow, new_flow, "complete stage before version payload")

    old_elevate = r'''        if (-not (Test-IsAdministrator)) {
            Write-Host 'Complete FastPatch payload validation succeeded; requesting administrator access.'
            Restart-Elevated
        }

'''
    text = replace_once(text, old_elevate, "", "complete remove old elevation")
    if "Restart-Elevated" in text:
        raise RuntimeError("complete: stale Restart-Elevated reference remains")

    old_base_args = r'''    if ($ValidateOnly) { $arguments += '-ValidateOnly' }
    if ($WhatIfPreference) { $arguments += '-WhatIf' }

    $process = Start-Process -FilePath 'powershell.exe' `
'''
    new_base_args = r'''    if ($ValidateOnly) { $arguments += '-ValidateOnly' }
    if (-not [string]::IsNullOrWhiteSpace($TrustedStagePath)) {
        $arguments += '-TrustedStagePath'
        $arguments += ConvertTo-QuotedProcessArgument -Value ([IO.Path]::GetFullPath($TrustedStagePath))
    }
    if ($WhatIfPreference) { $arguments += '-WhatIf' }

    $process = Start-Process -FilePath 'powershell.exe' `
'''
    text = replace_once(text, old_base_args, new_base_args, "complete forward trusted stage to helper")
    write(path, text)

def patch_sfx():
    path = "scripts/New-ProtonVPNPatchSfx.ps1"
    text = read(path)
    old = r'''            if ($removedElevationTreeWait -and -not $insertedSingleProcessWait -and $line.Trim() -eq 'exit $process.ExitCode') {
                '    $process.WaitForExit()'
                $insertedSingleProcessWait = $true
            }
'''
    new = r'''            if ($removedElevationTreeWait -and -not $insertedSingleProcessWait -and
                $line.Trim() -in @('exit $process.ExitCode', 'return $process.ExitCode')) {
                '    $process.WaitForExit()'
                $insertedSingleProcessWait = $true
            }
'''
    text = replace_once(text, old, new, "SFX elevation direct-process wait")
    write(path, text)

def patch_complete_tests():
    path = ".github/scripts/test-complete-fast-patch.ps1"
    text = read(path)
    old = r'''    Assert-Condition ($content.Contains('-NoNewWindow')) `
        'Complete FastPatch base-installer delegation must reuse the existing console.'
'''
    new = r'''    Assert-Condition ($content.Contains('-NoNewWindow')) `
        'Complete FastPatch base-installer delegation must reuse the existing console.'
    Assert-Condition (-not $content.Contains("'-File', (ConvertTo-QuotedProcessArgument -Value `$PSCommandPath)")) `
        'Complete FastPatch must not cross UAC by reopening the mutable original script path.'
    Assert-Condition ($content.Contains('-EncodedCommand')) `
        'Complete FastPatch must use an inline encoded bootstrap across the elevation boundary.'
    Assert-Condition ($content.Contains('Assert-TrustedStage -StagePath $TrustedStagePath -PayloadPath $PatchPath')) `
        'Complete FastPatch must verify the protected stage before privileged consumption.'
    Assert-Condition ($content.Contains("`$arguments += '-TrustedStagePath'")) `
        'Complete FastPatch must keep the base helper inside the same protected stage.'
'''
    text = replace_once(text, old, new, "complete test staging contracts")
    old_call = r'''    Test-InstallerLifecycleContracts
    $runtimeFixture = Test-RuntimeDependencyClosure
'''
    new_call = r'''    Test-InstallerLifecycleContracts
    & (Join-Path $PSScriptRoot 'test-fast-patch-secure-staging.ps1')
    if ($LASTEXITCODE -ne 0) {
        throw "FastPatch secure staging tests failed with exit code $LASTEXITCODE."
    }
    $runtimeFixture = Test-RuntimeDependencyClosure
'''
    text = replace_once(text, old_call, new_call, "complete test invoke secure staging")
    write(path, text)

def main():
    patch_base()
    patch_complete()
    patch_sfx()
    patch_complete_tests()
    print("Applied FastPatch immutable staging source transformations.")

if __name__ == "__main__":
    main()
