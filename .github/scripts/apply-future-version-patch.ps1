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

function Assert-StagedDiffIsSafe {
    $checkOutput = @(& git diff --cached --check 2>&1)
    $checkExitCode = $LASTEXITCODE

    if ($checkExitCode -eq 0) {
        return
    }

    if ($checkOutput.Count -gt 0) {
        $checkOutput | Out-Host
    }

    $hasConflictMarkers = @(
        $checkOutput | Where-Object { "$_" -match 'leftover conflict marker' }
    ).Count -gt 0

    if ($hasConflictMarkers) {
        throw 'Staged merge contains leftover conflict markers.'
    }

    if ($checkExitCode -eq 2) {
        Write-Warning 'Staged merge contains whitespace diagnostics; continuing because no conflict markers remain.'
        return
    }

    throw "git diff --cached --check failed with exit code $checkExitCode"
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

    Assert-StagedDiffIsSafe
    Invoke-Git commit -m $Message | Out-Host
    return Get-GitOutput rev-parse HEAD
}

function Resolve-MergeConflictsToTarget {
    $conflictedPaths = @(& git diff --name-only --diff-filter=U)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate unresolved merge conflicts.'
    }

    if ($conflictedPaths.Count -eq 0) {
        return
    }

    Write-Host 'Resolving remaining structural conflicts in favor of the target release:'
    foreach ($path in $conflictedPaths) {
        Write-Host "  $path"

        $stageEntries = @(& git ls-files --unmerged -- $path)
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to inspect merge stages for '$path'."
        }

        $hasTargetVersion = @(
            $stageEntries | Where-Object { $_ -match '^[0-9]+ [0-9a-f]+ 2\t' }
        ).Count -gt 0

        if ($hasTargetVersion) {
            & git checkout --ours -- $path | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to restore target-release version of '$path'."
            }
            Invoke-Git add -- $path
        }
        else {
            & git rm -f --ignore-unmatch -- $path | Out-Host
            if ($LASTEXITCODE -ne 0) {
                throw "Unable to preserve target-release deletion of '$path'."
            }
        }
    }

    $remainingConflicts = @(& git diff --name-only --diff-filter=U)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to verify merge conflict resolution.'
    }
    if ($remainingConflicts.Count -gt 0) {
        throw "Unresolved merge conflicts remain:`n$($remainingConflicts -join "`n")"
    }
}

function Test-UpstreamBackportSubject {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Subject
    )

    if ($Subject -notmatch '^(Port|Backport)\s+') {
        return $false
    }

    return $Subject -notmatch '^Port complete (fork|maintained fork)\b'
}

function New-CleanForkSourceRef {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef,

        [Parameter(Mandatory = $true)]
        [string] $TargetBranch
    )

    $logLines = @(& git log --format='%H%x09%s' "$SourceBase..$SourceRef")
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate fork commits for upstream-backport cleanup.'
    }

    $backports = [System.Collections.Generic.List[object]]::new()
    foreach ($line in $logLines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $parts = $line -split "`t", 2
        if ($parts.Count -ne 2 -or -not (Test-UpstreamBackportSubject -Subject $parts[1])) {
            continue
        }

        $parentLine = Get-GitOutput rev-list --parents -n 1 $parts[0]
        $parentParts = @($parentLine -split '\s+')
        if ($parentParts.Count -ne 2) {
            throw "Upstream backport '$($parts[0])' is not a single-parent commit: $($parts[1])"
        }

        $backports.Add([pscustomobject]@{
            Sha = $parts[0]
            Subject = $parts[1]
        })
    }

    if ($backports.Count -eq 0) {
        Write-Host 'No explicit upstream backport commits found in the fork delta.'
        return $SourceRef
    }

    Write-Host 'Preparing fork source with explicit upstream backports removed:'
    foreach ($backport in $backports) {
        Write-Host "  $($backport.Sha.Substring(0, 12))  $($backport.Subject)"
    }

    $cleanSourceRef = ''
    try {
        Invoke-Git switch --detach $SourceRef

        foreach ($backport in $backports) {
            $patchFile = Join-Path ([System.IO.Path]::GetTempPath()) "future-port-backport-$($backport.Sha)-$([System.Guid]::NewGuid()).patch"
            try {
                & git diff --binary --full-index --unified=0 "$($backport.Sha)^" $backport.Sha --output=$patchFile -- .
                if ($LASTEXITCODE -ne 0) {
                    throw "Unable to generate inverse patch for upstream backport $($backport.Sha)."
                }

                if (-not (Test-Path -LiteralPath $patchFile -PathType Leaf) -or
                    (Get-Item -LiteralPath $patchFile).Length -eq 0) {
                    Write-Host "  no tree delta: $($backport.Sha.Substring(0, 12))"
                    continue
                }

                $applyOutput = @(
                    & git apply --reverse --index --unidiff-zero --whitespace=nowarn $patchFile 2>&1
                )
                $applyExitCode = $LASTEXITCODE
                if ($applyOutput.Count -gt 0) {
                    $applyOutput | Out-Host
                }

                if ($applyExitCode -ne 0) {
                    Write-Host "Unable to remove upstream backport $($backport.Sha): $($backport.Subject)"
                    & git status --short
                    throw "git apply --reverse failed with exit code $applyExitCode"
                }
            }
            finally {
                Remove-Item -LiteralPath $patchFile -Force -ErrorAction SilentlyContinue
            }
        }

        $unmergedPaths = @(& git diff --name-only --diff-filter=U)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to inspect cleaned fork source for unresolved conflicts.'
        }
        if ($unmergedPaths.Count -gt 0) {
            throw "Cleaned fork source still contains unresolved conflicts:`n$($unmergedPaths -join "`n")"
        }

        $unstagedPaths = @(& git diff --name-only)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to inspect cleaned fork source for unstaged changes.'
        }
        if ($unstagedPaths.Count -gt 0) {
            throw "Cleaned fork source contains unstaged changes:`n$($unstagedPaths -join "`n")"
        }

        Assert-StagedDiffIsSafe
        $cleanTree = Get-GitOutput write-tree
        $cleanSourceRef = Get-GitOutput commit-tree $cleanTree -p $SourceBase -m 'Synthetic fork source without explicit upstream backports'
        Write-Host "Cleaned fork source commit: $cleanSourceRef"
    }
    finally {
        & git reset --hard $SourceRef | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to reset temporary cleaned fork source.'
        }

        & git switch $TargetBranch | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to return to target branch '$TargetBranch'."
        }
    }

    return $cleanSourceRef
}

function Get-WhitespaceEquivalentPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef,

        [Parameter(Mandatory = $true)]
        [string] $TargetRef
    )

    $sourceChanged = @(& git diff --name-only $SourceBase $SourceRef --)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate source-fork changes.'
    }

    $targetChanged = @(& git diff --name-only $SourceBase $TargetRef --)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate target-release changes.'
    }

    $targetChangedSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $targetChanged) {
        [void]$targetChangedSet.Add($path)
    }

    $equivalentPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($path in $sourceChanged) {
        if (-not $targetChangedSet.Contains($path)) {
            continue
        }

        & git cat-file -e "${SourceRef}:$path" 2>$null
        $sourceExists = $LASTEXITCODE -eq 0
        & git cat-file -e "${TargetRef}:$path" 2>$null
        $targetExists = $LASTEXITCODE -eq 0

        if (-not $sourceExists -or -not $targetExists) {
            continue
        }

        & git diff --quiet $TargetRef $SourceRef -- $path
        $exactDiffExitCode = $LASTEXITCODE
        if ($exactDiffExitCode -eq 0) {
            $equivalentPaths.Add($path)
            continue
        }
        if ($exactDiffExitCode -ne 1) {
            throw "Unable to compare source and target versions of '$path'."
        }

        $extension = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
        $whitespaceInsensitiveExtensions = @(
            '.cs', '.csproj', '.props', '.targets', '.xaml', '.xml', '.resw', '.json'
        )
        if ($extension -notin $whitespaceInsensitiveExtensions) {
            continue
        }

        & git diff --quiet --ignore-all-space --ignore-blank-lines $TargetRef $SourceRef -- $path
        $whitespaceDiffExitCode = $LASTEXITCODE
        if ($whitespaceDiffExitCode -eq 0) {
            $equivalentPaths.Add($path)
        }
        elseif ($whitespaceDiffExitCode -ne 1) {
            throw "Unable to compare whitespace-normalized versions of '$path'."
        }
    }

    return @($equivalentPaths)
}

function Restore-WhitespaceEquivalentTargetPaths {
    param(
        [Parameter(Mandatory = $true)]
        [string] $TargetRef,

        [Parameter(Mandatory = $true)]
        [string[]] $Paths
    )

    if ($Paths.Count -eq 0) {
        return
    }

    Write-Host 'Keeping target-release copies for fork backports already present upstream:'
    foreach ($path in $Paths) {
        Write-Host "  $path"
        & git checkout $TargetRef -- $path | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restore target-release copy of '$path'."
        }
        Invoke-Git add -- $path
    }
}

function Merge-ForkTree {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef,

        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    Write-Host "Merging the complete fork tree from $SourceBase..$SourceRef"
    Write-Host 'Overlapping changes prefer the target release; non-conflicting fork changes are retained.'

    $beforeMerge = Get-GitOutput rev-parse HEAD
    $equivalentTargetPaths = @(
        Get-WhitespaceEquivalentPaths -SourceBase $SourceBase -SourceRef $SourceRef -TargetRef $beforeMerge
    )

    $mergeOutput = @(
        & git -c merge.renames=true merge --no-ff --no-commit --no-edit -X ours -m $Message $SourceRef 2>&1
    )
    $mergeExitCode = $LASTEXITCODE
    $mergeOutput | Out-Host

    if ($mergeExitCode -ne 0) {
        $conflictedPaths = @(& git diff --name-only --diff-filter=U)
        if ($LASTEXITCODE -ne 0) {
            & git merge --abort 2>$null
            throw 'Unable to inspect merge conflicts.'
        }

        if ($conflictedPaths.Count -eq 0) {
            Write-Host 'Merge failed without producing resolvable file conflicts. Current status:'
            & git status --short
            & git merge --abort 2>$null
            throw "git merge failed with exit code $mergeExitCode"
        }

        try {
            Resolve-MergeConflictsToTarget
        }
        catch {
            Write-Host 'Target-preferred merge resolution failed. Current status:'
            & git status --short
            & git merge --abort 2>$null
            throw
        }
    }

    $mergeHead = & git rev-parse -q --verify MERGE_HEAD 2>$null
    $mergeInProgress = $LASTEXITCODE -eq 0
    if ($mergeExitCode -eq 0 -and -not $mergeInProgress) {
        $afterMerge = Get-GitOutput rev-parse HEAD
        if ($afterMerge -eq $beforeMerge) {
            Write-Host 'The source branch contains no fork changes to port.'
            return ''
        }
        return $afterMerge
    }

    try {
        Restore-WhitespaceEquivalentTargetPaths -TargetRef $beforeMerge -Paths $equivalentTargetPaths
        Assert-StagedDiffIsSafe
        Invoke-Git commit --no-edit | Out-Host
    }
    catch {
        Write-Host 'Finalizing the target-preferred fork merge failed. Current status:'
        & git status --short
        & git merge --abort 2>$null
        throw
    }

    $afterMerge = Get-GitOutput rev-parse HEAD
    if ($afterMerge -eq $beforeMerge) {
        Write-Host 'The source branch contains no fork changes to port.'
        return ''
    }

    return $afterMerge
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
$cleanSourceRef = New-CleanForkSourceRef `
    -SourceBase $sourceBase `
    -SourceRef $sourceRef `
    -TargetBranch $targetBranch

$forkPatchCommit = Merge-ForkTree `
    -SourceBase $sourceBase `
    -SourceRef $cleanSourceRef `
    -Message "Port complete fork from $sourcePatchBranch onto $baseBranch"

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
Write-GitHubOutput -Name 'clean_source_ref' -Value $cleanSourceRef
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
