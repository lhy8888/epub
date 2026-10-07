"""Validate a Windows publish directory and build a portable ZIP with checksums."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import struct
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZIP_DEFLATED


ROOT = Path(__file__).resolve().parents[1]


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def verify_windows_exe(path):
    data = path.read_bytes()
    if data[:2] != b"MZ":
        raise ValueError("QuietRead.exe is not a Windows executable.")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0" or struct.unpack_from("<H", data, pe + 4)[0] != 0x8664:
        raise ValueError("QuietRead.exe is not Windows x64.")
    if struct.unpack_from("<H", data, pe + 24 + 68)[0] != 2:
        raise ValueError("QuietRead.exe is not a Windows GUI executable.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish", required=True)
    parser.add_argument("--output", default="artifacts")
    parser.add_argument("--tag")
    args = parser.parse_args()
    version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if not version or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?", version):
        raise ValueError("Invalid project Version.")
    if args.tag and args.tag != f"v{version}":
        raise ValueError(f"Tag {args.tag!r} must match project version v{version}.")
    publish = Path(args.publish).resolve()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    verify_windows_exe(publish / "QuietRead.exe")
    for name in ("QuietRead.dll", "QuietRead.Core.dll", "coreclr.dll", "PresentationFramework.dll", "QuietRead.runtimeconfig.json"):
        if not (publish / name).is_file():
            raise ValueError(f"Self-contained package is missing {name}.")
    runtime = json.loads((publish / "QuietRead.runtimeconfig.json").read_text(encoding="utf-8"))
    frameworks = runtime["runtimeOptions"].get("includedFrameworks", [])
    if {item["name"] for item in frameworks} != {"Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App"}:
        raise ValueError("Publish must be self-contained .NET + Windows Desktop.")
    for framework in frameworks:
        package = framework["name"].lower() + ".runtime.win-x64-" + framework["version"]
        if not any(path.name.startswith(package) for path in (ROOT / "licenses").iterdir()):
            raise ValueError(f"Missing notices for bundled runtime {package}; update licenses before publishing.")
    for name in ("README.md", "LICENSE", "SECURITY.md", "CHANGELOG.md", "CONTRIBUTING.md"):
        shutil.copy2(ROOT / name, publish / name)
    for name in ("samples", "licenses"):
        shutil.copytree(ROOT / name, publish / name, dirs_exist_ok=True)
    shutil.copytree(ROOT / "docs", publish / "docs", dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("*.local.json"))
    (publish / "START-HERE.txt").write_text(
        "QuietRead · 静读 " + version + "\n\n"
        "1. Extract the entire ZIP before running QuietRead.exe.\n"
        "2. Open a local EPUB, or samples/QuietRead-Guide.epub.\n"
        "3. No installation or administrator rights are needed.\n\n"
        "The executable is unsigned. Checksums verify content, not publisher identity.\n"
        "Project: https://github.com/lhy8888/epub\n"
        "See README.md for controls, compatibility, privacy and security limits.\n",
        encoding="utf-8",
    )
    files = sorted(path for path in publish.rglob("*") if path.is_file() and path.name != "SHA256SUMS.txt")
    (publish / "SHA256SUMS.txt").write_text("".join(
        f"{digest(path)}  {path.relative_to(publish).as_posix()}\n" for path in files
    ), encoding="utf-8")
    archive = output / f"QuietRead-{version}-Windows-x64.zip"
    with ZipFile(archive, "w", compression=ZIP_DEFLATED, compresslevel=6) as zip_file:
        for path in sorted(publish.rglob("*")):
            if path.is_file():
                zip_file.write(path, f"QuietRead-{version}-Windows-x64/{path.relative_to(publish).as_posix()}")
    # Read every ZIP entry and verify CRCs before uploading it.
    with ZipFile(archive) as zip_file:
        bad_entry = zip_file.testzip()
        if bad_entry:
            raise ValueError(f"ZIP integrity check failed: {bad_entry}")
    archive.with_suffix(".zip.sha256").write_text(f"{digest(archive)}  {archive.name}\n", encoding="utf-8")
    print(f"PASS: Windows x64 PE, self-contained runtimes, licenses, ZIP CRCs and SHA-256.\n{archive}")


if __name__ == "__main__":
    main()
