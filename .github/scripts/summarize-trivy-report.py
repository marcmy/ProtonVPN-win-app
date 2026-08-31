#!/usr/bin/env python3
import argparse
import json
import os
import re
from collections import Counter
from datetime import date, datetime
from pathlib import Path

SEVERITIES = ("MEDIUM", "HIGH", "CRITICAL")
SEVERITY_RANK = {"CRITICAL": 3, "HIGH": 2, "MEDIUM": 1, "UNKNOWN": 0}


def _severity(value):
    value = (value or "UNKNOWN").upper()
    return value if value in SEVERITIES else "UNKNOWN"


def _finding_sort_key(finding):
    return (-SEVERITY_RANK.get(finding["severity"], 0), finding["id"])


def _active_findings(report):
    findings = []
    for result in report.get("Results") or []:
        target = result.get("Target") or ""
        for vuln in result.get("Vulnerabilities") or []:
            findings.append(
                {
                    "target": target,
                    "id": vuln.get("VulnerabilityID") or vuln.get("ID") or "UNKNOWN",
                    "pkg": vuln.get("PkgName") or vuln.get("PkgID") or "UNKNOWN",
                    "installed": vuln.get("InstalledVersion") or "",
                    "fixed": vuln.get("FixedVersion") or "",
                    "severity": _severity(vuln.get("Severity")),
                }
            )
    return findings


def _suppressed_findings(report):
    findings = []
    for result in report.get("Results") or []:
        target = result.get("Target") or ""
        for modified in result.get("ExperimentalModifiedFindings") or []:
            finding = modified.get("Finding") or {}
            vuln = finding.get("Vulnerability") if isinstance(finding.get("Vulnerability"), dict) else finding
            if not isinstance(vuln, dict):
                continue
            vuln_id = vuln.get("VulnerabilityID") or vuln.get("ID")
            if not vuln_id:
                continue
            findings.append(
                {
                    "target": target,
                    "id": vuln_id,
                    "pkg": vuln.get("PkgName") or vuln.get("PkgID") or "UNKNOWN",
                    "installed": vuln.get("InstalledVersion") or "",
                    "fixed": vuln.get("FixedVersion") or "",
                    "severity": _severity(vuln.get("Severity")),
                    "status": modified.get("Status") or "suppressed",
                    "statement": modified.get("Statement") or "",
                }
            )
    return findings


def _vulnerability_ignore_entries(ignore_text):
    match = re.search(
        r"(?ms)^vulnerabilities:\s*\n(?P<body>.*?)(?=^[A-Za-z0-9_.-]+:\s*(?:#.*)?$|\Z)",
        ignore_text,
    )
    if not match:
        return []

    body = match.group("body")
    starts = list(re.finditer(r"(?m)^  - id:\s*(?P<id>\S+)\s*$", body))
    entries = []
    for index, start in enumerate(starts):
        end = starts[index + 1].start() if index + 1 < len(starts) else len(body)
        block = body[start.start() : end]
        expiry_match = re.search(r"(?m)^\s{4}expired_at:\s*(\d{4}-\d{2}-\d{2})\s*$", block)
        purls = re.findall(r"(?m)^\s{6}-\s*[\"']?(pkg:[^\"'\s]+)[\"']?\s*$", block)
        paths = re.findall(r"(?m)^\s{6}-\s*[\"']?([^\"'\n]+)[\"']?\s*$", block)
        paths = [path for path in paths if not path.startswith("pkg:")]
        entries.append(
            {
                "id": start.group("id"),
                "expired_at": expiry_match.group(1) if expiry_match else None,
                "purls": purls,
                "paths": paths,
            }
        )
    return entries


def _write_output(name, value):
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as handle:
            handle.write(f"{name}={value}\n")


def _escape_table(value):
    return str(value or "").replace("|", "\\|").replace("\n", " ")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", required=True)
    parser.add_argument("--ignore", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--today", help="Override YYYY-MM-DD for tests")
    args = parser.parse_args()

    report = json.loads(Path(args.report).read_text(encoding="utf-8"))
    ignore_text = Path(args.ignore).read_text(encoding="utf-8")
    today = datetime.strptime(args.today, "%Y-%m-%d").date() if args.today else date.today()

    active = _active_findings(report)
    suppressed = _suppressed_findings(report)
    ignores = _vulnerability_ignore_entries(ignore_text)

    active_counts = Counter(finding["severity"] for finding in active)
    suppressed_counts = Counter(finding["severity"] for finding in suppressed)

    expired = []
    missing_expiry = []
    due_soon = []
    for entry in ignores:
        if not entry["expired_at"]:
            missing_expiry.append(entry)
            continue
        expiry = datetime.strptime(entry["expired_at"], "%Y-%m-%d").date()
        days_left = (expiry - today).days
        entry["days_left"] = days_left
        if days_left < 0:
            expired.append(entry)
        elif days_left <= 14:
            due_soon.append(entry)

    lines = [
        "# Medium+ vulnerability report",
        "",
        "This lane is informational for Medium findings. High/Critical findings remain governed by the blocking supply-chain audit.",
        "",
        "## Counts",
        "",
        "| State | Medium | High | Critical | Total |",
        "| --- | ---: | ---: | ---: | ---: |",
        f"| Active / unignored | {active_counts['MEDIUM']} | {active_counts['HIGH']} | {active_counts['CRITICAL']} | {len(active)} |",
        f"| Accepted / suppressed | {suppressed_counts['MEDIUM']} | {suppressed_counts['HIGH']} | {suppressed_counts['CRITICAL']} | {len(suppressed)} |",
        "",
    ]

    if active:
        lines += [
            "## Active findings",
            "",
            "| Severity | ID | Package | Installed | Fixed | Target |",
            "| --- | --- | --- | --- | --- | --- |",
        ]
        for finding in sorted(active, key=_finding_sort_key):
            lines.append(
                f"| {_escape_table(finding['severity'])} | {_escape_table(finding['id'])} | "
                f"{_escape_table(finding['pkg'])} | {_escape_table(finding['installed'])} | "
                f"{_escape_table(finding['fixed'])} | {_escape_table(finding['target'])} |"
            )
        lines.append("")

    if suppressed:
        lines += [
            "## Accepted / suppressed findings",
            "",
            "| Severity | ID | Package | Installed | Fixed | Status | Statement |",
            "| --- | --- | --- | --- | --- | --- | --- |",
        ]
        for finding in sorted(suppressed, key=_finding_sort_key):
            lines.append(
                f"| {_escape_table(finding['severity'])} | {_escape_table(finding['id'])} | "
                f"{_escape_table(finding['pkg'])} | {_escape_table(finding['installed'])} | "
                f"{_escape_table(finding['fixed'])} | {_escape_table(finding['status'])} | "
                f"{_escape_table(finding['statement'])} |"
            )
        lines.append("")

    lines += [
        "## Ignore lifecycle",
        "",
        "| ID | Expires | Days remaining | Scope |",
        "| --- | --- | ---: | --- |",
    ]
    if ignores:
        for entry in ignores:
            scope_bits = []
            if entry["purls"]:
                scope_bits.append(", ".join(entry["purls"]))
            if entry["paths"]:
                scope_bits.append(", ".join(entry["paths"]))
            scope = "; ".join(scope_bits) or "(global)"
            days = entry.get("days_left", "missing")
            lines.append(
                f"| {_escape_table(entry['id'])} | {_escape_table(entry['expired_at'] or 'MISSING')} | "
                f"{_escape_table(days)} | {_escape_table(scope)} |"
            )
    else:
        lines.append("| — | — | — | No vulnerability ignores configured |")
    lines.append("")

    if due_soon:
        lines += [
            "### Review due soon",
            "",
            *[f"- `{entry['id']}` expires on **{entry['expired_at']}** ({entry['days_left']} days)." for entry in due_soon],
            "",
        ]
    if expired:
        lines += [
            "### Expired exceptions",
            "",
            *[f"- `{entry['id']}` expired on **{entry['expired_at']}**." for entry in expired],
            "",
        ]
    if missing_expiry:
        lines += [
            "### Invalid exceptions",
            "",
            *[f"- `{entry['id']}` has no `expired_at` review date." for entry in missing_expiry],
            "",
        ]

    Path(args.summary).write_text("\n".join(lines) + "\n", encoding="utf-8")

    _write_output("active_medium", active_counts["MEDIUM"])
    _write_output("active_high", active_counts["HIGH"])
    _write_output("active_critical", active_counts["CRITICAL"])
    _write_output("active_total", len(active))
    _write_output("suppressed_total", len(suppressed))
    _write_output("expired_count", len(expired))
    _write_output("missing_expiry_count", len(missing_expiry))

    if active_counts["MEDIUM"]:
        print(f"::warning::{active_counts['MEDIUM']} active Medium vulnerability finding(s) detected.")
    if active_counts["HIGH"] or active_counts["CRITICAL"]:
        print(
            f"::error::{active_counts['HIGH']} High and {active_counts['CRITICAL']} Critical "
            "active vulnerability finding(s) detected."
        )
    for entry in due_soon:
        print(f"::warning::Security exception {entry['id']} expires on {entry['expired_at']} ({entry['days_left']} days).")
    for entry in expired:
        print(f"::error::Security exception {entry['id']} expired on {entry['expired_at']}.")
    for entry in missing_expiry:
        print(f"::error::Security exception {entry['id']} has no expired_at review date.")


if __name__ == "__main__":
    main()
