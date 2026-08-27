[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('client', 'service', 'both')]
    [string] $BuildMode,

    [ValidateNotNullOrEmpty()]
    [string] $CurrentPackagesPath = 'Directory.Packages.props',

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $BaselinePackagesPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $UpstreamBaseCommit,

    [ValidateNotNullOrEmpty()]
    [string] $ClientDependenciesPath = 'src/bin/ProtonVPN.Client.deps.json',

    [ValidateNotNullOrEmpty()]
    [string] $ServiceDependenciesPath = 'src/bin/win-x64/ProtonVPNService.deps.json',

    [ValidateNotNullOrEmpty()]
    [string] $ClientOutputDirectory = 'src/bin',

    [ValidateNotNullOrEmpty()]
    [string] $ServiceOutputDirectory = 'src/bin/win-x64',

    [ValidateNotNullOrEmpty()]
    [string] $StageDirectory = 'artifacts/runtime-dependency-output',

    [ValidateNotNullOrEmpty()]
    [string] $MetadataPath = 'artifacts/runtime-dependency-metadata.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-CentralPackageVersions {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Central package file was not found: $Path"
    }

    try {
        [xml] $document = Get-Content -LiteralPath $Path -Raw
    }
    catch {
        throw "Central package file is not valid XML: $Path. $($_.Exception.Message)"
    }

    $versions = @{}
    foreach ($node in @($document.Project.ItemGroup.PackageVersion)) {
        if ($null -eq $node) {
            continue
        }

        $id = [string] $node.Include
        if ([string]::IsNullOrWhiteSpace($id)) {
            $id = [string] $node.Update
        }
        if ([string]::IsNullOrWhiteSpace($id)) {
            continue
        }

        $version = [string] $node.Version
        if ([string]::IsNullOrWhiteSpace($version)) {
            $versionNode = $node.SelectSingleNode('Version')
            if ($null -ne $versionNode) {
                $version = [string] $versionNode.InnerText
            }
        }
        if ([string]::IsNullOrWhiteSpace($version)) {
            throw "PackageVersion '$id' does not declare a concrete Version in $Path"
        }

        $versions[$id.Trim()] = $version.Trim()
    }

    return $versions
}

function Resolve-SafeStageDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string[]] $ProtectedPaths
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $pathRoot = [System.IO.Path]::GetPathRoot($fullPath).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)

    if ($fullPath.Equals($pathRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime dependency stage directory must not be a filesystem root: $fullPath"
    }

    foreach ($protected in $ProtectedPaths) {
        if ([string]::IsNullOrWhiteSpace($protected)) {
            continue
        }

        $protectedPath = [System.IO.Path]::GetFullPath($protected).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $separator = [System.IO.Path]::DirectorySeparatorChar
        if ($fullPath.Equals($protectedPath, [StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith("$protectedPath$separator", [StringComparison]::OrdinalIgnoreCase) -or
            $protectedPath.StartsWith("$fullPath$separator", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Runtime dependency stage directory must not overlap protected input '$protectedPath': $fullPath"
        }
    }

    return $fullPath
}

function Get-DependencyDocument {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Dependency manifest was not found: $Path"
    }

    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        throw "Dependency manifest is not valid JSON: $Path. $($_.Exception.Message)"
    }
}

function Get-LibraryIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string] $LibraryKey
    )

    $separatorIndex = $LibraryKey.LastIndexOf('/')
    if ($separatorIndex -le 0 -or $separatorIndex -ge ($LibraryKey.Length - 1)) {
        throw "Unexpected dependency library key: $LibraryKey"
    }

    return [ordered]@{
        Id = $LibraryKey.Substring(0, $separatorIndex)
        Version = $LibraryKey.Substring($separatorIndex + 1)
    }
}

function Resolve-DependencyLibraryKey {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $Entries,

        [Parameter(Mandatory = $true)]
        [hashtable] $PackageKeysById,

        [Parameter(Mandatory = $true)]
        [string] $DependencyId,

        [string] $DependencyVersion = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($DependencyVersion)) {
        $exactKey = "$DependencyId/$DependencyVersion"
        if ($Entries.ContainsKey($exactKey)) {
            return $exactKey
        }
    }

    $keys = @($PackageKeysById[$DependencyId])
    if ($keys.Count -eq 1) {
        return [string] $keys[0]
    }
    if ($keys.Count -eq 0) {
        return ''
    }

    throw "Dependency graph contains multiple package versions for '$DependencyId' and no exact match for '$DependencyVersion': $($keys -join ', ')"
}

function Get-RuntimePackageClosure {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $Dependencies,

        [Parameter(Mandatory = $true)]
        [hashtable] $ChangedPackageIds
    )

    $runtimeTargetName = [string] $Dependencies.runtimeTarget.name
    if ([string]::IsNullOrWhiteSpace($runtimeTargetName)) {
        throw 'Dependency manifest does not declare runtimeTarget.name.'
    }

    $targetProperty = $Dependencies.targets.PSObject.Properties[$runtimeTargetName]
    if ($null -eq $targetProperty) {
        throw "Dependency manifest does not contain runtime target '$runtimeTargetName'."
    }

    $entries = @{}
    $packageKeysById = @{}
    foreach ($property in $targetProperty.Value.PSObject.Properties) {
        $identity = Get-LibraryIdentity -LibraryKey $property.Name
        $libraryMetadataProperty = $Dependencies.libraries.PSObject.Properties[$property.Name]
        $libraryType = if ($null -ne $libraryMetadataProperty) {
            [string] $libraryMetadataProperty.Value.type
        } else {
            ''
        }

        $entry = [pscustomobject]@{
            Key = $property.Name
            Id = [string] $identity.Id
            Version = [string] $identity.Version
            Type = $libraryType
            Value = $property.Value
        }
        $entries[$property.Name] = $entry

        if ($libraryType -eq 'package') {
            if (-not $packageKeysById.ContainsKey($entry.Id)) {
                $packageKeysById[$entry.Id] = @()
            }
            $packageKeysById[$entry.Id] = @($packageKeysById[$entry.Id]) + $entry.Key
        }
    }

    $queue = [System.Collections.Generic.Queue[string]]::new()
    $visited = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $entries.Values | Sort-Object Id, Version) {
        if ($entry.Type -eq 'package' -and $ChangedPackageIds.ContainsKey($entry.Id)) {
            $queue.Enqueue($entry.Key)
        }
    }

    while ($queue.Count -gt 0) {
        $key = $queue.Dequeue()
        if (-not $visited.Add($key)) {
            continue
        }

        $entry = $entries[$key]
        $dependenciesProperty = $entry.Value.PSObject.Properties['dependencies']
        if ($null -eq $dependenciesProperty) {
            continue
        }

        foreach ($dependency in $dependenciesProperty.Value.PSObject.Properties) {
            $dependencyKey = Resolve-DependencyLibraryKey `
                -Entries $entries `
                -PackageKeysById $packageKeysById `
                -DependencyId $dependency.Name `
                -DependencyVersion ([string] $dependency.Value)
            if ([string]::IsNullOrWhiteSpace($dependencyKey)) {
                continue
            }

            if ($entries[$dependencyKey].Type -eq 'package' -and -not $visited.Contains($dependencyKey)) {
                $queue.Enqueue($dependencyKey)
            }
        }
    }

    return [ordered]@{
        RuntimeTargetName = $runtimeTargetName
        Entries = $entries
        PackageKeys = @($visited | Sort-Object)
    }
}

function Get-AssetDestinationPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $AssetPath,

        [Parameter(Mandatory = $true)]
        [string] $Kind,

        $Metadata
    )

    $normalized = $AssetPath.Replace('\\', '/').TrimStart('/')
    $leaf = [System.IO.Path]::GetFileName($normalized)
    if ([string]::IsNullOrWhiteSpace($leaf) -or $leaf -eq '_._') {
        return ''
    }

    if ($Kind -eq 'resources') {
        $locale = ''
        if ($null -ne $Metadata) {
            $localeProperty = $Metadata.PSObject.Properties['locale']
            if ($null -ne $localeProperty) {
                $locale = [string] $localeProperty.Value
            }
        }
        if ([string]::IsNullOrWhiteSpace($locale)) {
            $segments = $normalized.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
            if ($segments.Length -ge 2) {
                $locale = $segments[$segments.Length - 2]
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($locale)) {
            return "$locale/$leaf"
        }
    }

    return $leaf
}

function Resolve-BuildAsset {
    param(
        [Parameter(Mandatory = $true)]
        [string] $OutputDirectory,

        [Parameter(Mandatory = $true)]
        [string] $DestinationPath,

        [Parameter(Mandatory = $true)]
        [string] $PackageIdentity,

        [Parameter(Mandatory = $true)]
        [string] $AssetPath
    )

    $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw "Build output directory missing while resolving runtime dependency '$PackageIdentity': $outputRoot"
    }

    $normalizedDestination = $DestinationPath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
    $exactDestination = Join-Path $outputRoot $normalizedDestination
    if (Test-Path -LiteralPath $exactDestination -PathType Leaf) {
        return (Resolve-Path -LiteralPath $exactDestination).Path
    }

    $leaf = [System.IO.Path]::GetFileName($normalizedDestination)
    $topLevel = Join-Path $outputRoot $leaf
    if (Test-Path -LiteralPath $topLevel -PathType Leaf) {
        return (Resolve-Path -LiteralPath $topLevel).Path
    }

    $candidates = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter $leaf -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        throw "Runtime asset '$AssetPath' from '$PackageIdentity' was declared by the dependency graph but was not found in build output '$outputRoot'."
    }

    if ($DestinationPath.Contains('/')) {
        $destinationParent = [System.IO.Path]::GetDirectoryName($normalizedDestination)
        $matchingParent = @(
            $candidates | Where-Object {
                [System.IO.Path]::GetRelativePath($outputRoot, $_.FullName).StartsWith(
                    "$destinationParent$([System.IO.Path]::DirectorySeparatorChar)",
                    [StringComparison]::OrdinalIgnoreCase)
            }
        )
        if ($matchingParent.Count -eq 1) {
            return $matchingParent[0].FullName
        }
        if ($matchingParent.Count -gt 1) {
            $candidates = $matchingParent
        }
    }

    if ($candidates.Count -eq 1) {
        return $candidates[0].FullName
    }

    $hashGroups = @($candidates | Group-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash })
    if ($hashGroups.Count -eq 1) {
        return ($candidates | Sort-Object { $_.FullName.Length }, FullName | Select-Object -First 1).FullName
    }

    $paths = ($candidates | ForEach-Object { $_.FullName }) -join ', '
    throw "Runtime asset '$AssetPath' from '$PackageIdentity' matched multiple different build outputs: $paths"
}

$forbiddenRuntimeNames = @(
    'ProtonVPN.Client.exe',
    'ProtonVPNService.exe',
    'ProtonVPN.Launcher.exe',
    'hostfxr.dll',
    'hostpolicy.dll',
    'coreclr.dll'
)

$currentPackages = Get-CentralPackageVersions -Path $CurrentPackagesPath
$baselinePackages = Get-CentralPackageVersions -Path $BaselinePackagesPath
$changedDirectPackages = @(
    foreach ($id in $currentPackages.Keys | Sort-Object) {
        $currentVersion = [string] $currentPackages[$id]
        $baselineVersion = if ($baselinePackages.ContainsKey($id)) { [string] $baselinePackages[$id] } else { '' }
        if (-not $currentVersion.Equals($baselineVersion, [StringComparison]::OrdinalIgnoreCase)) {
            [ordered]@{
                id = $id
                baselineVersion = if ([string]::IsNullOrWhiteSpace($baselineVersion)) { $null } else { $baselineVersion }
                currentVersion = $currentVersion
            }
        }
    }
)

$changedPackageIds = @{}
foreach ($package in $changedDirectPackages) {
    $changedPackageIds[[string] $package.id] = $true
}

$clientOutputDir = [System.IO.Path]::GetFullPath($ClientOutputDirectory)
$serviceOutputDir = [System.IO.Path]::GetFullPath($ServiceOutputDirectory)
$stageDir = Resolve-SafeStageDirectory `
    -Path $StageDirectory `
    -ProtectedPaths @($clientOutputDir, $serviceOutputDir, $CurrentPackagesPath, $BaselinePackagesPath)
$metadataFile = [System.IO.Path]::GetFullPath($MetadataPath)

Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Path $metadataFile -Parent) | Out-Null

$stagedFiles = @{}
$runtimePackages = @{}

function Stage-DependencyGraph {
    param(
        [Parameter(Mandatory = $true)]
        [string] $GraphName,

        [Parameter(Mandatory = $true)]
        [string] $DependenciesPath,

        [Parameter(Mandatory = $true)]
        [string] $OutputDirectory
    )

    $dependencies = Get-DependencyDocument -Path $DependenciesPath
    $closure = Get-RuntimePackageClosure -Dependencies $dependencies -ChangedPackageIds $changedPackageIds

    foreach ($packageKey in $closure.PackageKeys) {
        $entry = $closure.Entries[$packageKey]
        $runtimePackages[$packageKey] = [ordered]@{
            id = $entry.Id
            version = $entry.Version
        }

        foreach ($kind in @('runtime', 'native', 'resources')) {
            $property = $entry.Value.PSObject.Properties[$kind]
            if ($null -eq $property) {
                continue
            }

            foreach ($asset in $property.Value.PSObject.Properties) {
                $destination = Get-AssetDestinationPath -AssetPath $asset.Name -Kind $kind -Metadata $asset.Value
                if ([string]::IsNullOrWhiteSpace($destination)) {
                    continue
                }

                $leaf = [System.IO.Path]::GetFileName($destination)
                if ($forbiddenRuntimeNames -contains $leaf) {
                    throw "Changed runtime dependency closure attempted to stage install-unsafe core/app file '$leaf' from '$packageKey'."
                }
                if ($leaf -like 'ProtonVPN*.dll') {
                    throw "Changed runtime dependency closure unexpectedly contains first-party Proton assembly '$leaf' from package '$packageKey'."
                }
                if ($leaf -like '*Tests*.dll' -or $leaf -like '*Test*.dll') {
                    throw "Changed runtime dependency closure unexpectedly contains test assembly '$leaf' from package '$packageKey'."
                }

                $sourcePath = Resolve-BuildAsset `
                    -OutputDirectory $OutputDirectory `
                    -DestinationPath $destination `
                    -PackageIdentity $packageKey `
                    -AssetPath $asset.Name
                $normalizedDestination = $destination.Replace('\\', '/')
                $targetPath = Join-Path $stageDir ($normalizedDestination.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                $targetParent = Split-Path -Path $targetPath -Parent
                New-Item -ItemType Directory -Force -Path $targetParent | Out-Null

                $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
                if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                    $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    if (-not $sourceHash.Equals($targetHash, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Runtime dependency collision for '$normalizedDestination': '$packageKey' from $GraphName differs from an already staged asset."
                    }
                } else {
                    Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
                }

                if (-not $stagedFiles.ContainsKey($normalizedDestination)) {
                    $stagedFiles[$normalizedDestination] = [ordered]@{
                        path = $normalizedDestination
                        size = (Get-Item -LiteralPath $targetPath).Length
                        sha256 = $sourceHash
                        packages = @()
                        graphs = @()
                    }
                }

                $record = $stagedFiles[$normalizedDestination]
                if ($record.packages -notcontains $packageKey) {
                    $record.packages = @($record.packages) + $packageKey
                }
                if ($record.graphs -notcontains $GraphName) {
                    $record.graphs = @($record.graphs) + $GraphName
                }
            }
        }

        $runtimeTargetsProperty = $entry.Value.PSObject.Properties['runtimeTargets']
        if ($null -ne $runtimeTargetsProperty) {
            foreach ($asset in $runtimeTargetsProperty.Value.PSObject.Properties) {
                $ridProperty = $asset.Value.PSObject.Properties['rid']
                if ($null -ne $ridProperty -and
                    -not [string]::IsNullOrWhiteSpace([string] $ridProperty.Value) -and
                    -not $closure.RuntimeTargetName.EndsWith("/$([string] $ridProperty.Value)", [StringComparison]::OrdinalIgnoreCase)) {
                    continue
                }

                $kindProperty = $asset.Value.PSObject.Properties['assetType']
                $kind = if ($null -ne $kindProperty) { [string] $kindProperty.Value } else { 'runtime' }
                if ($kind -notin @('runtime', 'native', 'resources')) {
                    continue
                }

                $destination = Get-AssetDestinationPath -AssetPath $asset.Name -Kind $kind -Metadata $asset.Value
                if ([string]::IsNullOrWhiteSpace($destination)) {
                    continue
                }

                $leaf = [System.IO.Path]::GetFileName($destination)
                if ($forbiddenRuntimeNames -contains $leaf -or $leaf -like 'ProtonVPN*.dll' -or $leaf -like '*Tests*.dll') {
                    throw "Changed runtime dependency closure attempted to stage forbidden runtimeTarget asset '$leaf' from '$packageKey'."
                }

                $sourcePath = Resolve-BuildAsset `
                    -OutputDirectory $OutputDirectory `
                    -DestinationPath $destination `
                    -PackageIdentity $packageKey `
                    -AssetPath $asset.Name
                $normalizedDestination = $destination.Replace('\\', '/')
                $targetPath = Join-Path $stageDir ($normalizedDestination.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
                $targetParent = Split-Path -Path $targetPath -Parent
                New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
                $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()

                if (Test-Path -LiteralPath $targetPath -PathType Leaf) {
                    $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    if (-not $sourceHash.Equals($targetHash, [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Runtime dependency collision for '$normalizedDestination': '$packageKey' from $GraphName differs from an already staged asset."
                    }
                } else {
                    Copy-Item -LiteralPath $sourcePath -Destination $targetPath -Force
                }

                if (-not $stagedFiles.ContainsKey($normalizedDestination)) {
                    $stagedFiles[$normalizedDestination] = [ordered]@{
                        path = $normalizedDestination
                        size = (Get-Item -LiteralPath $targetPath).Length
                        sha256 = $sourceHash
                        packages = @()
                        graphs = @()
                    }
                }
                $record = $stagedFiles[$normalizedDestination]
                if ($record.packages -notcontains $packageKey) {
                    $record.packages = @($record.packages) + $packageKey
                }
                if ($record.graphs -notcontains $GraphName) {
                    $record.graphs = @($record.graphs) + $GraphName
                }
            }
        }
    }
}

if ($changedPackageIds.Count -gt 0) {
    if ($BuildMode -in @('client', 'both')) {
        Stage-DependencyGraph `
            -GraphName 'client' `
            -DependenciesPath $ClientDependenciesPath `
            -OutputDirectory $clientOutputDir
    }
    if ($BuildMode -in @('service', 'both')) {
        Stage-DependencyGraph `
            -GraphName 'service' `
            -DependenciesPath $ServiceDependenciesPath `
            -OutputDirectory $serviceOutputDir
    }
}

$actualStageFiles = @(Get-ChildItem -LiteralPath $stageDir -Recurse -File | Sort-Object FullName)
if ($actualStageFiles.Count -ne $stagedFiles.Count) {
    throw "Runtime dependency staging metadata mismatch: tracked $($stagedFiles.Count) files but staged $($actualStageFiles.Count)."
}

$metadata = [ordered]@{
    schemaVersion = 1
    buildMode = $BuildMode
    upstreamBaseCommit = $UpstreamBaseCommit
    changedDirectPackages = $changedDirectPackages
    runtimePackages = @($runtimePackages.Values | Sort-Object id, version)
    files = @($stagedFiles.Values | Sort-Object path)
}

$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataFile -Encoding utf8

Write-Host "Changed direct package roots: $($changedDirectPackages.Count)"
Write-Host "Resolved runtime dependency closure packages: $($runtimePackages.Count)"
Write-Host "Staged runtime dependency files: $($stagedFiles.Count)"
foreach ($package in $changedDirectPackages) {
    $baseline = if ($null -eq $package.baselineVersion) { '<not present>' } else { [string] $package.baselineVersion }
    Write-Host "  $($package.id): $baseline -> $($package.currentVersion)"
}
