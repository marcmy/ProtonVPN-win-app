#!/usr/bin/env python3
"""
Merge master into develop-v4 after a release promotion.

Brings the promoted release (version bump, tag on master, release hotfixes) back
into the default development branch.

Required environment variables:
  - CI_REPOSITORY_URL, RELEASE_PAT, RELEASE_GIT_EMAIL, RELEASE_GIT_USERNAME
"""
import os
import subprocess

# Branch merged from (lineage after merge-release-to-master)
SOURCE_BRANCH = "master"
# Target branch to merge into — uses the GitLab default branch (CI_DEFAULT_BRANCH),
# falling back to "develop-v4" when running outside CI.
TARGET_BRANCH = os.getenv("CI_DEFAULT_BRANCH", "develop-v4")

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
    pat = os.getenv("RELEASE_PAT")
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

def describe_latest_tag(ref: str) -> str | None:
    """Return the nearest tag name reachable from ref, or None."""
    result = subprocess.run(
        ["git", "describe", "--tags", "--abbrev=0", ref],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        return None
    tag = result.stdout.strip()
    return tag or None

def fetch_source_branch() -> None:
    run_git("fetch", "origin", SOURCE_BRANCH)

def checkout_target_branch() -> None:
    run_git("fetch", "origin", f"{TARGET_BRANCH}:{TARGET_BRANCH}")
    run_git("checkout", TARGET_BRANCH)

def merge_message_for_sync() -> str:
    """Merge commit message for merging master into the development branch."""
    remote_master = f"origin/{SOURCE_BRANCH}"
    tag = describe_latest_tag(remote_master)
    if tag:
        return f"Merge {SOURCE_BRANCH} into {TARGET_BRANCH} after release {tag}"
    return f"Merge {SOURCE_BRANCH} into {TARGET_BRANCH}"

def merge_master_into_target(merge_msg: str) -> None:
    remote_ref = f"origin/{SOURCE_BRANCH}"
    run_git("merge", remote_ref, "--no-ff", "-m", merge_msg)

def push_target_branch() -> None:
    try:
        run_git("push", "origin", TARGET_BRANCH)
    finally:
        orig_repo = os.getenv("CI_REPOSITORY_URL")
        if orig_repo:
            run_git("remote", "set-url", "origin", orig_repo)

# --- Main ---
branch = os.getenv("CI_COMMIT_BRANCH")
if not branch:
    raise SystemExit("No branch provided. Set CI_COMMIT_BRANCH or pass branch as first argument.")

if branch != SOURCE_BRANCH:
    raise SystemExit(f"This job must run from branch {SOURCE_BRANCH}, not '{branch}'")

print(f"Merging {SOURCE_BRANCH} into {TARGET_BRANCH}.")

email = os.getenv("RELEASE_GIT_EMAIL")
username = os.getenv("RELEASE_GIT_USERNAME")
if not email or not username:
    raise SystemExit("RELEASE_GIT_EMAIL and RELEASE_GIT_USERNAME must be set")

configure_git(email, username)
set_authenticated_remote()  # Set PAT-based URL before fetching
fetch_source_branch()
merge_msg = merge_message_for_sync()
checkout_target_branch()
merge_master_into_target(merge_msg)
push_target_branch()

print(f"Merge complete: {SOURCE_BRANCH} -> {TARGET_BRANCH}.")
