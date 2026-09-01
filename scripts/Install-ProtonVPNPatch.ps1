[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $PatchPath,

    [ValidateNotNullOrEmpty()]
    [string] $InstallRoot = 'C:\Program Files\Proton\VPN',

    [string] $TargetVersion,

    [string] $BackupRoot,

    [ValidateRange(0, 100)]
    [int] $BackupRetentionCount = 3,

    [switch] $NoRestart,

    [switch] $RestartClient,

    [switch] $ValidateOnly,

    [string] $ExpectedPatchArchiveSha256 = '',

    [string] $TrustedStagePath = '',

    [switch] $PauseBeforeExit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:TrustedArchiveManifestText = ''
$script:FastPatchInvocationScriptText = ''
$scriptContentsProperty = $MyInvocation.MyCommand.PSObject.Properties['ScriptContents']
if ($null -ne $scriptContentsProperty -and
    -not [string]::IsNullOrWhiteSpace([string] $scriptContentsProperty.Value)) {
    $script:FastPatchInvocationScriptText = [string] $scriptContentsProperty.Value
}
if ([string]::IsNullOrWhiteSpace($script:FastPatchInvocationScriptText)) {
    $verifiedSfxScript = Get-Variable `
        -Name 'ProtonVpnFastPatchVerifiedSfxScriptText' `
        -Scope Global `
        -ErrorAction SilentlyContinue
    if ($null -ne $verifiedSfxScript -and
        -not [string]::IsNullOrWhiteSpace([string] $verifiedSfxScript.Value)) {
        $script:FastPatchInvocationScriptText = [string] $verifiedSfxScript.Value
    }
}
if ([string]::IsNullOrWhiteSpace($script:FastPatchInvocationScriptText)) {
    throw 'FastPatch could not capture the installer script bytes for trusted staging.'
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SystemExecutablePath {
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

function ConvertTo-QuotedProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    if ($Value.Contains('"')) {
        throw "Arguments containing quote characters are not supported: $Value"
    }

    return '"' + $Value + '"'
}

function ConvertTo-Base64Utf8 {
    param([Parameter(Mandatory = $true)] [AllowEmptyString()] [string] $Value)
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Value))
}

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

function Get-SystemExecutablePath([string] $RelativePath) {
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

    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) -ArgumentList $argumentText -NoNewWindow -PassThru
    $process.WaitForExit()
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
    $encodedBootstrap = ConvertTo-CompressedEncodedCommand -ScriptText $bootstrap
    $bootstrapArguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encodedBootstrap"

    if (-not (Test-IsAdministrator)) {
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


function Get-VersionSortValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.DirectoryInfo] $Directory
    )

    $versionText = $Directory.Name.TrimStart([char[]] @('v', 'V'))
    $parsedVersion = [Version]::new(0, 0)
    if ([Version]::TryParse($versionText, [ref] $parsedVersion)) {
        return $parsedVersion
    }

    return [Version]::new(0, 0)
}

function Resolve-TargetDirectory {
    if (-not (Test-Path -LiteralPath $InstallRoot -PathType Container)) {
        throw "Proton VPN install root was not found: $InstallRoot"
    }

    if (-not [string]::IsNullOrWhiteSpace($TargetVersion)) {
        $normalizedVersion = if ($TargetVersion.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
            $TargetVersion
        } else {
            "v$TargetVersion"
        }

        $explicitTarget = Join-Path $InstallRoot $normalizedVersion
        if (-not (Test-Path -LiteralPath $explicitTarget -PathType Container)) {
            throw "Requested Proton VPN version folder was not found: $explicitTarget"
        }

        return (Resolve-Path -LiteralPath $explicitTarget).Path
    }

    $versionDirectories = @(
        Get-ChildItem -LiteralPath $InstallRoot -Directory |
            Where-Object { $_.Name -match '^v\d+(?:\.\d+){1,3}$' } |
            Sort-Object @{ Expression = { Get-VersionSortValue -Directory $_ }; Descending = $true },
                        @{ Expression = { $_.LastWriteTimeUtc }; Descending = $true }
    )

    if ($versionDirectories.Count -eq 0) {
        throw "No Proton VPN version folders were found below: $InstallRoot"
    }

    return $versionDirectories[0].FullName
}

function Resolve-PatchSource {
    param(
        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory
    )

    if ([string]::IsNullOrWhiteSpace($PatchPath)) {
        $defaultPayloadZip = Join-Path $PSScriptRoot 'payload.zip'
        if (Test-Path -LiteralPath $defaultPayloadZip -PathType Leaf) {
            $script:PatchPath = $defaultPayloadZip
        } else {
            $script:PatchPath = $PSScriptRoot
        }
    }

    $script:TrustedArchiveManifestText = ''
    $resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path
    if (Test-Path -LiteralPath $resolvedPatchPath -PathType Leaf) {
        if ([System.IO.Path]::GetExtension($resolvedPatchPath) -ne '.zip') {
            throw "PatchPath must be a directory or a .zip archive: $resolvedPatchPath"
        }

        $archiveGuard = $null
        try {
            if (-not [string]::IsNullOrWhiteSpace($ExpectedPatchArchiveSha256)) {
                $expectedArchiveHash = $ExpectedPatchArchiveSha256.Trim().ToLowerInvariant()
                if ($expectedArchiveHash -notmatch '^[0-9a-f]{64}$') {
                    throw "ExpectedPatchArchiveSha256 is invalid: $ExpectedPatchArchiveSha256"
                }

                # Keep a read-only sharing handle open through Expand-Archive. Other readers are
                # allowed, but same-user writers/deleters cannot replace the hash-pinned archive
                # between verification and extraction.
                $archiveGuard = [IO.File]::Open(
                    $resolvedPatchPath,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read)
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $actualArchiveHash = ([BitConverter]::ToString(
                        $sha256.ComputeHash($archiveGuard))).Replace('-', '').ToLowerInvariant()
                } finally {
                    $sha256.Dispose()
                }
                if (-not $actualArchiveHash.Equals($expectedArchiveHash, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "FastPatch archive hash mismatch. Expected $expectedArchiveHash, found $actualArchiveHash."
                }


                # The verified ZIP is the SFX trust anchor. Capture the manifest from that exact
                # archive stream before extracting anything into ordinary-user-writable temp.
                # Validation must never trust a post-extraction replacement manifest.
                $archiveGuard.Position = 0
                Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
                $zipArchive = [IO.Compression.ZipArchive]::new(
                    $archiveGuard,
                    [IO.Compression.ZipArchiveMode]::Read,
                    $true)
                try {
                    $manifestEntries = @(
                        $zipArchive.Entries |
                            Where-Object {
                                $_.Name.Equals('patch-manifest.json', [StringComparison]::OrdinalIgnoreCase)
                            }
                    )
                    if ($manifestEntries.Count -ne 1) {
                        throw "Verified FastPatch archive must contain exactly one patch-manifest.json entry; found $($manifestEntries.Count)."
                    }

                    $manifestStream = $manifestEntries[0].Open()
                    $manifestReader = [IO.StreamReader]::new(
                        $manifestStream,
                        [Text.UTF8Encoding]::new($false),
                        $true)
                    try {
                        $script:TrustedArchiveManifestText = $manifestReader.ReadToEnd()
                    } finally {
                        $manifestReader.Dispose()
                    }
                    if ([string]::IsNullOrWhiteSpace($script:TrustedArchiveManifestText)) {
                        throw 'Verified FastPatch archive contains an empty patch manifest.'
                    }
                } finally {
                    $zipArchive.Dispose()
                }
            }

            $expandedPath = Join-Path $WorkingDirectory 'ExpandedPatch'
            New-Item -ItemType Directory -Path $expandedPath -Force | Out-Null
            Expand-Archive -LiteralPath $resolvedPatchPath -DestinationPath $expandedPath -Force
            return Resolve-PayloadRoot -Root $expandedPath
        } finally {
            if ($null -ne $archiveGuard) { $archiveGuard.Dispose() }
        }
    }

    if (-not (Test-Path -LiteralPath $resolvedPatchPath -PathType Container)) {
        throw "PatchPath does not exist: $resolvedPatchPath"
    }

    return Resolve-PayloadRoot -Root $resolvedPatchPath
}

function Resolve-PayloadRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root
    )

    $markerNames = @(
        'ProtonVPN.Client.dll',
        'ProtonVPNService.dll',
        'ProtonVPN.Client.pri',
        'App.xbf'
    )

    foreach ($markerName in $markerNames) {
        if (Test-Path -LiteralPath (Join-Path $Root $markerName) -PathType Leaf) {
            return (Resolve-Path -LiteralPath $Root).Path
        }
    }

    $topLevelDirectories = @(Get-ChildItem -LiteralPath $Root -Directory)
    $topLevelFiles = @(Get-ChildItem -LiteralPath $Root -File)
    if ($topLevelDirectories.Count -eq 1 -and $topLevelFiles.Count -eq 0) {
        return Resolve-PayloadRoot -Root $topLevelDirectories[0].FullName
    }

    $recursiveMarkers = @(
        foreach ($markerName in $markerNames) {
            Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $markerName -ErrorAction SilentlyContinue
        }
    )

    if ($recursiveMarkers.Count -eq 0) {
        throw 'The patch does not contain any expected Proton VPN payload files.'
    }

    $candidateRoots = @(
        $recursiveMarkers |
            ForEach-Object { $_.Directory.FullName } |
            Sort-Object -Unique
    )

    if ($candidateRoots.Count -ne 1) {
        $candidateText = $candidateRoots -join [Environment]::NewLine
        throw "The patch payload root is ambiguous. Candidate folders:$([Environment]::NewLine)$candidateText"
    }

    return $candidateRoots[0]
}

function Test-PatchPayload {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PayloadRoot,

        [string] $ExpectedTargetVersion
    )

    $resolvedPayloadRoot = [System.IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\', '/')
    $reparsePoints = @(
        @(
            Get-Item -LiteralPath $resolvedPayloadRoot -Force
            Get-ChildItem -LiteralPath $resolvedPayloadRoot -Recurse -Force |
                Where-Object {
                    ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
                }
        ) | Where-Object {
            ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        }
    )
    if ($reparsePoints.Count -gt 0) {
        throw "Patch payload must not contain symbolic links or other reparse points: $($reparsePoints[0].FullName)"
    }

    $manifestPath = Join-Path $resolvedPayloadRoot 'patch-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Patch manifest was not found at the payload root: $manifestPath"
    }

    try {
        $manifestJson = if (-not [string]::IsNullOrWhiteSpace($script:TrustedArchiveManifestText)) {
            $script:TrustedArchiveManifestText
        } else {
            Get-Content -LiteralPath $manifestPath -Raw
        }
        $manifest = $manifestJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "Patch manifest is not valid JSON: $($_.Exception.Message)"
    }

    foreach ($requiredProperty in @('schemaVersion', 'targetVersion', 'buildMode', 'sourceCommit', 'files')) {
        if ($null -eq $manifest.PSObject.Properties[$requiredProperty]) {
            throw "Patch manifest is missing required property '$requiredProperty'."
        }
    }

    if ([int] $manifest.schemaVersion -ne 1) {
        throw "Unsupported patch manifest schema version: $($manifest.schemaVersion)"
    }

    if (([string] $manifest.buildMode) -notin @('client', 'service', 'both')) {
        throw "Patch manifest buildMode is invalid: $($manifest.buildMode)"
    }

    if ([string]::IsNullOrWhiteSpace([string] $manifest.sourceCommit)) {
        throw 'Patch manifest sourceCommit cannot be empty.'
    }

    $manifestTargetVersion = ([string] $manifest.targetVersion).Trim().TrimStart([char[]] @('v', 'V'))
    if ($manifestTargetVersion -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "Patch manifest targetVersion is invalid: $($manifest.targetVersion)"
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetVersion)) {
        $normalizedExpectedVersion = $ExpectedTargetVersion.Trim().TrimStart([char[]] @('v', 'V'))
        if (-not $manifestTargetVersion.Equals($normalizedExpectedVersion, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch targets Proton VPN $manifestTargetVersion, not the requested version $normalizedExpectedVersion."
        }
    }

    $declaredPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $manifestFiles = @($manifest.files)
    if ($manifestFiles.Count -eq 0) {
        throw 'Patch manifest does not declare any payload files.'
    }

    foreach ($file in $manifestFiles) {
        $relativePath = ([string] $file.path).Trim().Replace('/', '\')
        $pathSegments = $relativePath.Split(
            [char[]] @('\', '/'),
            [System.StringSplitOptions]::RemoveEmptyEntries)

        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains(':') -or
            $pathSegments -contains '..') {
            throw "Patch manifest contains an unsafe payload path: $($file.path)"
        }

        if ($relativePath.StartsWith('ServiceData\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch payload must not modify runtime ServiceData: $relativePath"
        }

        if (-not $declaredPaths.Add($relativePath)) {
            throw "Patch manifest declares the payload path more than once: $relativePath"
        }

        $payloadPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedPayloadRoot $relativePath))
        $payloadRootPrefix = $resolvedPayloadRoot + [System.IO.Path]::DirectorySeparatorChar
        if (-not $payloadPath.StartsWith($payloadRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch manifest path escapes the payload root: $relativePath"
        }

        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Patch payload file declared by the manifest is missing: $relativePath"
        }

        $payloadFile = Get-Item -LiteralPath $payloadPath
        if ([long] $file.size -ne $payloadFile.Length) {
            throw "Patch payload size mismatch for '$relativePath'. Expected $($file.size), found $($payloadFile.Length)."
        }

        $expectedHash = ([string] $file.sha256).Trim().ToLowerInvariant()
        if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
            throw "Patch manifest contains an invalid SHA-256 value for '$relativePath'."
        }

        $actualHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $actualHash.Equals($expectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch payload hash mismatch for '$relativePath'. Expected $expectedHash, found $actualHash."
        }
    }

    $actualPayloadFiles = @(
        Get-ChildItem -LiteralPath $resolvedPayloadRoot -Recurse -File |
            Where-Object { -not $_.FullName.Equals($manifestPath, [StringComparison]::OrdinalIgnoreCase) }
    )
    foreach ($actualFile in $actualPayloadFiles) {
        $relativePath = $actualFile.FullName.Substring($resolvedPayloadRoot.Length).TrimStart('\', '/')
        if (-not $declaredPaths.Contains($relativePath)) {
            throw "Patch payload contains a file that is not declared by the manifest: $relativePath"
        }
    }

    if ($actualPayloadFiles.Count -ne $declaredPaths.Count) {
        throw "Patch manifest file count does not match the payload. Declared $($declaredPaths.Count), found $($actualPayloadFiles.Count)."
    }

    $script:ValidatedManifestText = $manifestJson
    return $manifest
}

function Invoke-Robocopy {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,

        [Parameter(Mandatory = $true)]
        [string] $Destination,

        [switch] $Mirror,

        [string[]] $ExcludedFiles = @(),

        [string[]] $ExcludedDirectories = @()
    )

    $copyMode = if ($Mirror) { '/MIR' } else { '/E' }
    $arguments = @(
        $Source,
        $Destination,
        $copyMode,
        '/COPY:DAT',
        '/DCOPY:DAT',
        '/R:2',
        '/W:1',
        '/XJ',
        '/NFL',
        '/NDL',
        '/NP'
    )

    if ($ExcludedFiles.Count -gt 0) {
        $arguments += '/XF'
        $arguments += $ExcludedFiles
    }

    if ($ExcludedDirectories.Count -gt 0) {
        $arguments += '/XD'
        $arguments += $ExcludedDirectories
    }

    $robocopyPath = Get-SystemExecutablePath -RelativePath 'robocopy.exe'
    & $robocopyPath @arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -gt 7) {
        throw "robocopy failed with exit code $exitCode while copying '$Source' to '$Destination'."
    }
}

function Get-ProtonServicesForTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetDirectory
    )

    $escapedTarget = [Regex]::Escape($TargetDirectory)
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
            Where-Object {
                $_.Name -like 'ProtonVPN*' -or
                $_.DisplayName -like 'ProtonVPN*' -or
                ([string] $_.PathName) -match $escapedTarget
            } |
            Sort-Object Name -Unique
    )
}

function Stop-ProtonProcessesForTarget {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetDirectory
    )

    $clientWasRunning = $false
    $normalizedTarget = $TargetDirectory.TrimEnd('\') + '\'

    foreach ($process in @(Get-Process)) {
        $processPath = $null
        try {
            $processPath = $process.Path
        } catch {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($processPath)) {
            continue
        }

        if ($processPath.StartsWith($normalizedTarget, [StringComparison]::OrdinalIgnoreCase)) {
            if ($process.ProcessName -like 'ProtonVPN.Client*') {
                $clientWasRunning = $true
            }

            try {
                if ($process.MainWindowHandle -ne 0) {
                    $null = $process.CloseMainWindow()
                    if ($process.WaitForExit(5000)) {
                        continue
                    }
                }
            } catch {
            }

            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            try {
                $process.WaitForExit(5000)
            } catch {
            }
        }
    }

    return $clientWasRunning
}

function Stop-ProtonServices {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Services
    )

    foreach ($service in @($Services | Sort-Object Name -Descending)) {
        $controller = Get-Service -Name $service.Name -ErrorAction Stop
        if ($controller.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
            continue
        }

        Stop-Service -Name $service.Name -Force -ErrorAction Stop
        $controller = Get-Service -Name $service.Name -ErrorAction Stop
        $controller.WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(20)
        )
    }
}

function Remove-OldBackups {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Root,

        [Parameter(Mandatory = $true)]
        [string] $TargetFolderName,

        [Parameter(Mandatory = $true)]
        [string] $CurrentBackupDirectory,

        [Parameter(Mandatory = $true)]
        [int] $RetentionCount
    )

    if ($RetentionCount -eq 0 -or -not (Test-Path -LiteralPath $Root -PathType Container)) {
        return
    }

    $backupNamePattern = '^' + [Regex]::Escape($TargetFolderName) + '-backup-\d{8}-\d{6}$'
    $backups = @(
        Get-ChildItem -LiteralPath $Root -Directory |
            Where-Object { $_.Name -match $backupNamePattern } |
            Sort-Object Name -Descending
    )

    $keepPaths = @([System.IO.Path]::GetFullPath($CurrentBackupDirectory))
    foreach ($backup in $backups) {
        if ($keepPaths.Count -ge $RetentionCount) {
            break
        }

        $fullPath = [System.IO.Path]::GetFullPath($backup.FullName)
        if ($keepPaths -notcontains $fullPath) {
            $keepPaths += $fullPath
        }
    }

    foreach ($backup in $backups) {
        $fullPath = [System.IO.Path]::GetFullPath($backup.FullName)
        if ($keepPaths -contains $fullPath) {
            continue
        }

        Write-Host "Removing old backup: $fullPath"
        Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
    }
}

if (-not $ValidateOnly -and [string]::IsNullOrWhiteSpace($TrustedStagePath)) {
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
            -Content $script:FastPatchInvocationScriptText
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

$mutex = New-Object Threading.Mutex($false, 'Global\ProtonVPNCustomPatchInstaller')
$hasMutex = $false
$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNPatch-{0}" -f [Guid]::NewGuid().ToString('N'))
$backupDirectory = $null
$resolvedBackupRoot = $null
$targetDirectory = $null
$targetFolderName = $null
$services = @()
$runningServiceNames = @()
$clientWasRunning = $false
$backupCompleted = $false
$installCompleted = $false
$exitCode = 1
$transcriptStarted = $false
$logPath = $null

if (-not $WhatIfPreference) {
    try {
        $logRoot = Join-Path $env:ProgramData 'ProtonVPN Custom Patch\Logs'
        New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
        $logPath = Join-Path $logRoot ("install-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
        Start-Transcript -LiteralPath $logPath -Force | Out-Null
        $transcriptStarted = $true
        Write-Host "Log:     $logPath"
    } catch {
        Write-Warning "Could not start installer logging: $($_.Exception.Message)"
    }
}

try {
    $hasMutex = $mutex.WaitOne(0, $false)
    if (-not $hasMutex) {
        throw 'Another Proton VPN patch installation is already running.'
    }

    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $payloadRoot = Resolve-PatchSource -WorkingDirectory $workingDirectory
    $manifest = Test-PatchPayload -PayloadRoot $payloadRoot -ExpectedTargetVersion $TargetVersion

    if ($ValidateOnly) {
        Write-Host "Patch payload validation succeeded for Proton VPN $($manifest.targetVersion)." -ForegroundColor Green
        $exitCode = 0
    } else {
        $targetDirectory = Resolve-TargetDirectory
        $installedVersion = (Split-Path -Leaf $targetDirectory).TrimStart([char[]] @('v', 'V'))
        $manifestVersion = ([string] $manifest.targetVersion).Trim().TrimStart([char[]] @('v', 'V'))
        if (-not $installedVersion.Equals($manifestVersion, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch targets Proton VPN $manifestVersion, but the selected installation is $installedVersion."
        }

        $resolvedBackupRoot = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
            Split-Path -Path $targetDirectory -Parent
        } else {
            [System.IO.Path]::GetFullPath($BackupRoot)
        }

    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $targetFolderName = Split-Path -Leaf $targetDirectory
    $backupDirectory = Join-Path $resolvedBackupRoot ("{0}-backup-{1}" -f $targetFolderName, $timestamp)

    $normalizedTargetDirectory = [System.IO.Path]::GetFullPath($targetDirectory).TrimEnd('\') + '\'
    $normalizedBackupDirectory = [System.IO.Path]::GetFullPath($backupDirectory).TrimEnd('\') + '\'
    if ($normalizedBackupDirectory.StartsWith($normalizedTargetDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup directory cannot be inside the Proton VPN version folder: $backupDirectory"
    }

    if (Test-Path -LiteralPath $backupDirectory) {
        throw "Backup directory already exists: $backupDirectory"
    }

    Write-Host "Target:  $targetDirectory"
    Write-Host "Payload: $payloadRoot"
    Write-Host "Backup:  $backupDirectory"

    $services = Get-ProtonServicesForTarget -TargetDirectory $targetDirectory
    $runningServiceNames = @(
        $services |
            Where-Object { $_.State -eq 'Running' } |
            ForEach-Object { [string] $_.Name }
    )

    if ($PSCmdlet.ShouldProcess($targetDirectory, 'Back up and install Proton VPN custom patch')) {
        New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

        Write-Host 'Closing Proton VPN client...'
        $clientWasRunning = Stop-ProtonProcessesForTarget -TargetDirectory $targetDirectory

        Write-Host 'Stopping Proton VPN services...'
        Stop-ProtonServices -Services $services

        # ServiceData contains live settings and access-restricted WireGuard key material.
        # Patch payloads cannot target it, so preserve it in place across backup and rollback.
        Write-Host 'Backing up installed program files while preserving runtime ServiceData...'
        Invoke-Robocopy `
            -Source $targetDirectory `
            -Destination $backupDirectory `
            -Mirror `
            -ExcludedDirectories @('ServiceData')
        $backupCompleted = $true

        Write-Host 'Applying patch files...'
        Invoke-Robocopy `
            -Source $payloadRoot `
            -Destination $targetDirectory `
            -ExcludedFiles @('Install-ProtonVPNPatch.ps1', 'Install-ProtonVPNPatch.cmd', 'payload.zip')

        $installCompleted = $true
        Write-Host 'Patch installed successfully.' -ForegroundColor Green
    }

        $exitCode = 0
    }
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue

    if ($backupCompleted -and -not $installCompleted -and $targetDirectory -and $backupDirectory) {
        Write-Warning 'Patch installation failed. Restoring the backup automatically...'
        try {
            Invoke-Robocopy `
                -Source $backupDirectory `
                -Destination $targetDirectory `
                -Mirror `
                -ExcludedDirectories @('ServiceData')
            Write-Host 'Backup restored successfully.' -ForegroundColor Yellow
        } catch {
            Write-Error `
                -Message "Automatic rollback failed. The backup remains at '$backupDirectory'. $($_.Exception.Message)" `
                -ErrorAction Continue
        }
    }

    if (-not $backupCompleted -and $backupDirectory -and
        (Test-Path -LiteralPath $backupDirectory -PathType Container)) {
        try {
            Remove-Item -LiteralPath $backupDirectory -Recurse -Force -ErrorAction Stop
            Write-Host 'Removed incomplete backup.' -ForegroundColor Yellow
        } catch {
            Write-Warning "Could not remove incomplete backup '$backupDirectory': $($_.Exception.Message)"
        }
    }

    $exitCode = 1
} finally {
    if (-not $NoRestart -and $targetDirectory) {
        foreach ($serviceName in $runningServiceNames) {
            try {
                Start-Service -Name $serviceName -ErrorAction Stop
            } catch {
                Write-Warning "Could not restart service '$serviceName': $($_.Exception.Message)"
            }
        }

        if ($RestartClient -and $clientWasRunning) {
            $clientExecutable = Join-Path $targetDirectory 'ProtonVPN.Client.exe'
            if (Test-Path -LiteralPath $clientExecutable -PathType Leaf) {
                try {
                    Start-Process -FilePath $clientExecutable | Out-Null
                } catch {
                    Write-Warning "Could not restart Proton VPN Client: $($_.Exception.Message)"
                }
            }
        }
    }

    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }

    $mutex.Dispose()
}

if ($installCompleted) {
    try {
        Remove-OldBackups `
            -Root $resolvedBackupRoot `
            -TargetFolderName $targetFolderName `
            -CurrentBackupDirectory $backupDirectory `
            -RetentionCount $BackupRetentionCount
    } catch {
        Write-Warning "Patch installed, but old backup cleanup failed: $($_.Exception.Message)"
    }

    Write-Host "Backup retained at: $backupDirectory"
    if ($clientWasRunning -and -not $RestartClient) {
        Write-Host 'Proton VPN Client was left closed to avoid changing the previous connection state.' -ForegroundColor Yellow
    }
} elseif ($exitCode -ne 0) {
    Write-Host 'Patch installation failed.' -ForegroundColor Red
}

if ($transcriptStarted) {
    Write-Host "Installer log retained at: $logPath"
    Stop-Transcript | Out-Null
}

if ($PauseBeforeExit) {
    Write-Host
    $null = Read-Host 'Press Enter to close this window'
}

exit $exitCode
