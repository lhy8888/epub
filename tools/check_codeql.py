"""Fail CI on CodeQL errors or security findings rated high/critical (>= 7.0)."""

import argparse
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory")
    args = parser.parse_args()
    reports = sorted(Path(args.directory).rglob("*.sarif"))
    if not reports:
        raise ValueError("CodeQL produced no SARIF reports.")
    total = blocked = 0
    for path in reports:
        report = json.loads(path.read_text(encoding="utf-8-sig"))
        for run in report.get("runs", []):
            rules = {rule["id"]: rule for component in
                     [run["tool"]["driver"], *run["tool"].get("extensions", [])]
                     for rule in component.get("rules", [])}
            for result in run.get("results", []):
                total += 1
                rule = rules.get(result.get("ruleId"), {})
                severity = float(rule.get("properties", {}).get("security-severity", 0))
                level = result.get("level", rule.get("defaultConfiguration", {}).get("level", "warning"))
                if level == "error" or severity >= 7:
                    blocked += 1
                    message = result.get("message", {}).get("text", "")
                    print(f"BLOCKED: {result.get('ruleId')}: severity={severity}, level={level}: {message}")
    print(f"CodeQL: {len(reports)} report(s), {total} finding(s), {blocked} blocking finding(s).")
    return 1 if blocked else 0


if __name__ == "__main__":
    raise SystemExit(main())
