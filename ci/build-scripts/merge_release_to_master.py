#!/usr/bin/env python3
"""
Promote a release branch to master: merge into master and tag the merge commit.

Required environment variables:
  - CI_REPOSITORY_URL, RELEASE_PAT, RELEASE_GIT_EMAIL, RELEASE_GIT_USERNAME
"""
import os
import re
import subprocess

# Target branch to merge into
TARGET_BRANCH = "master"

def run_git(*args: str, safe_args_count: int | None = None) -> None:
    """Run a git command and raise on failure.
    
    Args:
        *args: Git command arguments.
        safe_args_count: Number of arguments safe to display in error messages.
                        If None, all arguments are displayed.
                        Remaining arguments are replaced with '***'.
    """
    result = subprocess.run(["git", *args], check=False)
    if result.returncode != 0:
        if safe_args_count is not None:
            display_args = list(args[:safe_args_count]) + ["***"] * (len(args) - safe_args_count)
        else:
            display_args = list(args)
        raise SystemExit(f"Git command failed: git {' '.join(display_args)}")

def get_remote_url() -> str:
    repository = os.getenv("CI_REPOSITORY_URL", "")
    if "@" not in repository:
        raise SystemExit("CI_REPOSITORY_URL is missing or malformed (expected '@' in URL)")
    pat = os.getenv('RELEASE_PAT')
    if not pat:
        raise SystemExit("RELEASE_PAT must be set")
    user = f"git:{pat}"
    (_, url) = repository.split("@", 1)  # split only on first @
    return f"https://{user}@{url.replace(':', '/')}"

def configure_git(git_email: str, git_username: str) -> None:
    run_git("config", "user.email", git_email, safe_args_count=2)
    run_git("config", "user.name", git_username, safe_args_count=2)

def set_authenticated_remote() -> None:
    """Set the remote URL to use RELEASE_PAT for authentication."""
    run_git("remote", "set-url", "origin", get_remote_url(), safe_args_count=3)

def checkout_target_branch() -> None:
    run_git("fetch", "origin", f"{TARGET_BRANCH}:{TARGET_BRANCH}")
    run_git("checkout", TARGET_BRANCH)

def fetch_branch(branch_name: str) -> None:
    run_git("fetch", "origin", branch_name)

def merge_release_into_target(branch_name: str, version: str) -> None:
    remote_ref = f"origin/{branch_name}"
    # Try a regular merge first; if it fails, inspect conflicts and auto-resolve
    result = subprocess.run(["git", "merge", remote_ref, "--no-ff", "-m", f"Promote release {version} into {TARGET_BRANCH}"], check=False)
    if result.returncode == 0:
        return

    # Merge failed — check unmerged files
    ls = subprocess.run(["git", "ls-files", "-u"], capture_output=True, text=True)
    if ls.returncode != 0:
        raise SystemExit(f"Git command failed: git merge origin/{branch_name} --no-ff -m Promote release {version} into {TARGET_BRANCH}")

    unmerged = set()
    for line in ls.stdout.splitlines():
        # ls-files -u output: <mode> <hash> <stage>\t<file>
        parts = line.split()
        if len(parts) >= 4:
            path = parts[3]
            unmerged.add(path)

    target_file = "src/GlobalAssemblyInfo.cs"
    if unmerged and all(p == target_file for p in unmerged):
        print(f"Auto-resolving expected conflict in {target_file} using release branch version.")
        run_git("checkout", "--theirs", target_file)
        run_git("add", target_file)
        run_git("commit", "--no-edit")
        return

    # Unknown conflicts — abort and surface the error
    if unmerged:
        raise SystemExit(f"Merge failed with conflicts in: {', '.join(sorted(unmerged))}")
    else:
        raise SystemExit(f"Git merge failed for unknown reasons when merging {remote_ref} into {TARGET_BRANCH}")

def release_tag(tag_name: str) -> None:
    run_git("tag", "-a", tag_name, "-m", f"Release {tag_name}")

def push_target_and_tag(tag_name: str) -> None:
    try:
        run_git("push", "origin", TARGET_BRANCH)
        run_git("push", "origin", tag_name)
    finally:
        orig_repo = os.getenv("CI_REPOSITORY_URL")
        if orig_repo:
            run_git("remote", "set-url", "origin", orig_repo)

def branch_to_version(branch_name: str) -> str:
    m = re.match(r"^release/(.+)$", branch_name)
    if not m:
        raise SystemExit(f"Branch '{branch_name}' does not look like a release branch (release/X.Y.Z)")
    return m.group(1)

# --- Main ---
branch = os.getenv("CI_COMMIT_BRANCH")
if not branch:
    raise SystemExit("No branch provided. Set CI_COMMIT_BRANCH or pass branch as first argument.")

print(f"Promoting {branch} into {TARGET_BRANCH}.")

if branch.startswith("release/9.9.9-from"):
    raise SystemExit("Refusing to promote special test branch starting with 'release/9.9.9-from'")

email = os.getenv("RELEASE_GIT_EMAIL")
username = os.getenv("RELEASE_GIT_USERNAME")
if not email or not username:
    raise SystemExit("RELEASE_GIT_EMAIL and RELEASE_GIT_USERNAME must be set")

version = branch_to_version(branch)

configure_git(email, username)
set_authenticated_remote()  # Set PAT-based URL before fetching
fetch_branch(branch)
checkout_target_branch()
merge_release_into_target(branch, version)

tag_name = f"v{version}"
release_tag(tag_name)
push_target_and_tag(tag_name)

print(f"Promotion complete: {branch} -> {TARGET_BRANCH}, tag {tag_name} pushed.")
