#!/usr/bin/env python3
"""Validate restored NuGet lockfiles and submit an actual GitHub dependency graph."""
import argparse
import datetime as dt
import json
import os
from pathlib import Path
import sys
import urllib.request


def build_snapshot(root):
    manifests = {}
    unique = set()
    locks = sorted(root.glob("src/**/packages.lock.json"))
    if len(locks) < 3:
        raise RuntimeError(f"Expected >=3 restored packages.lock.json files; found {len(locks)}")
    for lock in locks:
        doc = json.loads(lock.read_text(encoding="utf-8-sig"))
        if not isinstance(doc.get("dependencies"), dict):
            raise RuntimeError(f"Invalid lockfile: {lock}")
        projects = sorted(lock.parent.glob("*.csproj"))
        if not projects:
            raise RuntimeError(f"No .csproj beside lockfile {lock}")
        project = projects[0].relative_to(root).as_posix()
        resolved = {}
        development = ".tests" in project.lower() or "/tests/" in project.lower()
        for packages in doc["dependencies"].values():
            if not isinstance(packages, dict):
                continue
            names = {
                key.lower(): f"pkg:nuget/{key}@{value['resolved']}"
                for key, value in packages.items()
                if isinstance(value, dict) and value.get("resolved")
                and str(value.get("type", "")).lower() != "project"
            }
            for package_id, details in packages.items():
                if not isinstance(details, dict) or package_id.lower() not in names:
                    continue
                purl = names[package_id.lower()]
                children = {
                    names[d.lower()] for d in (details.get("dependencies") or {})
                    if d.lower() in names
                }
                entry = resolved.setdefault(purl, {
                    "package_url": purl, "relationship": "indirect",
                    "scope": "development" if development else "runtime",
                    "dependencies": [],
                })
                if str(details.get("type", "")).lower() == "direct":
                    entry["relationship"] = "direct"
                entry["dependencies"] = sorted(set(entry["dependencies"]) | children)
                unique.add(purl)
        if resolved:
            manifests[project] = {
                "name": project, "file": {"source_location": project},
                "resolved": resolved,
            }
    if len(unique) < 30 or not any(x.lower().startswith("pkg:nuget/log4net@") for x in unique):
        raise RuntimeError(
            f"Incomplete inventory ({len(unique)} packages). "
            "Expected >=30 including log4net; refusing to submit."
        )
    snap = {
        "version": 0,
        "sha": os.environ.get("GITHUB_SHA", "0" * 40),
        "ref": os.environ.get("GITHUB_REF", "refs/heads/validation"),
        "job": {"correlator": "protonvpn-restored-nuget", "id": str(os.environ.get("GITHUB_RUN_ID", "local"))},
        "detector": {
            "name": "ProtonVPN resolved NuGet lockfiles", "version": "1.0.0",
            "url": "https://github.com/marcmy/ProtonVPN-win-app",
        },
        "scanned": dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z"),
        "manifests": manifests,
    }
    return snap, len(locks), len(unique)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", default="artifacts/nuget-snapshot.json")
    parser.add_argument("--submit", action="store_true")
    options = parser.parse_args()
    root = Path.cwd()
    snapshot, locks, packages = build_snapshot(root)
    output = root / options.output
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(snapshot, indent=2) + "\n", encoding="utf-8")
    print(f"NuGet inventory: {locks} locks, {len(snapshot['manifests'])} project manifests, "
          f"{packages} resolved unique packages (including log4net).")
    if not options.submit:
        print("Validation only: no API writes.")
        return
    token = os.environ["GH_TOKEN"]
    repo = os.environ["GITHUB_REPOSITORY"]
    if not token:
        raise RuntimeError("Empty GH_TOKEN")
    req = urllib.request.Request(
        f"https://api.github.com/repos/{repo}/dependency-graph/snapshots",
        method="POST", data=json.dumps(snapshot).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "Content-Type": "application/json",
            "User-Agent": "ProtonVPN-NuGet-Snapshot",
        },
    )
    with urllib.request.urlopen(req, timeout=60) as response:
        body = json.loads(response.read())
        if response.status != 201 or body.get("result") != "SUCCESS":
            raise RuntimeError(f"Dependency graph submission rejected: {body}")
        print(f"GitHub accepted nonempty snapshot id={body.get('id')}")


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print(f"::error::{e}", file=sys.stderr)
        raise SystemExit(1)
