"""Run NuGet's audit and fail explicitly when its JSON contains vulnerabilities."""

import argparse
import json
from pathlib import Path
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", default="artifacts/dependencies.json")
    args = parser.parse_args()
    result = subprocess.run([
        "dotnet", "package", "list", "--project", "QuietRead.sln", "--no-restore",
        "--include-transitive", "--vulnerable", "--format", "json", "--output-version", "1",
    ], text=True, capture_output=True)
    if result.returncode:
        print(result.stdout)
        print(result.stderr)
        return result.returncode
    report = json.loads(result.stdout)
    path = Path(args.report)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    problems = []
    for project in report.get("projects", []):
        for framework in project.get("frameworks", []):
            for group in ("topLevelPackages", "transitivePackages"):
                for package in framework.get(group, []):
                    for advisory in package.get("vulnerabilities", []):
                        problems.append((project["path"], package["id"], advisory))
    if problems:
        for project, package, advisory in problems:
            print(f"VULNERABLE: {project}: {package}: {advisory}")
        return 1
    print("PASS: NuGet reports no known vulnerable package dependencies.")
    print("This check does not replace updates for the Windows OS or bundled .NET runtime.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
