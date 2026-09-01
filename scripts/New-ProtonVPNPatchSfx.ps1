[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [Alias('PatchDirectory')]
    [ValidateNotNullOrEmpty()]
    [string] $PatchPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $OutputPath,

    [string] $InstallerScriptPath = (Join-Path $PSScriptRoot 'Install-ProtonVPNPatch.ps1'),

    [string] $LauncherPath = (Join-Path $PSScriptRoot 'Install-ProtonVPNPatch.cmd'),

    [ValidateNotNullOrEmpty()]
    [string] $FriendlyName = 'Proton VPN Custom Patch Installer'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-PatchManifestJson {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedPatchPath,

        [Parameter(Mandatory = $true)]
        [bool] $IsPatchZip
    )

    if (-not $IsPatchZip) {
        $manifestFiles = @(
            Get-ChildItem -LiteralPath $ResolvedPatchPath -Recurse -File -Filter 'patch-manifest.json'
        )

        if ($manifestFiles.Count -eq 0) {
            throw "Patch manifest was not found below: $ResolvedPatchPath"
        }

        if ($manifestFiles.Count -ne 1) {
            $paths = ($manifestFiles | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
            throw "Patch payload contains multiple patch-manifest.json files:$([Environment]::NewLine)$paths"
        }

        return Get-Content -LiteralPath $manifestFiles[0].FullName -Raw
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ResolvedPatchPath)
    try {
        $manifestEntries = @(
            $archive.Entries | Where-Object {
                $normalizedName = $_.FullName.Replace('\\', '/').Trim('/')
                $normalizedName -eq 'patch-manifest.json' -or
                    $normalizedName.EndsWith('/patch-manifest.json', [StringComparison]::OrdinalIgnoreCase)
            }
        )

        if ($manifestEntries.Count -eq 0) {
            throw "Patch ZIP does not contain patch-manifest.json: $ResolvedPatchPath"
        }

        if ($manifestEntries.Count -ne 1) {
            $paths = ($manifestEntries | ForEach-Object { $_.FullName }) -join [Environment]::NewLine
            throw "Patch ZIP contains multiple patch-manifest.json entries:$([Environment]::NewLine)$paths"
        }

        $stream = $manifestEntries[0].Open()
        try {
            $reader = New-Object System.IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
            try {
                return $reader.ReadToEnd()
            } finally {
                $reader.Dispose()
            }
        } finally {
            $stream.Dispose()
        }
    } finally {
        $archive.Dispose()
    }
}

function Get-SfxLoaderScript {
    param(
        [Parameter(Mandatory = $true)] [string] $InstallerFileName,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $InstallerHash,
        [Parameter(Mandatory = $true)] [string] $PayloadFileName,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $PayloadHash,
        [Parameter(Mandatory = $true)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $TargetVersion
    )

    $template = @'
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Decode-FastPatchValue([string] $Value) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value)) }
function Get-FastPatchBytesSha256([byte[]] $Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Test-FastPatchAdministrator {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
$root = [IO.Path]::GetFullPath((Get-Location).Path).TrimEnd('\', '/')
$installer = Join-Path $root (Decode-FastPatchValue '__INSTALLER__')
$payload = Join-Path $root (Decode-FastPatchValue '__PAYLOAD__')
foreach ($path in @($root, $installer, $payload)) {
    $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Compiled FastPatch SFX source contains a reparse point: $path"
    }
}
$source = [IO.File]::Open($installer, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
$memory = New-Object IO.MemoryStream
try {
    $source.CopyTo($memory)
    $bytes = $memory.ToArray()
} finally {
    $memory.Dispose()
    $source.Dispose()
}
$actualInstallerHash = Get-FastPatchBytesSha256 $bytes
if (-not $actualInstallerHash.Equals('__INSTALLER_HASH__', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Compiled FastPatch SFX installer hash mismatch. Expected __INSTALLER_HASH__, found $actualInstallerHash."
}
$scriptText = [Text.Encoding]::UTF8.GetString($bytes)
if ($scriptText.Length -gt 0 -and $scriptText[0] -eq [char]0xFEFF) { $scriptText = $scriptText.Substring(1) }
if (-not (Test-FastPatchAdministrator)) {
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FastPatchConsoleWindow { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); }'
    [FastPatchConsoleWindow]::ShowWindow([FastPatchConsoleWindow]::GetConsoleWindow(), 0) | Out-Null
}
$global:ProtonVpnFastPatchVerifiedSfxScriptText = $scriptText
& ([ScriptBlock]::Create($scriptText)) `
    -PatchPath $payload `
    -TargetVersion (Decode-FastPatchValue '__TARGET__') `
    -RestartClient `
    -PauseBeforeExit `
    -ExpectedPatchArchiveSha256 '__PAYLOAD_HASH__'
'@

    $utf8 = [Text.Encoding]::UTF8
    return $template.Replace('__INSTALLER__', [Convert]::ToBase64String($utf8.GetBytes($InstallerFileName))).Replace(
        '__INSTALLER_HASH__', $InstallerHash.Trim().ToLowerInvariant()).Replace(
        '__PAYLOAD__', [Convert]::ToBase64String($utf8.GetBytes($PayloadFileName))).Replace(
        '__PAYLOAD_HASH__', $PayloadHash.Trim().ToLowerInvariant()).Replace(
        '__TARGET__', [Convert]::ToBase64String($utf8.GetBytes($TargetVersion)))
}

function Get-CompiledSfxBootstrapSource {
    param(
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $InstallerHash,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $PayloadHash,
        [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-fA-F]{64}$')] [string] $LoaderHash
    )

    $source = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class FastPatchSfxBootstrap
{
    private const string InstallerResource = "FastPatch.Installer";
    private const string PayloadResource = "FastPatch.Payload";
    private const string LoaderResource = "FastPatch.Loader";
    private const string InstallerHash = "__INSTALLER_HASH__";
    private const string PayloadHash = "__PAYLOAD_HASH__";
    private const string LoaderHash = "__LOADER_HASH__";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetSystemDirectory(StringBuilder lpBuffer, uint uSize);

    private static string ToHex(byte[] bytes)
    {
        StringBuilder result = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; ++i)
        {
            result.Append(bytes[i].ToString("x2"));
        }
        return result.ToString();
    }

    private static byte[] ReadVerifiedResource(Assembly assembly, string resourceName, string expectedHash)
    {
        using (Stream source = assembly.GetManifestResourceStream(resourceName))
        {
            if (source == null)
            {
                throw new InvalidOperationException("Embedded FastPatch resource was not found: " + resourceName);
            }
            using (MemoryStream memory = new MemoryStream())
            {
                source.CopyTo(memory);
                byte[] bytes = memory.ToArray();
                using (SHA256 sha = SHA256.Create())
                {
                    string actualHash = ToHex(sha.ComputeHash(bytes));
                    if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Embedded FastPatch resource hash mismatch for " + resourceName + ". Expected " + expectedHash + ", found " + actualHash + ".");
                    }
                }
                return bytes;
            }
        }
    }

    private static FileStream WriteLockedResource(byte[] bytes, string destinationPath, string expectedHash)
    {
        FileStream stream = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            65536,
            FileOptions.SequentialScan);
        try
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
            stream.Position = 0;
            using (SHA256 sha = SHA256.Create())
            {
                string actualHash = ToHex(sha.ComputeHash(stream));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("FastPatch SFX extracted resource hash mismatch for " + destinationPath + ". Expected " + expectedHash + ", found " + actualHash + ".");
                }
            }
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string GetWindowsPowerShellPath()
    {
        StringBuilder buffer = new StringBuilder(32768);
        uint length = GetSystemDirectory(buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new InvalidOperationException("Could not resolve the Windows system directory. Win32 error: " + Marshal.GetLastWin32Error());
        }
        string path = Path.Combine(buffer.ToString(), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Windows PowerShell was not found in the protected system directory.", path);
        }
        return path;
    }

    private static int RunPowerShell(string loaderText, string workingDirectory)
    {
        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(loaderText));
        if (encodedCommand.Length > 30000)
        {
            throw new InvalidOperationException("Embedded FastPatch loader exceeds the safe Windows command-line budget: " + encodedCommand.Length + " characters.");
        }

        ProcessStartInfo startInfo = new ProcessStartInfo();
        startInfo.FileName = GetWindowsPowerShellPath();
        startInfo.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand;
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = false;

        using (Process process = Process.Start(startInfo))
        {
            if (process == null)
            {
                throw new InvalidOperationException("Could not start Windows PowerShell for FastPatch.");
            }
            process.WaitForExit();
            return process.ExitCode;
        }
    }

    public static int Main()
    {
        string temporaryRoot = Path.Combine(Path.GetTempPath(), "ProtonVPNFastPatchSfx-" + Guid.NewGuid().ToString("N"));
        FileStream installerLock = null;
        FileStream payloadLock = null;
        int exitCode = 1;
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            Assembly assembly = Assembly.GetExecutingAssembly();
            byte[] loaderBytes = ReadVerifiedResource(assembly, LoaderResource, LoaderHash);
            byte[] installerBytes = ReadVerifiedResource(assembly, InstallerResource, InstallerHash);
            byte[] payloadBytes = ReadVerifiedResource(assembly, PayloadResource, PayloadHash);

            string installerPath = Path.Combine(temporaryRoot, "Install-ProtonVPNPatch.ps1");
            string payloadPath = Path.Combine(temporaryRoot, "payload.zip");
            installerLock = WriteLockedResource(installerBytes, installerPath, InstallerHash);
            payloadLock = WriteLockedResource(payloadBytes, payloadPath, PayloadHash);

            string loaderText = new UTF8Encoding(false, true).GetString(loaderBytes);
            if (loaderText.Length > 0 && loaderText[0] == '\ufeff')
            {
                loaderText = loaderText.Substring(1);
            }
            exitCode = RunPowerShell(loaderText, temporaryRoot);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FastPatch SFX bootstrap failed: " + exception.Message);
            exitCode = 1;
        }
        finally
        {
            if (payloadLock != null) payloadLock.Dispose();
            if (installerLock != null) installerLock.Dispose();
            try
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
            catch (Exception cleanupException)
            {
                Console.Error.WriteLine("FastPatch SFX cleanup failed: " + cleanupException.Message);
                if (exitCode == 0) exitCode = 1;
            }
        }
        return exitCode;
    }
}
'@

    return $source.Replace('__INSTALLER_HASH__', $InstallerHash.Trim().ToLowerInvariant()).Replace(
        '__PAYLOAD_HASH__', $PayloadHash.Trim().ToLowerInvariant()).Replace(
        '__LOADER_HASH__', $LoaderHash.Trim().ToLowerInvariant())
}

function Get-FrameworkCscPath {
    $windowsDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    if ([string]::IsNullOrWhiteSpace($windowsDirectory)) {
        throw 'Could not resolve the Windows directory for the .NET Framework compiler.'
    }

    foreach ($relativePath in @(
        'Microsoft.NET\Framework64\v4.0.30319\csc.exe',
        'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    )) {
        $candidate = Join-Path $windowsDirectory $relativePath
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    throw 'The .NET Framework C# compiler was not found. FastPatch SFX packaging requires .NET Framework 4.x.'
}

if ($env:OS -ne 'Windows_NT') {
    throw 'The self-extractor builder requires Windows.'
}

$resolvedPatchPath = (Resolve-Path -LiteralPath $PatchPath -ErrorAction Stop).Path
$resolvedInstallerScriptPath = (Resolve-Path -LiteralPath $InstallerScriptPath -ErrorAction Stop).Path
$resolvedLauncherPath = (Resolve-Path -LiteralPath $LauncherPath -ErrorAction Stop).Path
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Path $resolvedOutputPath -Parent

if ([System.IO.Path]::GetExtension($resolvedOutputPath) -ne '.exe') {
    throw "OutputPath must end in .exe: $resolvedOutputPath"
}

if (-not (Test-Path -LiteralPath $resolvedLauncherPath -PathType Leaf)) {
    throw "LauncherPath was not found: $resolvedLauncherPath"
}

$isPatchZip = Test-Path -LiteralPath $resolvedPatchPath -PathType Leaf
$isPatchDirectory = Test-Path -LiteralPath $resolvedPatchPath -PathType Container
if (-not $isPatchZip -and -not $isPatchDirectory) {
    throw "PatchPath must be a .zip archive or directory: $resolvedPatchPath"
}

if ($isPatchZip -and [System.IO.Path]::GetExtension($resolvedPatchPath) -ne '.zip') {
    throw "PatchPath must be a .zip archive or directory: $resolvedPatchPath"
}

if ($isPatchDirectory) {
    $patchFiles = @(Get-ChildItem -LiteralPath $resolvedPatchPath -Recurse -File)
    if ($patchFiles.Count -eq 0) {
        throw "Patch directory does not contain any files: $resolvedPatchPath"
    }
}

$windowsPowerShellPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShellPath -PathType Leaf)) {
    throw "Windows PowerShell was not found: $windowsPowerShellPath"
}
& $windowsPowerShellPath `
    -NoProfile `
    -NonInteractive `
    -ExecutionPolicy Bypass `
    -File $resolvedInstallerScriptPath `
    -PatchPath $resolvedPatchPath `
    -ValidateOnly
if ($LASTEXITCODE -ne 0) {
    throw "Patch payload validation failed with exit code $LASTEXITCODE."
}

$manifestJson = Get-PatchManifestJson -ResolvedPatchPath $resolvedPatchPath -IsPatchZip $isPatchZip
try {
    $manifest = $manifestJson | ConvertFrom-Json -ErrorAction Stop
} catch {
    throw "Patch manifest is not valid JSON: $($_.Exception.Message)"
}

foreach ($requiredProperty in @('schemaVersion', 'targetVersion', 'buildMode', 'sourceCommit', 'files')) {
    if ($null -eq $manifest.PSObject.Properties[$requiredProperty]) {
        throw "Patch manifest is missing required property '$requiredProperty'."
    }
}

$schemaVersion = [int] $manifest.schemaVersion
if ($schemaVersion -notin @(1, 2)) {
    throw "Unsupported patch manifest schema version: $schemaVersion"
}
if ($schemaVersion -eq 2) {
    $coverageProperty = $manifest.PSObject.Properties['completeRuntimeCoverage']
    if ($null -eq $coverageProperty -or -not [bool] $coverageProperty.Value) {
        throw 'Schema-v2 patch manifest must declare completeRuntimeCoverage=true.'
    }
}

$manifestTargetVersion = ([string] $manifest.targetVersion).Trim()
if ($manifestTargetVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Patch manifest targetVersion must be a numeric three-part release version. Received '$manifestTargetVersion'."
}

Write-Host "Packaging patch for Proton VPN $manifestTargetVersion ($($manifest.buildMode))."
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf) {
    Remove-Item -LiteralPath $resolvedOutputPath -Force
}

$workingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("ProtonVPNSfxBuild-{0}" -f [Guid]::NewGuid().ToString('N'))
$payloadFileName = 'payload.zip'
$payloadPath = Join-Path $workingDirectory $payloadFileName
$installerFileName = 'Install-ProtonVPNPatch.ps1'
$packagedInstallerScriptPath = Join-Path $workingDirectory $installerFileName
$loaderScriptPath = Join-Path $workingDirectory 'FastPatchSfxLoader.ps1'
$bootstrapSourcePath = Join-Path $workingDirectory 'FastPatchSfxBootstrap.cs'
$buildSucceeded = $false

try {
    New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
    Copy-Item -LiteralPath $resolvedInstallerScriptPath -Destination $packagedInstallerScriptPath -Force

    $removedElevationTreeWait = $false
    $insertedSingleProcessWait = $false
    $installerLines = @(
        foreach ($line in Get-Content -LiteralPath $packagedInstallerScriptPath) {
            if (-not $removedElevationTreeWait -and $line.Trim() -eq '-Wait `') {
                $removedElevationTreeWait = $true
                continue
            }

            if ($removedElevationTreeWait -and -not $insertedSingleProcessWait -and
                $line.Trim() -in @('exit $process.ExitCode', 'return $process.ExitCode')) {
                '    $process.WaitForExit()'
                $insertedSingleProcessWait = $true
            }

            $line
        }
    )

    if (-not $removedElevationTreeWait -or -not $insertedSingleProcessWait) {
        throw 'Could not update the packaged installer elevation wait behavior.'
    }
    Set-Content -LiteralPath $packagedInstallerScriptPath -Value $installerLines -Encoding UTF8

    if ($isPatchZip) {
        Copy-Item -LiteralPath $resolvedPatchPath -Destination $payloadPath -Force
    } else {
        Compress-Archive `
            -Path (Join-Path $resolvedPatchPath '*') `
            -DestinationPath $payloadPath `
            -CompressionLevel Optimal `
            -Force
    }

    $installerHash = (Get-FileHash -LiteralPath $packagedInstallerScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $loaderScript = Get-SfxLoaderScript `
        -InstallerFileName $installerFileName `
        -InstallerHash $installerHash `
        -PayloadFileName $payloadFileName `
        -PayloadHash $payloadHash `
        -TargetVersion $manifestTargetVersion
    [IO.File]::WriteAllText($loaderScriptPath, $loaderScript, [Text.UTF8Encoding]::new($false))
    $loaderHash = (Get-FileHash -LiteralPath $loaderScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $bootstrapSource = Get-CompiledSfxBootstrapSource `
        -InstallerHash $installerHash `
        -PayloadHash $payloadHash `
        -LoaderHash $loaderHash
    [IO.File]::WriteAllText($bootstrapSourcePath, $bootstrapSource, [Text.UTF8Encoding]::new($false))

    $cscPath = Get-FrameworkCscPath
    $compilerArguments = @(
        '/nologo',
        '/target:exe',
        '/platform:anycpu',
        '/optimize+',
        "/out:$resolvedOutputPath",
        "/resource:$loaderScriptPath,FastPatch.Loader",
        "/resource:$packagedInstallerScriptPath,FastPatch.Installer",
        "/resource:$payloadPath,FastPatch.Payload",
        $bootstrapSourcePath
    )

    Write-Host "Compiling immutable FastPatch SFX bootstrap with: $cscPath"
    & $cscPath @compilerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "FastPatch SFX bootstrap compilation failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
        throw "FastPatch SFX compiler did not create the expected output: $resolvedOutputPath"
    }
    if ((Get-Item -LiteralPath $resolvedOutputPath).Length -le 0) {
        throw "FastPatch SFX compiler created an empty output: $resolvedOutputPath"
    }

    $buildSucceeded = $true
    Write-Host "Created immutable self-contained patch installer: $resolvedOutputPath" -ForegroundColor Green
} finally {
    if (-not $buildSucceeded -and (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
        Remove-Item -LiteralPath $resolvedOutputPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $workingDirectory -PathType Container) {
        Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
