#!/usr/bin/env python3
"""Validate the staged iOS application without signing or packaging it."""

import argparse
import json
import pathlib
import plistlib
import re
import subprocess

from integrate_extensions import FMOD_NAME, SPINE_NAME


def inspect(*args):
    return subprocess.check_output(["xcrun", *map(str, args)], text=True)


def verify_device_binary(binary):
    if inspect("lipo", "-archs", binary).strip() != "arm64":
        raise SystemExit(f"Expected ARM64 only: {binary}")
    build = inspect("vtool", "-show-build", binary)
    if not re.search(r"^\s*platform\s+IOS\s*$", build, re.MULTILINE):
        raise SystemExit(f"Expected iPhoneOS device binary: {binary}")
    minimum = re.search(r"^\s*minos\s+(\d+)(?:\.(\d+))?", build, re.MULTILINE)
    if not minimum or (int(minimum[1]), int(minimum[2] or 0)) > (16, 0):
        raise SystemExit(f"{binary} exceeds the iOS 16.0 deployment target")


def verify_symbols(binary, *names):
    symbols = {line.split()[-1] for line in inspect("nm", "-gU", binary).splitlines() if line.split()}
    missing = set(names) - symbols
    if missing:
        raise SystemExit(f"Missing symbols in {binary}: {', '.join(sorted(missing))}")


def verify_framework(app, name, *symbols):
    bundle = app / "Frameworks" / f"{name}.framework"
    info = plistlib.loads((bundle / "Info.plist").read_bytes())
    executable = bundle / name
    if info["CFBundleExecutable"] != name or info["CFBundlePackageType"] != "FMWK":
        raise SystemExit(f"Invalid framework bundle: {bundle}")
    verify_device_binary(executable)
    if inspect("otool", "-D", executable).splitlines()[-1] != f"@rpath/{name}.framework/{name}":
        raise SystemExit(f"Unexpected install name: {executable}")
    verify_symbols(executable, *symbols)


def verify_linked_libraries(app, executable):
    frameworks = app / "Frameworks"
    images = [executable, *sorted(frameworks.glob("*.dylib"))]
    for framework in sorted(frameworks.glob("*.framework")):
        metadata = plistlib.loads((framework / "Info.plist").read_bytes())
        images.append(framework / metadata["CFBundleExecutable"])
    bundled = {image.resolve() for image in images}

    def expand(path, image):
        for prefix, directory in (("@loader_path", image.parent), ("@executable_path", executable.parent)):
            if path == prefix:
                return directory
            if path.startswith(prefix + "/"):
                return directory / path[len(prefix) + 1:]
        return pathlib.Path(path) if path.startswith("/") else None

    def runpaths(image):
        commands = inspect("otool", "-l", image)
        return [folder for path in re.findall(r"cmd LC_RPATH\s+cmdsize \d+\s+path (.+?) \(offset \d+\)", commands)
                if (folder := expand(path, image)) is not None]

    app_runpaths = runpaths(executable)
    for image in images:
        search = app_runpaths if image == executable else [*runpaths(image), *app_runpaths]
        install_names = [] if image == executable else inspect("otool", "-D", image).splitlines()[1:]
        for line in inspect("otool", "-L", image).splitlines()[1:]:
            dependency = line.strip().partition(" (compatibility version ")[0]
            if not dependency or dependency in install_names or dependency.startswith(("/usr/lib/", "/System/Library/Frameworks/")):
                continue
            if dependency.startswith("@rpath/"):
                relative = dependency[len("@rpath/"):]
                if any((folder / relative).resolve() in bundled for folder in search):
                    continue
                if re.fullmatch(r"libswift[A-Za-z0-9_]+\.dylib", relative) and pathlib.Path("/usr/lib/swift") in search:
                    continue
            else:
                target = expand(dependency, image)
                if target is not None and target.resolve() in bundled:
                    continue
            raise SystemExit(f"Unresolved bundled dependency for {image.name}: {dependency}")
    return len(images)


def validate(app, extensions, fixtures):
    info = plistlib.loads((app / "Info.plist").read_bytes())
    binary = app / info["CFBundleExecutable"]
    if bool(info.get("STS2ExtensionFixtures")) != fixtures:
        raise SystemExit("App extension fixture marker differs from the build mode")
    arguments = ["--main-pack", "user://StS2.pck", "--log-file", "user://LauncherLogs/godot.log"]
    if info.get("MinimumOSVersion") != "16.0" or info.get("godot_cmdline") != arguments:
        raise SystemExit("Godot launch arguments or deployment target differ from the launcher contract")
    verify_device_binary(binary)
    verify_symbols(binary, "_sts2_extensions_available", "_OBJC_CLASS_$_LauncherGate")
    verify_framework(app, "sts2", "_sts2_host_delegate", "_godotsharp_game_main_init",
                     "_sts2_initialize_game_runtime", "_sts2_jit_patch", "_sts2_vault_read",
                     "_sts2_vault_write", "_sts2_vault_clear", "_sts2_webdav_read", "_sts2_webdav_write", "_sts2_webdav_clear",
                     "_sts2_nexus_read", "_sts2_nexus_write", "_sts2_nexus_clear")
    for name in ("libSystem.Native.dylib", "libSystem.Globalization.Native.dylib",
                 "libSystem.IO.Compression.Native.dylib", "libSystem.Net.Security.Native.dylib",
                 "libSystem.Security.Cryptography.Native.Apple.dylib"):
        verify_device_binary(app / "Frameworks" / name)
    for name in ("Launcher.Host", "Launcher.Core", "Launcher.Steam", "Launcher.Adapters", "SteamKit2",
                 "System.Private.CoreLib", "STS2JitSupport", "protobuf-net", "protobuf-net.Core", "SharpCompress"):
        with (app / "Managed" / f"{name}.dll").open("rb") as assembly:
            if assembly.read(2) != b"MZ":
                raise SystemExit(f"Invalid managed assembly: {name}")
    if extensions or fixtures:
        verify_symbols(binary, "_load_all_fmod_plugins")
        verify_framework(app, FMOD_NAME, "_fmod_library_init", "_FMOD_System_Create", "_FMOD_Studio_System_Create")
        verify_framework(app, SPINE_NAME, "_spine_godot_library_init")
        fmod = app / "Frameworks" / f"{FMOD_NAME}.framework" / FMOD_NAME
        imports = inspect("nm", "-gu", fmod).split()
        if "_load_all_fmod_plugins" not in imports:
            raise SystemExit(f"Missing FMOD host callback import: {fmod}")
    elif any((app / "Frameworks" / f"{name}.framework").exists() for name in (FMOD_NAME, SPINE_NAME)):
        raise SystemExit("Unexpected extension frameworks in the diagnostic app")
    for item in app.rglob("*"):
        if item.name.lower() in {"sts2.dll", "0harmony.dll", "slaythespire2.pck", "release_info.json",
                                 "embedded.mobileprovision"} or item.is_symlink():
            raise SystemExit(f"Unexpected entry in pre-IPA app: {item}")
    linked_images = verify_linked_libraries(app, binary)
    return {"bundle": info["CFBundleIdentifier"], "minimum_os": info["MinimumOSVersion"],
            "extensions": extensions, "extension_fixtures": fixtures, "architecture": "arm64", "platform": "iPhoneOS",
            "linked_images": linked_images, "status": "compiled and staged; device untested"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=pathlib.Path)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--extensions", action="store_true")
    mode.add_argument("--diagnostic", action="store_true")
    mode.add_argument("--extension-fixtures", action="store_true")
    parser.add_argument("--report", type=pathlib.Path, default=pathlib.Path(".native-build/pre-ipa-report.json"))
    args = parser.parse_args()
    result = json.dumps(validate(args.app, args.extensions, args.extension_fixtures), indent=2)
    args.report.write_text(result + "\n")
    print(result)
