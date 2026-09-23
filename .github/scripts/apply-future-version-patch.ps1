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

function Get-TargetReleaseVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string] $BaseBranch,

        [string] $BaseRef = ''
    )

    foreach ($candidate in @($BaseRef, $BaseBranch)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            $candidate -match '(?<!\d)v?(?<version>\d+\.\d+\.\d+)(?!\d)') {
            return [Version]$Matches['version']
        }
    }

    return $null
}

function Get-UpstreamBackportCommits {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef,

        [AllowNull()]
        [Version] $TargetVersion
    )

    # These historical "Port ..." commits were manual backports from Proton
    # releases through 5.1.8. Once the official target is 5.1.8 or newer,
    # replaying them is both unnecessary and unsafe because the upstream
    # implementation may have evolved structurally.
    if ($null -eq $TargetVersion -or $TargetVersion -lt [Version]'5.1.8') {
        return @()
    }

    $defaultCutoff = '183630b0029c256b23913a91d86710c29af0fc92'
    $cutoff = if ([string]::IsNullOrWhiteSpace($env:FUTURE_PORT_BACKPORT_CUTOFF)) {
        $defaultCutoff
    }
    else {
        $env:FUTURE_PORT_BACKPORT_CUTOFF.Trim()
    }

    & git cat-file -e "${cutoff}^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        if (-not [string]::IsNullOrWhiteSpace($env:FUTURE_PORT_BACKPORT_CUTOFF)) {
            throw "Configured upstream-backport cutoff '$cutoff' is not available."
        }

        Write-Host 'Known upstream-backport cutoff is not present in this source history; skipping backport cleanup.'
        return @()
    }

    & git merge-base --is-ancestor $cutoff $SourceRef
    if ($LASTEXITCODE -eq 1) {
        Write-Host 'Known upstream-backport cutoff is not an ancestor of the source branch; skipping backport cleanup.'
        return @()
    }
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to verify upstream-backport cutoff ancestry.'
    }

    $records = @(& git log --no-merges --format='%H%x09%s' "$SourceBase..$cutoff")
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate known upstream-backport commits.'
    }

    $commits = [System.Collections.Generic.List[string]]::new()
    foreach ($record in $records) {
        $parts = "$record" -split "`t", 2
        if ($parts.Count -ne 2) {
            continue
        }

        if ($parts[1] -match '^(Port|Backport)\b') {
            $commits.Add($parts[0])
        }
    }

    return @($commits)
}

function Get-DeferredForkCommits {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef,

        [Parameter(Mandatory = $true)]
        [string[]] $BackportCommits
    )

    # Reverting these descendants first lets the backport patches be removed
    # from their original tree. They are replayed later, when the target release
    # supplies the upstream code those fork-specific changes were based on.
    $backportSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($commit in $BackportCommits) {
        [void]$backportSet.Add($commit)
    }

    $candidateCommits = @(& git rev-list --reverse --topo-order --no-merges "$SourceBase..$SourceRef")
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to enumerate fork commits for backport cleanup.'
    }

    $candidatePaths = @{}
    foreach ($candidate in $candidateCommits) {
        $paths = @(& git diff-tree --no-commit-id --name-only --no-renames -r $candidate)
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to enumerate paths changed by fork commit $candidate."
        }

        $pathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $paths) {
            if (-not [string]::IsNullOrWhiteSpace($path)) {
                [void]$pathSet.Add("$path")
            }
        }
        $candidatePaths[$candidate] = $pathSet
    }

    $deferredSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($backport in $BackportCommits) {
        $backportPaths = @(& git diff-tree --no-commit-id --name-only --no-renames -r $backport)
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to enumerate paths changed by upstream backport $backport."
        }

        $backportPathSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($path in $backportPaths) {
            if (-not [string]::IsNullOrWhiteSpace($path)) {
                [void]$backportPathSet.Add("$path")
            }
        }
        if ($backportPathSet.Count -eq 0) {
            continue
        }

        $descendants = @(& git rev-list --no-merges "$backport..$SourceRef")
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to enumerate commits after upstream backport $backport."
        }

        foreach ($candidate in $descendants) {
            if ($backportSet.Contains($candidate) -or -not $candidatePaths.ContainsKey($candidate)) {
                continue
            }

            $changedPaths = $candidatePaths[$candidate]
            foreach ($path in $backportPathSet) {
                if ($changedPaths.Contains($path)) {
                    [void]$deferredSet.Add($candidate)
                    break
                }
            }
        }
    }

    return @($candidateCommits | Where-Object { $deferredSet.Contains("$_") })
}

function New-CleanForkSource {
    param(
        [Parameter(Mandatory = $true)]
        [string] $SourceBase,

        [Parameter(Mandatory = $true)]
        [string] $SourceRef,

        [Parameter(Mandatory = $true)]
        [string] $TargetBranch,

        [AllowNull()]
        [Version] $TargetVersion
    )

    $backportCommits = @(
        Get-UpstreamBackportCommits -SourceBase $SourceBase -SourceRef $SourceRef -TargetVersion $TargetVersion
    )

    if ($backportCommits.Count -eq 0) {
        return [pscustomobject]@{
            Ref = $SourceRef
            TemporaryBranch = ''
            DeferredCommits = @()
        }
    }

    $deferredCommits = @(
        Get-DeferredForkCommits -SourceBase $SourceBase -SourceRef $SourceRef -BackportCommits $backportCommits
    )

    $temporaryBranch = '__future_port_clean_source'
    Write-Host "Preparing a fork source snapshot without $($backportCommits.Count) upstream backport commit(s)."
    if ($deferredCommits.Count -gt 0) {
        Write-Host "Temporarily deferring $($deferredCommits.Count) later fork commit(s) that modify backported files until after the target merge."
    }
    Invoke-Git switch -C $temporaryBranch $SourceRef | Out-Host

    try {
        for ($index = $deferredCommits.Count - 1; $index -ge 0; $index--) {
            $commit = $deferredCommits[$index]
            $subject = Get-GitOutput show -s --format=%s $commit
            Write-Host "  Temporarily deferring later fork change: $($commit.Substring(0, 12)) $subject"
            Invoke-Git revert --no-commit --no-edit $commit | Out-Host
        }

        foreach ($commit in $backportCommits) {
            $subject = Get-GitOutput show -s --format=%s $commit
            $parent = Get-GitOutput rev-parse "$commit^"
            $patchPath = Join-Path ([System.IO.Path]::GetTempPath()) ("future-port-backport-{0}.patch" -f $commit.Substring(0, 12))
            Write-Host "  Removing upstream backport patch: $($commit.Substring(0, 12)) $subject"

            try {
                & git diff --binary --full-index --find-renames --find-copies --unified=0 $parent $commit "--output=$patchPath"
                if ($LASTEXITCODE -ne 0) {
                    throw "Unable to build reverse patch for upstream backport $commit ('$subject')."
                }

                if ((Get-Item -LiteralPath $patchPath).Length -eq 0) {
                    continue
                }

                # Apply to the temporary worktree and stage the complete cleaned
                # snapshot once all reversals finish. --index can reject a valid
                # reverse patch when earlier deferred reverts already changed
                # the index for the same tree.
                $applyOutput = @(& git apply --reverse --unidiff-zero --whitespace=nowarn $patchPath 2>&1)
                $applyExitCode = $LASTEXITCODE
                $applyOutput | Out-Host
                if ($applyExitCode -ne 0) {
                    $failureDetails = @($applyOutput | ForEach-Object { "$($_)".Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                    $failureSuffix = if ($failureDetails.Count -gt 0) { "`n$($failureDetails -join "`n")" } else { '' }
                    throw "Unable to subtract upstream backport $commit ('$subject') from the current fork tree without touching later edits.$failureSuffix"
                }
            }
            finally {
                Remove-Item -LiteralPath $patchPath -Force -ErrorAction SilentlyContinue
            }
        }

        Invoke-Git add '--all' | Out-Host
        if (Test-StagedChanges) {
            Assert-StagedDiffIsSafe
            Invoke-Git commit -m 'Prepare fork source without upstream backports already in target release' | Out-Host
        }

        $cleanSourceRef = Get-GitOutput rev-parse HEAD
    }
    catch {
        & git revert --abort 2>$null | Out-Host
        & git reset --hard $SourceRef | Out-Host
        Invoke-Git switch $TargetBranch | Out-Host
        & git branch -D $temporaryBranch 2>$null | Out-Host
        throw
    }

    Invoke-Git switch $TargetBranch | Out-Host

    return [pscustomobject]@{
        Ref = $cleanSourceRef
        TemporaryBranch = $temporaryBranch
        DeferredCommits = $deferredCommits
    }
}

function Apply-DeferredForkCommits {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Commits
    )

    foreach ($commit in $Commits) {
        $subject = Get-GitOutput show -s --format=%s $commit
        Write-Host "Replaying deferred fork-specific change after the target merge: $($commit.Substring(0, 12)) $subject"

        & git cherry-pick --empty=drop --no-edit $commit 2>&1 | Out-Host
        if ($LASTEXITCODE -eq 0) {
            continue
        }

        $conflictedPaths = @(& git diff --name-only --diff-filter=U)
        $conflictInspectionExitCode = $LASTEXITCODE
        & git cherry-pick --abort 2>$null | Out-Host

        if ($conflictInspectionExitCode -ne 0) {
            throw "Unable to inspect conflicts while replaying deferred fork commit $commit ('$subject')."
        }

        if ($conflictedPaths.Count -gt 0) {
            throw "Deferred fork commit $commit ('$subject') conflicts with the target release in:`n$($conflictedPaths -join "`n")"
        }

        throw "Unable to replay deferred fork commit $commit ('$subject')."
    }
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
        [string] $Message,

        [bool] $PreferTargetContent = $true
    )

    Write-Host "Merging the complete fork tree from $SourceBase..$SourceRef"
    if ($PreferTargetContent) {
        Write-Host 'Overlapping changes prefer the target release; non-conflicting fork changes are retained.'
    }
    else {
        Write-Host 'Known upstream backports were removed; merging remaining fork changes normally and resolving only actual conflicts to the target release.'
    }

    $beforeMerge = Get-GitOutput rev-parse HEAD
    $equivalentTargetPaths = @(
        Get-WhitespaceEquivalentPaths -SourceBase $SourceBase -SourceRef $SourceRef -TargetRef $beforeMerge
    )

    if ($PreferTargetContent) {
        $mergeOutput = @(
            & git -c merge.renames=true merge --no-ff --no-commit --no-edit -X ours -m $Message $SourceRef 2>&1
        )
    }
    else {
        $mergeOutput = @(
            & git -c merge.renames=true merge --no-ff --no-commit --no-edit -m $Message $SourceRef 2>&1
        )
    }
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

        if (-not $PreferTargetContent) {
            Write-Host 'The cleaned fork still has semantic conflicts with the target release:'
            $conflictedPaths | ForEach-Object { Write-Host "  $_" }
            & git merge --abort 2>$null
            throw "Cleaned fork changes still conflict with the target release:`n$($conflictedPaths -join "`n")"
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
        if ($equivalentTargetPaths.Count -gt 0) {
            Restore-WhitespaceEquivalentTargetPaths -TargetRef $beforeMerge -Paths $equivalentTargetPaths
        }
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
$originalSourceRef = "origin/$sourcePatchBranch"
$targetVersion = Get-TargetReleaseVersion -BaseBranch $baseBranch -BaseRef $baseRef
if ($null -ne $targetVersion) {
    Write-Host "Detected target release version $targetVersion."
}

$sourceSelection = New-CleanForkSource -SourceBase $sourceBase -SourceRef $originalSourceRef -TargetBranch $targetBranch -TargetVersion $targetVersion
$sourceRef = $sourceSelection.Ref

try {
    $preferTargetContent = [string]::IsNullOrWhiteSpace($sourceSelection.TemporaryBranch)
    $forkPatchCommit = Merge-ForkTree -SourceBase $sourceBase -SourceRef $sourceRef -Message "Port complete fork from $sourcePatchBranch onto $baseBranch" -PreferTargetContent $preferTargetContent
    if ($sourceSelection.DeferredCommits.Count -gt 0) {
        Apply-DeferredForkCommits -Commits $sourceSelection.DeferredCommits
        $forkPatchCommit = Get-GitOutput rev-parse HEAD
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($sourceSelection.TemporaryBranch)) {
        & git branch -D $sourceSelection.TemporaryBranch 2>$null | Out-Host
    }
}

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
