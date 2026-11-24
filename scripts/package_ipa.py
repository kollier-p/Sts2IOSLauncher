"""Package only the built iOS app and reject known game payloads."""
from pathlib import Path
import plistlib
import subprocess
import zipfile

root = Path("src/Launcher.iOS/bin/Release/net10.0-ios/ios-arm64")
apps = sorted(root.glob("*.app"))
if len(apps) != 1:
    raise SystemExit(f"Expected one app bundle, found {len(apps)}")
app = apps[0]
with (app / "Info.plist").open("rb") as f:
    info = plistlib.load(f)
exe = app / info["CFBundleExecutable"]
subprocess.run(["lipo", "-verify_arch", "arm64", str(exe)], check=True)
if (app / "embedded.mobileprovision").exists() or (app / "_CodeSignature").exists():
    raise SystemExit("Unexpected provisioning or signature directory")
out = Path("dist/STS2IOSLauncher.ipa")
out.parent.mkdir(exist_ok=True)
def check(name):
    parts = name.lower().split("/")
    if any(p in {"sts2.dll", "sts2.deps.json", "sts2.runtimeconfig.json", "release_info.json", "data_sts2_windows_x86_64", "current.json", ".payload_manifest.json", "embedded.mobileprovision", "_codesignature"} or p.endswith((".pck", ".ipa", ".zip", ".7z", ".rar", ".log")) for p in parts):
        raise SystemExit(f"Forbidden payload or archive: {name}")
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for p in sorted(app.rglob("*")):
        if p.is_symlink():
            raise SystemExit(f"Unexpected symlink: {p}")
        if p.is_file():
            name = "Payload/" + app.name + "/" + p.relative_to(app).as_posix()
            check(name)
            z.write(p, name)
with zipfile.ZipFile(out) as z:
    if z.testzip() is not None:
        raise SystemExit("Corrupt IPA")
    for name in z.namelist():
        check(name)
print(f"Packaged and scanned {out}")
