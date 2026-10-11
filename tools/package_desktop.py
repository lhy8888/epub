"""Publish and package the shared desktop reader for one explicit native RID."""
import argparse
import hashlib
import plistlib
from pathlib import Path
import shutil
import subprocess
import tarfile
import zipfile
from release_platforms import project_version

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True, choices=["linux-x64", "osx-x64", "osx-arm64", "win-x64"])
    args = parser.parse_args()
    destination = ROOT / "artifacts" / ("desktop-" + args.rid)
    destination.mkdir(parents=True, exist_ok=True)
    output = destination / "publish"
    subprocess.run(["dotnet", "publish", str(ROOT / "src/QuietRead.Desktop"), "-c", "Release", "-r", args.rid,
                    "--self-contained", "true", "-p:PublishSingleFile=false", "-p:RestoreLockedMode=true", "-o", str(output)], check=True)
    version = project_version()
    stem = "QuietRead-" + version + "-" + args.rid
    if args.rid.startswith("osx-"):
        package = destination / (stem + ".app")
        content = package / "Contents"
        executable = content / "MacOS"
        shutil.copytree(output, executable, dirs_exist_ok=True)
        (content / "Resources").mkdir(parents=True, exist_ok=True)
        with (content / "Info.plist").open("wb") as stream:
            plistlib.dump({"CFBundleName": "QuietRead", "CFBundleDisplayName": "QuietRead", "CFBundleExecutable": "QuietRead.Desktop",
                          "CFBundleIdentifier": "io.github.lhy8888.quietread", "CFBundlePackageType": "APPL", "CFBundleVersion": version,
                          "CFBundleShortVersionString": version, "NSHighResolutionCapable": True,
                          "LSMinimumSystemVersion": "13.0"}, stream)
        shutil.copy(ROOT / "LICENSE", content / "Resources")
        shutil.copy(ROOT / "docs/THIRD-PARTY-NOTICES.md", content / "Resources")
        archive = destination / (stem + ".tar.gz")
        with tarfile.open(archive, "w:gz") as target: target.add(package, arcname=package.name)
    elif args.rid.startswith("linux-"):
        package = destination / stem
        shutil.copytree(output, package, dirs_exist_ok=True)
        shutil.copy(ROOT / "LICENSE", package)
        shutil.copy(ROOT / "docs/THIRD-PARTY-NOTICES.md", package)
        shutil.copytree(ROOT / "samples", package / "samples", dirs_exist_ok=True)
        archive = destination / (stem + ".tar.gz")
        with tarfile.open(archive, "w:gz") as target: target.add(package, arcname=package.name)
    else:
        archive = destination / (stem + ".zip")
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as target:
            for path in output.rglob("*"):
                if path.is_file(): target.write(path, arcname=str(Path(stem) / path.relative_to(output)))
            target.write(ROOT / "LICENSE", arcname=stem + "/LICENSE")
            target.write(ROOT / "docs/THIRD-PARTY-NOTICES.md", arcname=stem + "/THIRD-PARTY-NOTICES.md")
    digest = hashlib.file_digest(archive.open("rb"), "sha256").hexdigest()
    archive.with_suffix(archive.suffix + ".sha256").write_text(digest + "  " + archive.name + "\n")
    print(archive)


if __name__ == "__main__":
    main()
