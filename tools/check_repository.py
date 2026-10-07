"""Check tracked source files and the project's small, explicit supply-chain policy."""

from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[1]


def main():
    files = subprocess.check_output(
        ["git", "ls-files", "-z"], cwd=ROOT
    ).decode("utf-8").split("\0")
    files = [name for name in files if name]
    if not files:
        raise ValueError("No tracked source files; run git add before checking locally.")
    errors = []
    forbidden = {"bin", "obj", ".vs", "release", "artifacts", "TestResults", "__pycache__"}
    private = re.compile(r"(?:-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{60,})")
    for name in files:
        path = ROOT / name
        if forbidden.intersection(path.relative_to(ROOT).parts):
            errors.append(f"Generated output must not be committed: {name}")
        if path.suffix.lower() in {".exe", ".dll", ".pfx", ".pem", ".key", ".pyc"} or path.name == ".env":
            errors.append(f"Runtime binaries/credentials must not be committed: {name}")
        if path.suffix.lower() in {".ico", ".epub", ".png"}:
            continue
        text = path.read_text(encoding="utf-8-sig")
        if private.search(text):
            errors.append(f"Possible private key or GitHub credential: {name}")
        if name.endswith(".csproj"):
            project = ET.fromstring(text)
            if project.findall(".//PackageReference"):
                errors.append(f"A new NuGet dependency needs a documented policy change: {name}")
        if name.startswith(".github/workflows/"):
            for action in re.findall(r"^\s*-?\s*uses:\s*([^\s#]+)", text, re.MULTILINE):
                if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_./-]+@[0-9a-f]{40}", action):
                    errors.append(f"Action must use a full commit SHA: {name}: {action}")
            if "pull_request_target:" in text:
                errors.append(f"Use unprivileged pull_request events: {name}")
            if "persist-credentials: false" not in text:
                errors.append(f"Checkout must discard credentials: {name}")
    for name in ("README.md", "LICENSE", "SECURITY.md", "CONTRIBUTING.md", "CHANGELOG.md", "QuietRead.sln"):
        if name not in files:
            errors.append(f"Missing project documentation/configuration: {name}")
    manifest = ET.parse(ROOT / "src/QuietRead.App/app.manifest")
    requested = manifest.find(".//{urn:schemas-microsoft-com:asm.v3}requestedExecutionLevel")
    if requested is None or requested.get("level") != "asInvoker":
        errors.append("The application must use normal user privileges.")
    # Compare decoded entries, rather than ZIP timestamps or the host's zlib output.
    with tempfile.TemporaryDirectory(prefix="quietread-sample-") as temporary:
        generated = Path(temporary) / "guide.epub"
        subprocess.run([sys.executable, str(ROOT / "tools/make_sample.py"), str(generated)], check=True)
        with ZipFile(generated) as new, ZipFile(ROOT / "samples/QuietRead-Guide.epub") as committed:
            if set(new.namelist()) != set(committed.namelist()) or any(
                new.read(name) != committed.read(name) for name in new.namelist()
            ):
                errors.append("The committed example EPUB does not match its generator.")
    if errors:
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1
    print(f"PASS: {len(files)} tracked files; source policy, action pins, manifest, sample reproducibility.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
