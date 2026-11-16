"""Resolve the real upstream ancestor without asking GitHub to render a diff."""

import argparse
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def resolve_baseline(fork_url, fork_commit, upstream_url, baseline_path,
                     upstream_ref="refs/heads/master"):
    if not re.fullmatch(r"[0-9a-f]{40}", fork_commit):
        raise ValueError("The fork commit must be a full lowercase Git SHA.")
    with tempfile.TemporaryDirectory(prefix="proton-baseline-") as directory:
        def git(*args):
            return subprocess.check_output(["git", "-C", directory, *args])

        git("init", "--bare", "--quiet")
        git("remote", "add", "fork", fork_url)
        git("remote", "add", "upstream", upstream_url)
        git("fetch", "--quiet", "--filter=blob:none", "--no-tags", "fork",
            f"{fork_commit}:refs/heads/fork")
        git("fetch", "--quiet", "--filter=blob:none", "--no-tags", "upstream",
            f"{upstream_ref}:refs/heads/upstream")
        merge_base = git("merge-base", "refs/heads/fork", "refs/heads/upstream").decode().strip()
        if not re.fullmatch(r"[0-9a-f]{40}", merge_base):
            raise ValueError("Git did not return a valid upstream merge base.")
        content = git("show", f"{merge_base}:Directory.Packages.props")
        project = ET.fromstring(content)
        if project.tag != "Project" or not project.findall(".//PackageVersion"):
            raise ValueError("The upstream dependency baseline has no package versions.")
        destination = Path(baseline_path)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(content)
    return merge_base


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fork-url", required=True)
    parser.add_argument("--fork-commit", required=True)
    parser.add_argument("--upstream-url", required=True)
    parser.add_argument("--baseline-path", required=True)
    parser.add_argument("--github-output", required=True)
    args = parser.parse_args()
    merge_base = resolve_baseline(args.fork_url, args.fork_commit,
                                  args.upstream_url, args.baseline_path)
    with open(args.github_output, "a", encoding="utf-8") as output:
        output.write(f"upstream_base_commit={merge_base}\n")
    print(f"FastPatch dependency baseline: {merge_base}")
