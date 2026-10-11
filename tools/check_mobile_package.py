"""Validate mobile output before upload; device signing is a separate release step."""
import hashlib
from pathlib import Path
import plistlib
import subprocess
import sys
import tarfile
import zipfile


def checksum(path):
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    path.with_suffix(path.suffix + ".sha256").write_text(digest + "  " + path.name + "\n")


def main():
    kind, directory = sys.argv[1:]
    root = Path(directory)
    if kind == "android":
        apks = list(root.glob("*-Signed.apk"))
        assert apks, "No signed development APK"
        for apk in apks:
            with zipfile.ZipFile(apk) as package:
                assert package.testzip() is None, "APK CRC failure"
                names = package.namelist()
                assert "AndroidManifest.xml" in names
                assert any(name.startswith("lib/arm64-v8a/") for name in names), "No ARM64 native runtime"
                assert any(name.startswith("lib/x86_64/") for name in names), "No x64 native runtime"
            # aapt is part of the runner's verified Android SDK installation.
            import os
            sdk = Path(os.environ.get("ANDROID_HOME", os.environ.get("ANDROID_SDK_ROOT", "")))
            candidates = sorted((sdk / "build-tools").glob("*/aapt"))
            assert candidates, "Cannot inspect Android permissions without aapt"
            permissions = subprocess.check_output([str(candidates[-1]), "dump", "permissions", str(apk)], text=True)
            assert "android.permission.INTERNET" not in permissions, "Offline reader must not request Internet permission"
            assert "MANAGE_EXTERNAL_STORAGE" not in permissions, "Use system document picker"
            checksum(apk)
    elif kind == "ios":
        for flavor in ("simulator", "device"):
            apps = list((root / ("ios-" + flavor)).rglob("*.app"))
            assert len(apps) == 1, f"Expected one iOS {flavor} app, got {apps}"
            app = apps[0]
            with (app / "Info.plist").open("rb") as stream: info = plistlib.load(stream)
            assert info["CFBundleIdentifier"] == "io.github.lhy8888.quietread"
            assert (app / info["CFBundleExecutable"]).is_file(), "Missing iOS executable"
            output = root / ("QuietRead-ios-" + flavor + ".tar.gz")
            with tarfile.open(output, "w:gz") as archive: archive.add(app, arcname=app.name)
            checksum(output)
    else:
        raise ValueError("Unknown mobile platform: " + kind)
    print("PASS: " + kind + " application, archive and SHA-256")


if __name__ == "__main__":
    main()
