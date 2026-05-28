#!/usr/bin/env python3
"""Coverage gate for pulseboard-oss CI.

Rules
-----
- Every run : print overall line coverage from the Cobertura report.
- PR events : fail if any .fs file changed in the PR has line coverage
              below TOUCHED_MIN (default 70 %).

Exit codes
----------
0  All checks passed (or non-applicable — not a PR / no coverage file).
1  One or more changed files are below the per-file threshold.
"""

import glob
import os
import subprocess
import sys
import xml.etree.ElementTree as ET
from typing import Optional

TOUCHED_MIN = 0.70   # line-coverage floor for PR-touched .fs files


def find_report() -> Optional[str]:
    matches = glob.glob("coverage/**/coverage.cobertura.xml", recursive=True)
    return matches[0] if matches else None


def overall_coverage(root: ET.Element) -> tuple[float, int, int]:
    total   = int(root.get("lines-valid",   0))
    covered = int(root.get("lines-covered", 0))
    rate    = covered / total if total else 1.0
    return rate, covered, total


def changed_fs_files(base_ref: str) -> set[str]:
    result = subprocess.run(
        ["git", "diff", "--name-only", f"origin/{base_ref}...HEAD"],
        capture_output=True,
        text=True,
        check=True,
    )
    return {p.strip().replace("\\", "/")
            for p in result.stdout.splitlines()
            if p.strip().endswith(".fs")}


def per_file_rates(root: ET.Element) -> dict[str, float]:
    rates: dict[str, float] = {}
    for cls in root.iter("class"):
        fname = cls.get("filename", "").replace("\\", "/")
        if not fname:
            continue
        lines = cls.findall(".//line")
        if not lines:
            continue
        rate = sum(1 for l in lines if int(l.get("hits", "0")) > 0) / len(lines)
        rates[fname] = rate
    return rates


def main() -> None:
    path = find_report()
    if not path:
        print("No coverage.cobertura.xml found — coverage gate skipped.")
        return

    root = ET.parse(path).getroot()
    rate, covered, total = overall_coverage(root)
    print(f"Overall line coverage: {rate:.1%}  ({covered}/{total} lines)\n")

    event    = os.environ.get("GITHUB_EVENT_NAME", "")
    base_ref = os.environ.get("GITHUB_BASE_REF", "")

    if event != "pull_request" or not base_ref:
        print("Not a PR — per-file coverage gate skipped.")
        return

    changed = changed_fs_files(base_ref)
    if not changed:
        print("No .fs files changed in this PR — per-file gate skipped.")
        return

    print(f"Checking coverage on {len(changed)} changed .fs file(s):")
    file_rates = per_file_rates(root)

    failures: list[str] = []
    for path_key in sorted(changed):
        # Match by suffix so the coverage filename doesn't need to be identical
        # to the git-diff path (some tools strip a leading prefix).
        match = next(
            (cov_path for cov_path in file_rates
             if cov_path.endswith(path_key) or path_key.endswith(cov_path)),
            None,
        )
        if match is None:
            print(f"  [--] {path_key}: not found in coverage report (no tests?)")
            continue
        r = file_rates[match]
        status = "OK  " if r >= TOUCHED_MIN else "FAIL"
        print(f"  [{status}] {path_key}: {r:.1%}")
        if r < TOUCHED_MIN:
            failures.append(path_key)

    print()
    if failures:
        print(
            f"Coverage gate FAILED: {len(failures)} file(s) below "
            f"the {TOUCHED_MIN:.0%} per-file threshold:"
        )
        for f in failures:
            print(f"  {f}")
        sys.exit(1)

    print("Per-file coverage gate passed.")


if __name__ == "__main__":
    main()
