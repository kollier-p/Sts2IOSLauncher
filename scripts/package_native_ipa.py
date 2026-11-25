#!/usr/bin/env python3
"""Create an unsigned or development-signed IPA from the validated Godot app."""

import argparse
import datetime
import pathlib
import plistlib
import re
import shutil
import stat
import subprocess
import tempfile
import zipfile

from integrate_extensions import FMOD_NAME, SPINE_NAME
from validate_native_app import validate


def signing_entitlements(profile, bundle_identifier, now):
    allowed = profile.get("Entitlements", {})
    teams = profile.get("TeamIdentifier", [])
    prefixes = profile.get("ApplicationIdentifierPrefix", [])
    if len(teams) != 1 or len(prefixes) != 1 or allowed.get("com.apple.developer.team-identifier") != teams[0]:
        raise SystemExit("The Apple development profile has no unambiguous team and application prefix.")
    if allowed.get("get-task-allow") is not True or not profile.get("ProvisionedDevices"):
        raise SystemExit("Use an Apple development profile that permits debugging and lists your iPad.")
    expires = profile.get("ExpirationDate")
    if not expires or expires <= now:
        raise SystemExit("The Apple development profile has expired.")
    app_id = f"{prefixes[0]}.{bundle_identifier}"
    permitted = allowed.get("application-identifier", "")
    if permitted != app_id and not (permitted.endswith(".*") and app_id.startswith(permitted[:-1])):
        raise SystemExit("The Apple development profile does not cover this app's bundle identifier.")
    return {"application-identifier": app_id, "com.apple.developer.team-identifier": teams[0],
            "get-task-allow": True}


def sign_app(app, profile_path, identity, bundle_identifier):
    profile_data = subprocess.check_output(["security", "cms", "-D", "-i", profile_path])
    profile = plistlib.loads(profile_data)
    now = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)
    entitlements = signing_entitlements(profile, bundle_identifier, now)
    shutil.copyfile(profile_path, app / "embedded.mobileprovision")
    entitlements_path = app.parent / "development-entitlements.plist"
    entitlements_path.write_bytes(plistlib.dumps(entitlements))
    sign = ["codesign", "--force", "--sign", identity, "--timestamp=none"]
    frameworks = app / "Frameworks"
    for nested in sorted(frameworks.iterdir()):
        if nested.suffix in {".framework", ".dylib"}:
            subprocess.run([*sign, nested], check=True)
    subprocess.run([*sign, "--entitlements", entitlements_path, app], check=True)
    subprocess.run(["codesign", "--verify", "--deep", "--strict", app], check=True)
    certificate_prefix = app.parent / "certificate-"
    subprocess.run(["codesign", "--display", f"--extract-certificates={certificate_prefix}", app], check=True)
    leaf = certificate_prefix.with_name(certificate_prefix.name + "0").read_bytes()
    if leaf not in profile.get("DeveloperCertificates", []):
        raise SystemExit("The signing certificate is not authorized by the Apple development profile.")
    actual = plistlib.loads(subprocess.check_output(["codesign", "--display", "--entitlements", "-", app]))
    if any(actual.get(key) != value for key, value in entitlements.items()):
        raise SystemExit("The packaged app does not have the required development entitlements.")


def package(app, output, profile_path=None, identity=None, bundle_identifier=None):
    info_bytes = (app / "Info.plist").read_bytes()
    info = plistlib.loads(info_bytes)
    if info.get("STS2ExtensionFixtures"):
        raise SystemExit("The extension test fixture cannot be packaged.")
    if (app / "_CodeSignature").exists():
        raise SystemExit("Use the unsigned build output for this packaging step.")

    frameworks = app / "Frameworks"
    extensions = any((frameworks / f"{name}.framework").exists() for name in (FMOD_NAME, SPINE_NAME))
    report = validate(app, extensions=extensions, fixtures=False)
    if profile_path:
        with tempfile.TemporaryDirectory(prefix="sts2-sign-") as temporary:
            signed_app = pathlib.Path(temporary) / app.name
            shutil.copytree(app, signed_app)
            if bundle_identifier:
                info["CFBundleIdentifier"] = bundle_identifier
                info_bytes = plistlib.dumps(info)
                (signed_app / "Info.plist").write_bytes(info_bytes)
            sign_app(signed_app, profile_path, identity, info["CFBundleIdentifier"])
            write_ipa(signed_app, output, info_bytes, extensions, signed=True)
    else:
        write_ipa(app, output, info_bytes, extensions, signed=False)
    mode = "development-signed" if profile_path else "unsigned"
    print(f"Verified {mode} {report['architecture']} IPA ({output.stat().st_size:,} bytes): {output}")


def write_ipa(app, output, info_bytes, extensions, signed):
    info = plistlib.loads(info_bytes)
    prefix = f"Payload/{app.name}/"
    executables = [info["CFBundleExecutable"], "Frameworks/sts2.framework/sts2"]
    executables.extend(f"Frameworks/{path.name}" for path in (app / "Frameworks").glob("*.dylib"))
    if extensions:
        executables.extend(f"Frameworks/{name}.framework/{name}" for name in (FMOD_NAME, SPINE_NAME))

    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(".ipa.tmp")
    try:
        with zipfile.ZipFile(temporary, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=3) as archive:
            for path in sorted(app.rglob("*")):
                if path.is_file():
                    archive.write(path, prefix + path.relative_to(app).as_posix())
        with zipfile.ZipFile(temporary) as archive:
            if archive.testzip() or archive.read(prefix + "Info.plist") != info_bytes:
                raise SystemExit("The IPA archive failed integrity validation.")
            if signed:
                for member in ("embedded.mobileprovision", "_CodeSignature/CodeResources"):
                    if archive.getinfo(prefix + member).file_size == 0:
                        raise SystemExit("The IPA is missing development signing data.")
            for executable in executables:
                member = archive.getinfo(prefix + executable)
                if member.file_size == 0 or not (member.external_attr >> 16) & stat.S_IXUSR:
                    raise SystemExit(f"Missing executable permissions in the IPA: {executable}")
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path("dist/STS2IOSLauncher.ipa"))
    parser.add_argument("--profile", type=pathlib.Path, help="Apple development provisioning profile for the test iPad")
    parser.add_argument("--identity", help="matching Apple Development signing identity in the macOS Keychain")
    parser.add_argument("--bundle-identifier", help="bundle identifier covered by the development profile")
    args = parser.parse_args()
    if bool(args.profile) != bool(args.identity):
        parser.error("--profile and --identity must be supplied together")
    if args.bundle_identifier and (not args.profile or not re.fullmatch(r"[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", args.bundle_identifier)):
        parser.error("--bundle-identifier requires development signing and a reverse DNS identifier")
    package(args.app.resolve(), args.output.resolve(), args.profile, args.identity, args.bundle_identifier)
