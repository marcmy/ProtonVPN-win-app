#!/usr/bin/env python3
import argparse
import re
from datetime import datetime
from pathlib import Path

SECTION_HEADER_RE = re.compile(r"(?m)^vulnerabilities:[ \t]*(?:#.*)?$")
SECTION_RE = re.compile(
    r"(?ms)^vulnerabilities:[ \t]*(?:#.*)?\n(?P<body>.*?)(?=^[A-Za-z0-9_.-]+:[ \t]*(?:#.*)?$|\Z)"
)
CANONICAL_ENTRY_RE = re.compile(r"(?m)^  - id:[ \t]*(?P<id>\S+)[ \t]*$")
ANY_ENTRY_RE = re.compile(r"(?m)^(?P<indent>[ \t]*)-[ \t]+id:[ \t]*(?P<id>\S+)[ \t]*$")
EXPIRY_RE = re.compile(r"(?m)^    expired_at:[ \t]*(\d{4}-\d{2}-\d{2})[ \t]*$")
STATEMENT_RE = re.compile(r"(?m)^    statement:[ \t]*(\S.*)?$")


def fail(message):
    raise SystemExit(f"Invalid .trivyignore.yaml vulnerability metadata: {message}")


def main():
    parser = argparse.ArgumentParser(
        description="Validate the canonical vulnerability-exception structure consumed by the Trivy report summarizer."
    )
    parser.add_argument("ignore_file")
    args = parser.parse_args()

    text = Path(args.ignore_file).read_text(encoding="utf-8")
    header = SECTION_HEADER_RE.search(text)

    if not header:
        if re.search(r"(?m)^[ \t]*vulnerabilities[ \t]*:", text):
            fail("the vulnerabilities key must be a top-level block key on its own line")
        print("No vulnerability exceptions configured.")
        return

    section = SECTION_RE.search(text)
    if not section:
        fail("could not isolate the vulnerabilities block")

    body = section.group("body")
    canonical = list(CANONICAL_ENTRY_RE.finditer(body))
    any_entries = list(ANY_ENTRY_RE.finditer(body))

    if len(canonical) != len(any_entries):
        fail("every vulnerability entry must start exactly with two spaces followed by '- id:'")

    if not canonical:
        meaningful = [
            line for line in body.splitlines() if line.strip() and not line.lstrip().startswith("#")
        ]
        if meaningful:
            fail("the vulnerabilities block contains data but no canonical '- id:' entries")
        print("No vulnerability exceptions configured.")
        return

    seen_ids = set()
    for index, start in enumerate(canonical):
        vuln_id = start.group("id")
        if vuln_id in seen_ids:
            fail(f"duplicate vulnerability id '{vuln_id}'")
        seen_ids.add(vuln_id)

        end = canonical[index + 1].start() if index + 1 < len(canonical) else len(body)
        block = body[start.start() : end]

        expiries = EXPIRY_RE.findall(block)
        if len(expiries) != 1:
            fail(f"'{vuln_id}' must contain exactly one four-space-indented expired_at: YYYY-MM-DD")
        try:
            datetime.strptime(expiries[0], "%Y-%m-%d")
        except ValueError:
            fail(f"'{vuln_id}' has an invalid expired_at date '{expiries[0]}'")

        statements = STATEMENT_RE.findall(block)
        if len(statements) != 1:
            fail(f"'{vuln_id}' must contain exactly one four-space-indented statement")
        if not statements[0].strip():
            fail(f"'{vuln_id}' statement must not be empty")

        for line in block.splitlines()[1:]:
            if re.match(r"^[ \t]*-[ \t]+", line) and not line.startswith("      - "):
                fail(f"'{vuln_id}' nested list items must use six-space indentation")

    print(f"Validated {len(canonical)} vulnerability exception(s).")


if __name__ == "__main__":
    main()
