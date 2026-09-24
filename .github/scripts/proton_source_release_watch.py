import re
from typing import Callable, Iterable


def parse_version(value: str) -> tuple[int, int, int]:
    if not re.fullmatch(r"\d+\.\d+\.\d+", value):
        raise ValueError(f"Invalid Proton source version: {value!r}")
    return tuple(int(part) for part in value.split("."))


def find_latest_shared_release_tag(
    candidate_version: str,
    target_version: str,
    official_versions: Iterable[str],
    *,
    candidate_ref: str,
    target_ref: str,
    is_ancestor: Callable[[str, str], bool],
) -> str | None:
    """Find the newest official tag that is an ancestor of both branches."""
    candidate_key = parse_version(candidate_version)
    target_key = parse_version(target_version)
    eligible_versions = {
        version
        for version in official_versions
        if parse_version(version) <= candidate_key and parse_version(version) <= target_key
    }

    for version in sorted(eligible_versions, key=parse_version, reverse=True):
        tag_ref = f"refs/tags/v{version}"
        if is_ancestor(tag_ref, candidate_ref) and is_ancestor(tag_ref, target_ref):
            return version

    return None


def select_source_patch_branch(
    version: str,
    *,
    candidate_versions: Iterable[str],
    compatible_candidate_versions: Iterable[str],
) -> str:
    """Choose the newest reviewed port sharing an official release base with the target."""

    target_key = parse_version(version)
    candidates = {candidate for candidate in candidate_versions if parse_version(candidate) <= target_key}
    if not candidates:
        return "marc/proton"

    latest_candidate = max(candidates, key=parse_version)
    compatible = set(compatible_candidate_versions)
    if latest_candidate not in compatible:
        raise ValueError(
            f"Candidate branch 'future/proton/v{latest_candidate}' has no verified official-tag "
            f"ancestor in common with v{version}; refusing to fall back to marc/proton."
        )

    return f"future/proton/v{latest_candidate}"
