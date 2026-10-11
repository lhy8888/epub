"""Compile both Apple targets, retaining native-tool errors in CI annotations."""
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    targets = (("restore", None),) if "--restore" in sys.argv else (("simulator", "iossimulator-arm64"), ("device", "ios-arm64"))
    for flavor, rid in targets:
        command = ["dotnet", "build", "src/QuietRead.iOS", "-c", "Release", "--no-restore", "-r", rid,
                   "-p:EnableCodeSigning=false", "-p:RestoreLockedMode=true", "-p:OutputPath=../../artifacts/ios-" + flavor + "/"]
        if flavor == "restore":
            command = ["dotnet", "restore", "src/QuietRead.iOS", "--locked-mode"]
        lines = []
        with subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True) as process:
            for line in process.stdout:
                print(line, end="", flush=True)
                lines.append(line)
            code = process.wait()
        (ROOT / "artifacts").mkdir(exist_ok=True)
        (ROOT / "artifacts" / ("ios-" + flavor + ".log")).write_text("".join(lines))
        if code:
            important = [line for line in lines if "error" in line.lower()]
            message = ("".join(important) + "\n" + "".join(lines[-15:]))[-7000:]
            message = message.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
            print("::error title=iOS " + flavor + " native build::" + message, flush=True)
            return code
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
