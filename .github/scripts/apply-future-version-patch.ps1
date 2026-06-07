[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BaseBranch,

    [Parameter(Mandatory = $true)]
    [string] $SourcePatchBranch,

    [Parameter(Mandatory = $true)]
    [string] $TargetBranch,

    [switch] $IncludeUpdaterSkip,

    [switch] $ForceResetTarget
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Normalize-BranchName {
    param(
        [Parameter(Mandatory = $true)]
        [string] $BranchName
    )

    $branch = $BranchName.Trim()
    $branch = $branch -replace '^refs/heads/', ''
    $branch = $branch -replace '^origin/', ''

    if ([string]::IsNullOrWhiteSpace($branch)) {
        throw 'Branch names cannot be empty.'
    }

    if ($branch.StartsWith('-') -or
        $branch.EndsWith('.') -or
        $branch.Contains('..') -or
        $branch.Contains('@{') -or
        $branch -match '[\s~^:?*\[\]\\]') {
        throw "Unsafe or invalid branch name: '$BranchName'"
    }

    return $branch
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Get-GitOutput {
    param(
        [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
        [string[]] $Arguments
    )

    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }

    return ($output -join "`n").Trim()
}

function Test-RemoteBranch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Branch
    )

    & git ls-remote --exit-code --heads origin $Branch | Out-Null
    if ($LASTEXITCODE -eq 0) {
        return $true
    }
    if ($LASTEXITCODE -eq 2) {
        return $false
    }

    throw "Unable to check whether origin/$Branch exists."
}

function Test-LocalBranch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Branch
    )

    & git show-ref --verify --quiet "refs/heads/$Branch"
    if ($LASTEXITCODE -eq 0) {
        return $true
    }
    if ($LASTEXITCODE -eq 1) {
        return $false
    }

    throw "Unable to check whether local branch $Branch exists."
}

function Write-GitHubOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $Value
    )

    if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
        Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$Name=$Value"
    }
}

function Test-StagedChanges {
    & git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        return $false
    }
    if ($LASTEXITCODE -eq 1) {
        return $true
    }

    throw 'Unable to inspect staged changes.'
}

function Commit-StagedChanges {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    if (-not (Test-StagedChanges)) {
        Write-Host "No staged changes for '$Message'."
        return ''
    }

    Invoke-Git diff --cached --check
    Invoke-Git commit -m $Message | Out-Host
    return Get-GitOutput rev-parse HEAD
}

function Apply-PathPatch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name,

        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef,

        [Parameter(Mandatory = $true)]
        [string[]] $Paths
    )

    Write-Host "Preparing $Name patch from $SourceBase..$SourceRef"

    $patchFile = Join-Path ([System.IO.Path]::GetTempPath()) "$($Name -replace '[^A-Za-z0-9_.-]', '-')-$([System.Guid]::NewGuid()).patch"
    $diffOutput = & git diff --binary $SourceBase $SourceRef -- @Paths
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create $Name patch."
    }

    if ([string]::IsNullOrWhiteSpace(($diffOutput -join "`n"))) {
        Write-Host "No $Name hunks were found in the requested source branch."
        return
    }

    $diffOutput | Set-Content -LiteralPath $patchFile -Encoding utf8

    try {
        Invoke-Git apply --3way --index --whitespace=nowarn $patchFile
    }
    catch {
        Write-Host "$Name patch could not be applied cleanly. Current status:"
        & git status --short
        throw
    }
    finally {
        Remove-Item -LiteralPath $patchFile -Force -ErrorAction SilentlyContinue
    }
}

$baseBranch = Normalize-BranchName $BaseBranch
$sourcePatchBranch = Normalize-BranchName $SourcePatchBranch
$targetBranch = Normalize-BranchName $TargetBranch

$protectedTargets = @(
    'main',
    'master',
    'marc/v4.4.1-split-tunnel-patterns'
)

if ($protectedTargets -contains $targetBranch) {
    throw "Refusing to modify protected target branch '$targetBranch'."
}

if ($targetBranch -eq $baseBranch) {
    throw 'target_branch must be different from base_branch.'
}

if ($targetBranch -eq $sourcePatchBranch) {
    throw 'target_branch must be different from source_patch_branch.'
}

$dirtyStatus = @(Get-GitOutput status --porcelain)
if ($dirtyStatus.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace(($dirtyStatus -join "`n"))) {
    throw "Working tree must be clean before patching.`n$($dirtyStatus -join "`n")"
}

if ($env:GITHUB_ACTIONS -eq 'true') {
    Invoke-Git config user.name 'github-actions[bot]'
    Invoke-Git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
}

Write-Host "Fetching base branch origin/$baseBranch"
Invoke-Git fetch --no-tags origin "+refs/heads/${baseBranch}:refs/remotes/origin/${baseBranch}"

Write-Host "Fetching source patch branch origin/$sourcePatchBranch"
Invoke-Git fetch --no-tags origin "+refs/heads/${sourcePatchBranch}:refs/remotes/origin/${sourcePatchBranch}"

$targetExists = Test-RemoteBranch $targetBranch
$localTargetExists = Test-LocalBranch $targetBranch

if ($targetExists) {
    Write-Host "Remote target branch origin/$targetBranch exists."
    if (-not $ForceResetTarget) {
        throw "Target branch '$targetBranch' already exists. Set force_reset_target=true to reset it from '$baseBranch'."
    }

    Invoke-Git fetch --no-tags origin "+refs/heads/${targetBranch}:refs/remotes/origin/${targetBranch}"
}
elseif ($localTargetExists -and -not $ForceResetTarget) {
    throw "Local target branch '$targetBranch' already exists. Choose a new target branch or set force_reset_target=true."
}

if ($ForceResetTarget) {
    Write-Host "Creating/resetting $targetBranch from origin/$baseBranch"
    Invoke-Git switch -C $targetBranch "origin/$baseBranch"
}
else {
    Write-Host "Creating $targetBranch from origin/$baseBranch"
    Invoke-Git switch -c $targetBranch "origin/$baseBranch"
}

$sourceBase = Get-GitOutput merge-base "origin/$baseBranch" "origin/$sourcePatchBranch"
$sourceRef = "origin/$sourcePatchBranch"

$splitTunnelPaths = @(
    'src/Client/ProtonVPN.Client.Core/Models/ExternalApp.cs',
    'src/Client/ProtonVPN.Client.Core/Models/TunnelingApp.cs',
    'src/Client/ProtonVPN.Client/UI/Main/Settings/Pages/Connection/SplitTunnelingPageViewModel.cs',
    'src/Client/ProtonVPN.Client/UI/Overlays/Selection/AppSelectorOverlayView.xaml',
    'src/Client/ProtonVPN.Client/UI/Overlays/Selection/AppSelectorOverlayView.xaml.cs',
    'src/Client/ProtonVPN.Client/UI/Overlays/Selection/AppSelectorOverlayViewModel.cs',
    'src/Client/ProtonVPN.Client/Handlers/ServiceSettingChangeHandler.cs',
    'src/Client/Settings/ProtonVPN.Client.Settings/RequiredReconnections/RequiredReconnectionSettings.cs',
    'src/ProtonVPN.Service/SplitTunneling/SplitTunnel.cs',
    'src/Tests/ProtonVPN.Integration.Tests/Handlers/ServiceSettingChangeHandlerTest.cs',
    'src/Tests/ProtonVPN.Integration.Tests/Settings/RequiredReconnectionSettingsTest.cs',
    'src/Tests/ProtonVPN.Service.Tests/SplitTunneling/SplitTunnelTest.cs'
)

$updaterSkipPaths = @(
    'src/Client/Settings/ProtonVPN.Client.Settings.Contracts/IGlobalSettings.cs',
    'src/Client/Settings/ProtonVPN.Client.Settings/GlobalSettings.cs',
    'src/Client/Logic/Updates/ProtonVPN.Client.Logic.Updates.Contracts/IUpdatesManager.cs',
    'src/Client/Logic/Updates/ProtonVPN.Client.Logic.Updates/UpdatesManager.cs',
    'src/Client/ProtonVPN.Client/UI/Update/UpdateComponent.xaml',
    'src/Client/ProtonVPN.Client/UI/Update/UpdateComponent.xaml.cs',
    'src/Client/ProtonVPN.Client/UI/Update/UpdateViewModel.cs',
    'src/Tests/ProtonVPN.Integration.Tests/Updates/UpdatesManagerSkipTest.cs'
)

Apply-PathPatch -Name 'split-tunnel' -SourceBase $sourceBase -SourceRef $sourceRef -Paths $splitTunnelPaths
$splitTunnelCommit = Commit-StagedChanges "Apply split tunnel patch from $sourcePatchBranch"

if ($IncludeUpdaterSkip) {
    Apply-PathPatch -Name 'updater-skip' -SourceBase $sourceBase -SourceRef $sourceRef -Paths $updaterSkipPaths
    $updaterSkipCommit = Commit-StagedChanges "Apply updater skip patch from $sourcePatchBranch"
}
else {
    $updaterSkipCommit = ''
    Write-Host 'Updater-skip hunks were not requested.'
}

Invoke-Git diff --check

$unstagedChanges = & git diff --name-only
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect unstaged changes.'
}

if ($unstagedChanges.Count -gt 0) {
    throw "Unstaged changes remain after patching:`n$($unstagedChanges -join "`n")"
}

$targetSha = Get-GitOutput rev-parse HEAD
$targetSlug = ($targetBranch -replace '[^A-Za-z0-9_.-]+', '-').Trim('-')
if ([string]::IsNullOrWhiteSpace($targetSlug)) {
    $targetSlug = 'target'
}

Write-GitHubOutput -Name 'base_branch' -Value $baseBranch
Write-GitHubOutput -Name 'source_patch_branch' -Value $sourcePatchBranch
Write-GitHubOutput -Name 'source_base' -Value $sourceBase
Write-GitHubOutput -Name 'target_branch' -Value $targetBranch
Write-GitHubOutput -Name 'target_sha' -Value $targetSha
Write-GitHubOutput -Name 'target_slug' -Value $targetSlug
Write-GitHubOutput -Name 'target_exists' -Value ($targetExists.ToString().ToLowerInvariant())
Write-GitHubOutput -Name 'split_tunnel_commit' -Value $splitTunnelCommit
Write-GitHubOutput -Name 'updater_skip_commit' -Value $updaterSkipCommit

Write-Host "Patched branch: $targetBranch"
Write-Host "Target SHA: $targetSha"
if ($splitTunnelCommit) {
    Write-Host "Split tunnel commit: $splitTunnelCommit"
}
if ($updaterSkipCommit) {
    Write-Host "Updater skip commit: $updaterSkipCommit"
}
