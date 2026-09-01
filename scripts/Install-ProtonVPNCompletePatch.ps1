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
    param([Parameter(Mandatory = $true)] [string] $Value)

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
        $expectedSizeValue = [long] $ExpectedSize
        if ($actualSize -ne $expectedSizeValue) {
            throw "Trusted staging size mismatch for '$Destination'. Expected $expectedSizeValue, found $actualSize."
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
    param([Parameter(Mandatory = $true)] [System.IO.DirectoryInfo] $Directory)

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

function Resolve-PayloadRoot {
    param([Parameter(Mandatory = $true)] [string] $Root)

    $manifestPath = Join-Path $Root 'patch-manifest.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Root).Path
    }

    $topLevelDirectories = @(Get-ChildItem -LiteralPath $Root -Directory)
    $topLevelFiles = @(Get-ChildItem -LiteralPath $Root -File)
    if ($topLevelDirectories.Count -eq 1 -and $topLevelFiles.Count -eq 0) {
        return Resolve-PayloadRoot -Root $topLevelDirectories[0].FullName
    }

    $manifests = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter 'patch-manifest.json')
    if ($manifests.Count -ne 1) {
        throw "Complete patch must contain exactly one patch-manifest.json below '$Root'; found $($manifests.Count)."
    }
    return $manifests[0].Directory.FullName
}

function Resolve-PatchSource {
    param([Parameter(Mandatory = $true)] [string] $WorkingDirectory)

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

function Test-CompletePatchPayload {
    param(
        [Parameter(Mandatory = $true)] [string] $PayloadRoot,
        [string] $ExpectedTargetVersion
    )

    $resolvedPayloadRoot = [System.IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\', '/')
    $reparsePoints = @(
        @(
            Get-Item -LiteralPath $resolvedPayloadRoot -Force
            Get-ChildItem -LiteralPath $resolvedPayloadRoot -Recurse -Force |
                Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }
        ) | Where-Object { ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0 }
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

    foreach ($requiredProperty in @(
        'schemaVersion', 'targetVersion', 'buildMode', 'sourceCommit', 'files',
        'completeRuntimeCoverage', 'launcherIncluded', 'upstreamBaseCommit'
    )) {
        if ($null -eq $manifest.PSObject.Properties[$requiredProperty]) {
            throw "Complete patch manifest is missing required property '$requiredProperty'."
        }
    }

    if ([int] $manifest.schemaVersion -ne 2) {
        throw "Complete FastPatch requires manifest schema version 2; found $($manifest.schemaVersion)."
    }
    if (-not [bool] $manifest.completeRuntimeCoverage) {
        throw 'Complete FastPatch manifest does not declare completeRuntimeCoverage=true.'
    }
    if (([string] $manifest.buildMode) -notin @('client', 'service', 'both')) {
        throw "Patch manifest buildMode is invalid: $($manifest.buildMode)"
    }
    if ([string]::IsNullOrWhiteSpace([string] $manifest.sourceCommit)) {
        throw 'Patch manifest sourceCommit cannot be empty.'
    }
    if (([string] $manifest.upstreamBaseCommit) -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Complete patch manifest upstreamBaseCommit is invalid: $($manifest.upstreamBaseCommit)"
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
        throw 'Complete patch manifest does not declare any payload files.'
    }

    $installRootEntries = @()
    $toolEntries = @()
    foreach ($file in $manifestFiles) {
        foreach ($requiredFileProperty in @('path', 'scope', 'size', 'sha256')) {
            if ($null -eq $file.PSObject.Properties[$requiredFileProperty]) {
                throw "Complete patch manifest file entry is missing '$requiredFileProperty'."
            }
        }

        $scope = ([string] $file.scope).Trim()
        if ($scope -notin @('version', 'installRoot', 'tool')) {
            throw "Complete patch manifest contains unsupported install scope '$scope' for '$($file.path)'."
        }

        $relativePath = ([string] $file.path).Trim().Replace('/', '\')
        $segments = $relativePath.Split([char[]] @('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains(':') -or
            $segments -contains '..') {
            throw "Patch manifest contains an unsafe payload path: $($file.path)"
        }
        if ($relativePath.StartsWith('ServiceData\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch payload must not modify runtime ServiceData: $relativePath"
        }
        if (-not $declaredPaths.Add($relativePath)) {
            throw "Patch manifest declares the payload path more than once: $relativePath"
        }

        if ($scope -eq 'installRoot') {
            if (-not $relativePath.Equals('ProtonVPN.Launcher.exe', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Unsupported install-root payload file: $relativePath"
            }
            $installRootEntries += $file
        } elseif ($scope -eq 'tool') {
            if (-not $relativePath.Equals('Tools\Install-ProtonVPNPatch.base.ps1', [StringComparison]::OrdinalIgnoreCase)) {
                throw "Unsupported complete-patch tool payload file: $relativePath"
            }
            $toolEntries += $file
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

    $launcherExpected = [bool] $manifest.launcherIncluded
    if ($launcherExpected -ne ($installRootEntries.Count -eq 1)) {
        throw 'Complete patch launcherIncluded metadata does not match the install-root launcher payload.'
    }
    if ($toolEntries.Count -ne 1) {
        throw 'Complete patch must contain exactly one validated base-installer helper.'
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

function New-VersionPayload {
    param(
        [Parameter(Mandatory = $true)] [string] $PayloadRoot,
        [Parameter(Mandatory = $true)] $Manifest,
        [Parameter(Mandatory = $true)] [string] $Destination,
        [switch] $TrustedDestination
    )

    Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
    if ($TrustedDestination) {
        New-AdministratorOnlyDirectory -Path $Destination
    } else {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    $versionFiles = @($Manifest.files | Where-Object { ([string] $_.scope) -eq 'version' })
    if ($versionFiles.Count -eq 0) {
        throw 'Complete patch does not contain any version-folder payload files.'
    }

    foreach ($file in $versionFiles) {
        $relativePath = ([string] $file.path).Replace('/', '\')
        $sourcePath = Join-Path $PayloadRoot $relativePath
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
    }

    $versionManifestFiles = @(
        foreach ($file in $versionFiles) {
            [ordered]@{
                path = [string] $file.path
                size = [long] $file.size
                sha256 = [string] $file.sha256
            }
        }
    )
    $sourceRefValue = if ($null -ne $Manifest.PSObject.Properties['sourceRef']) { [string] $Manifest.sourceRef } else { '' }
    $workflowRunIdValue = if ($null -ne $Manifest.PSObject.Properties['workflowRunId']) { [string] $Manifest.workflowRunId } else { '' }
    $versionManifest = [ordered]@{
        schemaVersion = 1
        targetVersion = [string] $Manifest.targetVersion
        buildMode = [string] $Manifest.buildMode
        sourceCommit = [string] $Manifest.sourceCommit
        sourceRef = $sourceRefValue
        workflowRunId = $workflowRunIdValue
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
        files = $versionManifestFiles
    }
    $versionManifestJson = $versionManifest | ConvertTo-Json -Depth 6
    $versionManifestPath = Join-Path $Destination 'patch-manifest.json'
    if ($TrustedDestination) {
        Write-AdministratorOnlyTextFile -Path $versionManifestPath -Content $versionManifestJson
    } else {
        $versionManifestJson | Set-Content -LiteralPath $versionManifestPath -Encoding utf8
    }
}

function Get-BackupDirectories {
    param(
        [Parameter(Mandatory = $true)] [string] $Root,
        [Parameter(Mandatory = $true)] [string] $TargetFolderName
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }
    $pattern = '^' + [Regex]::Escape($TargetFolderName) + '-backup-\d{8}-\d{6}$'
    return @(
        Get-ChildItem -LiteralPath $Root -Directory |
            Where-Object { $_.Name -match $pattern } |
            Sort-Object Name -Descending
    )
}

function Stop-RootLauncherProcesses {
    param([Parameter(Mandatory = $true)] [string] $LauncherPath)

    $normalizedLauncher = [System.IO.Path]::GetFullPath($LauncherPath)
    foreach ($process in @(Get-Process -Name 'ProtonVPN.Launcher' -ErrorAction SilentlyContinue)) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($process.Path) -and
                [System.IO.Path]::GetFullPath($process.Path).Equals($normalizedLauncher, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                $process.WaitForExit(5000)
            }
        } catch {
            throw "Could not stop ProtonVPN.Launcher before patching '$normalizedLauncher': $($_.Exception.Message)"
        }
    }
}

function Test-ClientRunningForTarget {
    param([Parameter(Mandatory = $true)] [string] $TargetDirectory)

    $normalizedTarget = [System.IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\') + '\'
    foreach ($process in @(Get-Process -Name 'ProtonVPN.Client*' -ErrorAction SilentlyContinue)) {
        try {
            $processPath = $process.Path
        } catch {
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($processPath) -and
            [System.IO.Path]::GetFullPath($processPath).StartsWith($normalizedTarget, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}
function Invoke-BaseInstaller {
    param(
        [Parameter(Mandatory = $true)] [string] $BaseInstallerPath,
        [Parameter(Mandatory = $true)] [string] $VersionPayloadPath,
        [Parameter(Mandatory = $true)] [string] $ResolvedTargetVersion
    )

    $arguments = @(
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', (ConvertTo-QuotedProcessArgument -Value $BaseInstallerPath),
        '-PatchPath', (ConvertTo-QuotedProcessArgument -Value $VersionPayloadPath),
        '-InstallRoot', (ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($InstallRoot))),
        '-TargetVersion', (ConvertTo-QuotedProcessArgument -Value $ResolvedTargetVersion),
        '-BackupRetentionCount', [string] $BackupRetentionCount
    )
    if (-not [string]::IsNullOrWhiteSpace($BackupRoot)) {
        $arguments += '-BackupRoot'
        $arguments += ConvertTo-QuotedProcessArgument -Value ([System.IO.Path]::GetFullPath($BackupRoot))
    }
    if ($NoRestart) { $arguments += '-NoRestart' }
    if ($RestartClient) { $arguments += '-RestartClient' }
    if ($ValidateOnly) { $arguments += '-ValidateOnly' }
    if (-not [string]::IsNullOrWhiteSpace($TrustedStagePath)) {
        $arguments += '-TrustedStagePath'
        $arguments += ConvertTo-QuotedProcessArgument -Value ([IO.Path]::GetFullPath($TrustedStagePath))
    }
    if ($WhatIfPreference) { $arguments += '-WhatIf' }

    $process = Start-Process -FilePath (Get-WindowsPowerShellPath) `
        -ArgumentList ($arguments -join ' ') `
        -NoNewWindow `
        -Wait `
        -PassThru
    return $process.ExitCode
}

$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNCompletePatch-{0}" -f [Guid]::NewGuid().ToString('N'))
$pendingRootBackupDirectory = $null
$rootLauncherPatched = $false
$rootLauncherPersisted = $false
$clientWasRunningBeforeInstall = $false
$exitCode = 1

try {
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    $payloadRoot = Resolve-PatchSource -WorkingDirectory $workingDirectory
    $manifest = Test-CompletePatchPayload -PayloadRoot $payloadRoot -ExpectedTargetVersion $TargetVersion
    $resolvedTargetVersion = ([string] $manifest.targetVersion).Trim().TrimStart([char[]] @('v', 'V'))

    if (-not $ValidateOnly -and [string]::IsNullOrWhiteSpace($TrustedStagePath)) {
        $installerSnapshot = Join-Path $workingDirectory 'Install-ProtonVPNCompletePatch.snapshot.ps1'
        $manifestSnapshot = Join-Path $workingDirectory 'patch-manifest.snapshot.json'
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

    $versionPayload = if ($ValidateOnly) {
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
        $baseExitCode = Invoke-BaseInstaller `
            -BaseInstallerPath $baseInstallerPath `
            -VersionPayloadPath $versionPayload `
            -ResolvedTargetVersion $resolvedTargetVersion
        if ($baseExitCode -ne 0) {
            throw "Base version-folder payload validation failed with exit code $baseExitCode."
        }
        Write-Host "Complete FastPatch payload validation succeeded for Proton VPN $resolvedTargetVersion." -ForegroundColor Green
        $exitCode = 0
    } else {
        $targetDirectory = Resolve-TargetDirectory
        $clientWasRunningBeforeInstall = Test-ClientRunningForTarget -TargetDirectory $targetDirectory
        $installedVersion = (Split-Path -Leaf $targetDirectory).TrimStart([char[]] @('v', 'V'))
        if (-not $installedVersion.Equals($resolvedTargetVersion, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch targets Proton VPN $resolvedTargetVersion, but the selected installation is $installedVersion."
        }

        $resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
        $resolvedBackupRoot = if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
            Split-Path -Path $targetDirectory -Parent
        } else {
            [System.IO.Path]::GetFullPath($BackupRoot)
        }
        New-Item -ItemType Directory -Path $resolvedBackupRoot -Force | Out-Null

        $targetFolderName = Split-Path -Leaf $targetDirectory
        $backupsBefore = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($backup in Get-BackupDirectories -Root $resolvedBackupRoot -TargetFolderName $targetFolderName) {
            $null = $backupsBefore.Add([System.IO.Path]::GetFullPath($backup.FullName))
        }

        if ([bool] $manifest.launcherIncluded) {
            $launcherSource = Join-Path $payloadRoot 'ProtonVPN.Launcher.exe'
            $launcherTarget = Join-Path $resolvedInstallRoot 'ProtonVPN.Launcher.exe'
            if (-not (Test-Path -LiteralPath $launcherTarget -PathType Leaf)) {
                throw "Installed ProtonVPN.Launcher.exe was not found at the install root: $launcherTarget"
            }

            if ($WhatIfPreference) {
                Write-Host "What if: replace root launcher '$launcherTarget'."
            } elseif ($PSCmdlet.ShouldProcess($launcherTarget, 'Replace root ProtonVPN launcher')) {
                $pendingRootBackupDirectory = Join-Path $resolvedBackupRoot ('.pending-fastpatch-root-' + [Guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $pendingRootBackupDirectory -Force | Out-Null
                $pendingLauncherBackup = Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe'
                Copy-Item -LiteralPath $launcherTarget -Destination $pendingLauncherBackup -Force
                $rootLauncherPatched = $true

                Stop-RootLauncherProcesses -LauncherPath $launcherTarget
                Copy-Item -LiteralPath $launcherSource -Destination $launcherTarget -Force
                $expectedLauncherHash = [string] (@($manifest.files | Where-Object { ([string] $_.scope) -eq 'installRoot' })[0].sha256)
                $installedLauncherHash = (Get-FileHash -LiteralPath $launcherTarget -Algorithm SHA256).Hash.ToLowerInvariant()
                if (-not $installedLauncherHash.Equals($expectedLauncherHash, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Installed ProtonVPN.Launcher.exe hash verification failed after copy.'
                }
            } else {
                throw 'Root ProtonVPN launcher update was not approved; complete FastPatch installation cancelled.'
            }
        }

        $baseExitCode = Invoke-BaseInstaller `
            -BaseInstallerPath $baseInstallerPath `
            -VersionPayloadPath $versionPayload `
            -ResolvedTargetVersion $resolvedTargetVersion
        if ($baseExitCode -ne 0) {
            throw "Version-folder FastPatch installation failed with exit code $baseExitCode."
        }

        if ($rootLauncherPatched -and $pendingRootBackupDirectory) {
            $newBackups = @(
                Get-BackupDirectories -Root $resolvedBackupRoot -TargetFolderName $targetFolderName |
                    Where-Object { -not $backupsBefore.Contains([System.IO.Path]::GetFullPath($_.FullName)) }
            )
            if ($newBackups.Count -ge 1) {
                $selectedBackup = $newBackups | Sort-Object Name -Descending | Select-Object -First 1
                if ($newBackups.Count -gt 1) {
                    Write-Warning "Multiple new version backups were found; associating the root launcher backup with '$($selectedBackup.FullName)'."
                }
                $installRootBackupDir = Join-Path $selectedBackup.FullName 'InstallRoot'
                New-Item -ItemType Directory -Path $installRootBackupDir -Force | Out-Null
                Copy-Item `
                    -LiteralPath (Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe') `
                    -Destination (Join-Path $installRootBackupDir 'ProtonVPN.Launcher.exe') `
                    -Force
                Remove-Item -LiteralPath $pendingRootBackupDirectory -Recurse -Force
                $pendingRootBackupDirectory = $null
                $rootLauncherPersisted = $true
                Write-Host "Root launcher backup retained with version backup: $installRootBackupDir"
            } else {
                Write-Warning "Version patch succeeded, but its new backup directory could not be identified. Root launcher backup remains at '$pendingRootBackupDirectory'."
            }
        }

        if (-not $NoRestart -and $RestartClient -and $clientWasRunningBeforeInstall -and
            -not (Test-ClientRunningForTarget -TargetDirectory $targetDirectory)) {
            $clientExecutable = Join-Path $targetDirectory 'ProtonVPN.Client.exe'
            if (Test-Path -LiteralPath $clientExecutable -PathType Leaf) {
                try {
                    Write-Host 'Base installer did not leave the previously running Proton VPN Client active; restarting it now.' -ForegroundColor Yellow
                    Start-Process -FilePath $clientExecutable | Out-Null
                } catch {
                    Write-Warning "Could not restart Proton VPN Client after complete patch installation: $($_.Exception.Message)"
                }
            }
        }

        Write-Host 'Complete Proton VPN FastPatch installed successfully.' -ForegroundColor Green
        $exitCode = 0
    }
} catch {
    Write-Error -Message $_.Exception.Message -ErrorAction Continue

    if ($rootLauncherPatched -and -not $rootLauncherPersisted -and $pendingRootBackupDirectory) {
        $pendingLauncherBackup = Join-Path $pendingRootBackupDirectory 'ProtonVPN.Launcher.exe'
        $launcherTarget = Join-Path ([System.IO.Path]::GetFullPath($InstallRoot)) 'ProtonVPN.Launcher.exe'
        if (Test-Path -LiteralPath $pendingLauncherBackup -PathType Leaf) {
            try {
                Stop-RootLauncherProcesses -LauncherPath $launcherTarget
                Copy-Item -LiteralPath $pendingLauncherBackup -Destination $launcherTarget -Force
                Write-Host 'Root ProtonVPN launcher restored after failed installation.' -ForegroundColor Yellow
                Remove-Item -LiteralPath $pendingRootBackupDirectory -Recurse -Force -ErrorAction SilentlyContinue
                $pendingRootBackupDirectory = $null
            } catch {
                Write-Error -Message "Could not restore the root ProtonVPN launcher. Backup remains at '$pendingRootBackupDirectory'. $($_.Exception.Message)" -ErrorAction Continue
            }
        }
    }
    $exitCode = 1
} finally {
    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }

    if ($PauseBeforeExit) {
        try { Read-Host 'Press Enter to close' | Out-Null } catch {}
    }
}

exit $exitCode
