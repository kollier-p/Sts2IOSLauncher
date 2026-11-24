#!/usr/bin/env python3
"""Stage the iOS native extension frameworks and link the host callback."""

import argparse
import pathlib
import plistlib
import re
import shutil
import subprocess

FMOD_NAME = "Sts2Fmod"
SPINE_NAME = "Sts2Spine"
SPINE_SOURCE = "libspine_godot.ios.template_release"


def link_host_callback(project):
    pbx = project / "project.pbxproj"
    flags = " -Wl,-u,_load_all_fmod_plugins"
    contents, count = re.subn(
        r'(OTHER_LDFLAGS = ")([^"]*)(";)',
        lambda match: match[1] + match[2] + flags + match[3],
        pbx.read_text(),
    )
    if not count:
        raise SystemExit("Unrecognized Xcode linker settings")
    pbx.write_text(contents)


def embed_framework(frameworks, name, source, identifier, version):
    description = subprocess.check_output(["file", "-b", source], text=True)
    if "dynamically linked shared library" not in description:
        raise SystemExit(f"Expected a Mach-O dynamic library: {source}")
    subprocess.run(["xcrun", "lipo", "-verify_arch", "arm64", source], check=True)
    bundle = frameworks / f"{name}.framework"
    bundle.mkdir(parents=True, exist_ok=True)
    executable = bundle / name
    shutil.copy2(source, executable)
    executable.chmod(0o755)
    subprocess.run(["xcrun", "install_name_tool", "-id", f"@rpath/{name}.framework/{name}", executable], check=True)
    (bundle / "Info.plist").write_bytes(plistlib.dumps({
        "CFBundleExecutable": name,
        "CFBundleIdentifier": f"org.sts2mobile.ioslauncher.{identifier}",
        "CFBundlePackageType": "FMWK",
        "CFBundleVersion": "1",
        "CFBundleShortVersionString": version,
        "MinimumOSVersion": "16.0",
    }))


def embed_extensions(frameworks, extensions, *, fixtures=False):
    embed_framework(frameworks, FMOD_NAME, extensions / "fmod" / f"{FMOD_NAME}.dylib", "fmod", "6.1.0")
    spine = extensions / "spine" / f"{SPINE_SOURCE}.framework" / SPINE_SOURCE
    embed_framework(frameworks, SPINE_NAME, spine, "spine", "4.2.0")
    if not fixtures:
        shutil.copy2(extensions / "spine" / "LICENSE", frameworks / f"{SPINE_NAME}.framework" / "Spine-LICENSE.txt")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("extensions", type=pathlib.Path)
    parser.add_argument("frameworks", type=pathlib.Path)
    args = parser.parse_args()
    embed_extensions(args.frameworks, args.extensions)
