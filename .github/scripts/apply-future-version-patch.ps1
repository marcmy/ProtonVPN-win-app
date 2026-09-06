[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $BaseBranch,

    [string] $BaseRepositoryUrl = '',

    [string] $BaseRef = '',

    [Parameter(Mandatory = $true)]
    [string] $SourcePatchBranch,

    [Parameter(Mandatory = $true)]
    [string] $TargetBranch,

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

function Apply-ForkPatch {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef
    )

    Write-Host "Preparing the complete fork patch from $SourceBase..$SourceRef"

    $patchFile = Join-Path ([System.IO.Path]::GetTempPath()) "complete-fork-$([System.Guid]::NewGuid()).patch"

    try {
        Invoke-Git diff --binary --full-index "--output=$patchFile" $SourceBase $SourceRef -- .
        if (-not (Test-Path -LiteralPath $patchFile -PathType Leaf) -or
            (Get-Item -LiteralPath $patchFile).Length -eq 0) {
            Write-Host 'The source branch contains no fork changes to port.'
            return
        }

        Invoke-Git apply --3way --index --whitespace=nowarn $patchFile
    }
    catch {
        Write-Host 'The complete fork patch could not be applied cleanly. Current status:'
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
$baseRepositoryUrl = $BaseRepositoryUrl.Trim()
$baseRef = $BaseRef.Trim()

if ([string]::IsNullOrWhiteSpace($baseRepositoryUrl) -xor [string]::IsNullOrWhiteSpace($baseRef)) {
    throw 'base_repository_url and base_ref must be provided together.'
}

$usesExternalBase = -not [string]::IsNullOrWhiteSpace($baseRepositoryUrl)
if ($usesExternalBase) {
    & git check-ref-format $baseRef
    if ($LASTEXITCODE -ne 0) {
        throw "Invalid base ref: $baseRef"
    }
}

$protectedTargets = @(
    'main',
    'master',
    'marc/proton'
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

if ($usesExternalBase) {
    $baseTrackingRef = 'refs/remotes/codex-base/source'
    Write-Host "Fetching external base $baseRepositoryUrl@$baseRef"
    Invoke-Git fetch --no-tags $baseRepositoryUrl "+${baseRef}:${baseTrackingRef}"
}
else {
    $baseTrackingRef = "refs/remotes/origin/$baseBranch"
    Write-Host "Fetching base branch origin/$baseBranch"
    Invoke-Git fetch --no-tags origin "+refs/heads/${baseBranch}:${baseTrackingRef}"
}

Write-Host "Fetching source patch branch origin/$sourcePatchBranch"
Invoke-Git fetch --no-tags origin "+refs/heads/${sourcePatchBranch}:refs/remotes/origin/${sourcePatchBranch}"
$baseCommit = Get-GitOutput rev-parse "${baseTrackingRef}^{commit}"

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
    Write-Host "Creating/resetting $targetBranch from $baseTrackingRef ($baseCommit)"
    Invoke-Git switch -C $targetBranch $baseCommit
}
else {
    Write-Host "Creating $targetBranch from $baseTrackingRef ($baseCommit)"
    Invoke-Git switch -c $targetBranch $baseCommit
}

$sourceBase = Get-GitOutput merge-base $baseCommit "origin/$sourcePatchBranch"
$sourceRef = "origin/$sourcePatchBranch"

Apply-ForkPatch -SourceBase $sourceBase -SourceRef $sourceRef
$forkPatchCommit = Commit-StagedChanges "Port complete fork from $sourcePatchBranch onto $baseBranch"

Invoke-Git diff --check

$unstagedChanges = @(& git diff --name-only)
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
Write-GitHubOutput -Name 'base_commit' -Value $baseCommit
Write-GitHubOutput -Name 'base_ref' -Value $(if ($usesExternalBase) { $baseRef } else { "refs/heads/$baseBranch" })
Write-GitHubOutput -Name 'source_patch_branch' -Value $sourcePatchBranch
Write-GitHubOutput -Name 'source_base' -Value $sourceBase
Write-GitHubOutput -Name 'target_branch' -Value $targetBranch
Write-GitHubOutput -Name 'target_sha' -Value $targetSha
Write-GitHubOutput -Name 'target_slug' -Value $targetSlug
Write-GitHubOutput -Name 'target_exists' -Value ($targetExists.ToString().ToLowerInvariant())
Write-GitHubOutput -Name 'fork_patch_commit' -Value $forkPatchCommit

Write-Host "Patched branch: $targetBranch"
Write-Host "Target SHA: $targetSha"
if ($forkPatchCommit) {
    Write-Host "Complete fork patch commit: $forkPatchCommit"
}
