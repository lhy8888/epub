"""Plan independent platform releases, verify assets, and publish through gh."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
VERSION = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
PLATFORMS = {
    "windows": ("Windows", "QuietRead-Windows-x64", ["Windows-x64.zip"], False,
                "Windows 11 x64：完整解压 ZIP 后运行 QuietRead.exe；包含 .NET 运行时。二进制未签名。"),
    "macos": ("macOS", "QuietRead-osx-*", ["osx-x64.tar.gz", "osx-arm64.tar.gz"], False,
              "macOS 13+：Intel 使用 osx-x64，Apple Silicon 使用 osx-arm64。解压后打开 .app；应用未签名、未公证。"),
    "linux": ("Linux", "QuietRead-linux-x64", ["linux-x64.tar.gz"], False,
              "Linux x64：解压后运行 QuietRead.Desktop。包含 .NET 运行时；需要图形会话、fontconfig 和系统字体。"),
    "android": ("Android", "QuietRead-Android-development", ["Android-arm64-x64-development.apk"], True,
                "Android 6+ ARM64 / x64 开发签名 APK。开发签名并非固定生产密钥，不保证后续构建可原地升级；升级或卸载前请导出阅读数据。未进行手机真机验收。"),
    "ios": ("iOS", "QuietRead-iOS-builds", ["iOS-simulator-unsigned.tar.gz", "iOS-device-unsigned.tar.gz"], True,
            "iOS 15+：提供 Apple Silicon 模拟器应用和 ARM64 未签名设备构建。设备包不能直接安装；需要 Apple 签名及描述文件，不是 App Store/TestFlight 分发包。未进行手机真机验收。"),
}


def project_version():
    version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
    if not version or not re.fullmatch(VERSION, version):
        raise ValueError("Project Version must be a three-part numeric version")
    return version


def release_plan(tag):
    match = re.fullmatch(r"(release|unix|windows|macos|linux|android|ios)-v(" + VERSION + ")", tag)
    if not match or match[2] != project_version():
        raise ValueError("Release tag must select a platform (or release) and match project Version")
    platforms = PLATFORMS if match[1] == "release" else ["macos", "linux"] if match[1] == "unix" else [match[1]]
    return {"include": [{"platform": p, "tag": p + "-v" + match[2],
                         "title": "QuietRead " + PLATFORMS[p][0] + " " + match[2],
                         "artifact": PLATFORMS[p][1]} for p in platforms]}


def assets_for(platform, tag, directory):
    plan = release_plan(tag)
    if len(plan["include"]) != 1 or plan["include"][0]["platform"] != platform:
        raise ValueError("Publisher requires one matching platform tag")
    root = Path(directory)
    names = ["QuietRead-" + project_version() + "-" + suffix for suffix in PLATFORMS[platform][2]]
    binaries = [p for p in root.rglob("*") if p.name.endswith((".zip", ".tar.gz", ".apk"))]
    if len(binaries) != len(names) or {p.name for p in binaries} != set(names):
        raise ValueError("Missing, duplicate, unexpected, or wrong-version release binaries: " + repr(binaries))
    by_name = {p.name: p for p in binaries}
    assets = []
    for name in names:
        package = by_name[name]
        checksum = package.with_name(name + ".sha256")
        if package.is_symlink() or checksum.is_symlink() or package.stat().st_size == 0:
            raise ValueError("Release assets must be nonempty regular files")
        with package.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if checksum.read_text(encoding="utf-8") != digest + "  " + name + "\n":
            raise ValueError("Checksum mismatch: " + name)
        assets.extend([package, checksum])
    return assets


def gh(*args, optional=False):
    result = subprocess.run(["gh", *args], text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        if optional and "not found" in result.stderr.lower():
            return None
        raise RuntimeError(result.stderr.strip() or "GitHub CLI failed")
    return result.stdout


def publish(platform, tag, directory):
    assets = assets_for(platform, tag, directory)
    repository = os.environ["GITHUB_REPOSITORY"]
    commit = os.environ["GITHUB_SHA"]
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository) or not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Invalid release repository or commit")
    title, _, _, preview, description = PLATFORMS[platform]
    notes = Path("release-notes.md")
    notes.write_text("# QuietRead " + title + " " + project_version() + "\n\n" + description + "\n\n"
                     + (ROOT / "docs/RELEASE-NOTES.md").read_text(encoding="utf-8"), encoding="utf-8")
    fields = "tagName,isDraft,isPrerelease,assets,url"
    existing = gh("release", "view", tag, "--repo", repository, "--json", fields, optional=True)
    ref = gh("api", "repos/" + repository + "/git/ref/tags/" + tag, optional=True)
    if ref:
        target = json.loads(ref)["object"]
        if target["type"] != "commit" or target["sha"] != commit:
            raise ValueError("Existing platform tag does not point directly to the verified commit")
    if existing and not ref:
        raise ValueError("Existing release has no verifiable source tag")
    if not ref:
        gh("api", "--method", "POST", "repos/" + repository + "/git/refs",
           "-f", "ref=refs/tags/" + tag, "-f", "sha=" + commit)
    if not existing:
        command = ["release", "create", tag, "--repo", repository, "--verify-tag",
                   "--title", "QuietRead " + title + " " + project_version(), "--notes-file", str(notes),
                   "--draft", "--latest=false"]
        if preview:
            command.append("--prerelease")
        gh(*command)
    if not existing or json.loads(existing)["isDraft"]:
        gh("release", "upload", tag, *(str(p) for p in assets), "--repo", repository, "--clobber")
    release = json.loads(gh("release", "view", tag, "--repo", repository, "--json", fields))
    if {p["name"] for p in release["assets"]} != {p.name for p in assets}:
        raise ValueError("GitHub release attachment list differs from verified assets")
    if release["isPrerelease"] != preview:
        raise ValueError("GitHub prerelease status differs from platform availability")
    # Download uploaded bytes before making the draft public. Never replace a published asset.
    with tempfile.TemporaryDirectory(prefix="quietread-release-") as temporary:
        gh("release", "download", tag, "--repo", repository, "--dir", temporary)
        assets_for(platform, tag, temporary)
        for asset in assets:
            with asset.open("rb") as source, (Path(temporary) / asset.name).open("rb") as downloaded:
                if hashlib.file_digest(source, "sha256").digest() != hashlib.file_digest(downloaded, "sha256").digest():
                    raise ValueError("Uploaded attachment differs: " + asset.name)
    # Verify the platform tag again after uploading the attachments.
    ref = json.loads(gh("api", "repos/" + repository + "/git/ref/tags/" + tag))["object"]
    if ref["type"] != "commit" or ref["sha"] != commit:
        raise ValueError("Published platform source tag differs from verified commit")
    if release["isDraft"]:
        gh("release", "edit", tag, "--repo", repository, "--draft=false", "--latest=false")
    final = json.loads(gh("release", "view", tag, "--repo", repository, "--json", fields))
    if final["isDraft"]:
        raise ValueError("Release remained a draft")
    report = {"platform": platform, "version": project_version(), "commit": commit, **final}
    Path("release-verification.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(final["url"])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["plan", "verify", "publish"])
    parser.add_argument("--tag", required=True)
    parser.add_argument("--platform", choices=PLATFORMS)
    parser.add_argument("--directory", default="artifacts")
    parser.add_argument("--output")
    args = parser.parse_args()
    if args.command == "plan":
        matrix = json.dumps(release_plan(args.tag), separators=(",", ":"))
        if args.output:
            with Path(args.output).open("a", encoding="utf-8") as stream:
                stream.write("matrix=" + matrix + "\n")
        print(matrix)
    elif not args.platform:
        parser.error("--platform is required for verify and publish")
    elif args.command == "verify":
        print("PASS: " + ", ".join(p.name for p in assets_for(args.platform, args.tag, args.directory)))
    else:
        publish(args.platform, args.tag, args.directory)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        message = (type(error).__name__ + ": " + str(error)).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print("::error title=Platform release verification::" + message, flush=True)
        raise
