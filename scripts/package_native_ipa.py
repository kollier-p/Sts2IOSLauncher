#!/usr/bin/env python3
"""Create an unsigned device IPA from the validated Godot app bundle."""

import argparse
import pathlib
import plistlib
import stat
import zipfile

from integrate_extensions import FMOD_NAME, SPINE_NAME
from validate_native_app import validate


def package(app, output):
    info_bytes = (app / "Info.plist").read_bytes()
    info = plistlib.loads(info_bytes)
    if info.get("STS2ExtensionFixtures"):
        raise SystemExit("The extension test fixture cannot be packaged.")
    if (app / "_CodeSignature").exists():
        raise SystemExit("Use the unsigned build output for this packaging step.")

    frameworks = app / "Frameworks"
    extensions = any((frameworks / f"{name}.framework").exists() for name in (FMOD_NAME, SPINE_NAME))
    report = validate(app, extensions=extensions, fixtures=False)
    prefix = f"Payload/{app.name}/"
    executables = [info["CFBundleExecutable"], "Frameworks/sts2.framework/sts2"]
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
            for executable in executables:
                member = archive.getinfo(prefix + executable)
                if member.file_size == 0 or not (member.external_attr >> 16) & stat.S_IXUSR:
                    raise SystemExit(f"Missing executable permissions in the IPA: {executable}")
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)
    print(f"Verified unsigned {report['architecture']} IPA ({output.stat().st_size:,} bytes): {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=pathlib.Path)
    parser.add_argument("--output", type=pathlib.Path, default=pathlib.Path("dist/STS2IOSLauncher.ipa"))
    args = parser.parse_args()
    package(args.app.resolve(), args.output.resolve())
