"""Validate mobile output before upload; device signing is a separate release step."""
import hashlib
from pathlib import Path
import plistlib
import subprocess
import struct
import sys
import tarfile
import zipfile
from release_platforms import project_version


def checksum(path):
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    path.with_suffix(path.suffix + ".sha256").write_text(digest + "  " + path.name + "\n")


def main():
    kind, directory = sys.argv[1:]
    root = Path(directory)
    version = project_version()
    if kind == "android":
        apks = sorted(set(root.glob("*-Signed.apk")) | set(root.glob("QuietRead-*-Android-*.apk")))
        assert apks, "No signed development APK"
        for apk in apks:
            with zipfile.ZipFile(apk) as package:
                assert package.testzip() is None, "APK CRC failure"
                names = package.namelist()
                assert "AndroidManifest.xml" in names
                assert any(name.startswith("lib/arm64-v8a/") for name in names), "No ARM64 native runtime"
                assert any(name.startswith("lib/x86_64/") for name in names), "No x64 native runtime"
                # Android 16 KB devices need every 64-bit native LOAD segment to
                # align to 16 KB. Check the actual APK, including transitive assets.
                for name in names:
                    if not name.startswith(("lib/arm64-v8a/", "lib/x86_64/")) or not name.endswith(".so"):
                        continue
                    data = package.read(name)
                    assert data[:6] == b"\x7fELF\x02\x01", "Expected little-endian ELF64: " + name
                    offset = struct.unpack_from("<Q", data, 32)[0]
                    size, count = struct.unpack_from("<HH", data, 54)
                    assert size >= 56
                    for i in range(count):
                        segment_type, flags, file_offset, address, physical, file_size, memory_size, alignment = struct.unpack_from("<IIQQQQQQ", data, offset + i * size)
                        if segment_type == 1:
                            assert alignment >= 16384 and file_offset % 16384 == address % 16384, "Native library lacks 16 KB page support: " + name
            # aapt is part of the runner's verified Android SDK installation.
            import os
            sdk = Path(os.environ.get("ANDROID_HOME", os.environ.get("ANDROID_SDK_ROOT", "")))
            candidates = sorted((sdk / "build-tools").glob("*/aapt"))
            assert candidates, "Cannot inspect Android permissions without aapt"
            permissions = subprocess.check_output([str(candidates[-1]), "dump", "permissions", str(apk)], text=True)
            assert "android.permission.INTERNET" not in permissions, "Offline reader must not request Internet permission"
            assert "MANAGE_EXTERNAL_STORAGE" not in permissions, "Use system document picker"
            badging = subprocess.check_output([str(candidates[-1]), "dump", "badging", str(apk)], text=True)
            assert "versionName='" + version + "'" in badging, "APK version differs from project version"
            renamed = root / ("QuietRead-" + version + "-Android-arm64-x64-development.apk")
            assert not renamed.exists() or renamed == apk, "Duplicate Android release output"
            apk.rename(renamed)
            apk = renamed
            checksum(apk)
    elif kind == "ios":
        for flavor in ("simulator", "device"):
            apps = list((root / ("ios-" + flavor)).rglob("*.app"))
            assert len(apps) == 1, f"Expected one iOS {flavor} app, got {apps}"
            app = apps[0]
            with (app / "Info.plist").open("rb") as stream: info = plistlib.load(stream)
            assert info["CFBundleIdentifier"] == "io.github.lhy8888.quietread"
            assert info["CFBundleShortVersionString"] == version, "iOS version differs from project version"
            assert (app / info["CFBundleExecutable"]).is_file(), "Missing iOS executable"
            output = root / ("QuietRead-" + version + "-iOS-" + flavor + "-unsigned.tar.gz")
            with tarfile.open(output, "w:gz") as archive: archive.add(app, arcname=app.name)
            checksum(output)
    else:
        raise ValueError("Unknown mobile platform: " + kind)
    print("PASS: " + kind + " application, archive and SHA-256")


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        message = str(error).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print("::error title=Mobile package verification::" + type(error).__name__ + ": " + message, flush=True)
        raise
