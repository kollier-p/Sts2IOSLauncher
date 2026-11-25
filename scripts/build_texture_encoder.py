#!/usr/bin/env python3
"""Build the pinned ASTC encoder for the iOS runtime or local tests."""

import argparse
import concurrent.futures
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tarfile

ROOT = Path(__file__).resolve().parents[1]
ARM_REVISION = "bf32abd05eccaf3042170b2a85cebdf0bfee5873"


def fetch(folder, name, url, expected_sha256):
    path = folder / name
    if not path.exists():
        download = path.with_suffix(".partial")
        subprocess.run(["curl", "-fsSL", "--retry", "4", "--max-time", "90", "-o", download, url], check=True)
        download.replace(path)
    with path.open("rb") as source:
        if hashlib.file_digest(source, "sha256").hexdigest() != expected_sha256:
            raise RuntimeError(f"Checksum mismatch: {path}")
    return path


def build(folder, host):
    folder.mkdir(parents=True, exist_ok=True)
    archive = fetch(folder, "astcenc.tar.gz",
                    f"https://codeload.github.com/ARM-software/astc-encoder/tar.gz/{ARM_REVISION}",
                    "c3563b1c8839c9e737ebe1085f4b0bc0da4189fb19d498309d2735a90de9f8df")
    bcdec = fetch(folder, "bcdec.h", "https://raw.githubusercontent.com/godotengine/godot/4.5.1-stable/thirdparty/misc/bcdec.h",
                  "08b361b4c9516f0ce366386d9f12482a92e4335ca625886a4687b2cdb4a7f5a6")
    source = folder / "astcenc"
    source.mkdir(exist_ok=True)
    with tarfile.open(archive) as compressed:
        for member in compressed:
            _, _, name = member.name.partition("/")
            if not member.isfile() or (name != "LICENSE.txt" and not name.startswith("Source/astcenc")):
                continue
            with compressed.extractfile(member) as original, (source / Path(name).name).open("wb") as output:
                shutil.copyfileobj(original, output)

    if host:
        compiler = [os.environ.get("CXX", "c++"), "-fPIC"]
    else:
        sdk = subprocess.check_output(["xcrun", "--sdk", "iphoneos", "--show-sdk-path"], text=True).strip()
        compiler = ["xcrun", "--sdk", "iphoneos", "clang++", "-arch", "arm64", "-miphoneos-version-min=16.0",
                    "-isysroot", sdk]
    compiler += ["-std=c++17", "-O2", "-ffp-contract=off", "-fvisibility=hidden", "-fvisibility-inlines-hidden",
                 f"-DASTCENC_NEON={int(not host)}", "-DASTCENC_SVE=0", "-DASTCENC_SSE=0", "-DASTCENC_AVX=0",
                 "-DASTCENC_POPCNT=0", "-DASTCENC_F16C=0", f"-I{source}", f"-I{bcdec.parent}"]
    sources = [file for file in sorted(source.glob("astcenc_*.cpp")) if not file.stem.startswith("astcenccli")]
    sources.append(ROOT / "native/texture/TextureEncoder.cpp")

    def compile_file(file):
        obj = folder / f"{file.stem}.o"
        subprocess.run([*compiler, "-c", file, "-o", obj], check=True)
        return obj

    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        objects = list(pool.map(compile_file, sources))
    if host:
        subprocess.run([*compiler, "-shared", "-pthread", *objects, "-o", folder / "libtexture_encoder.so"], check=True)
    else:
        subprocess.run(["xcrun", "--sdk", "iphoneos", "libtool", "-static", "-o", folder / "libtexture_encoder.a", *objects], check=True)

    notices = folder / "notices"
    notices.mkdir(exist_ok=True)
    shutil.copy2(source / "LICENSE.txt", notices / "ASTC-Encoder-LICENSE.txt")
    shutil.copy2(ROOT / "native/texture/LICENSE.reference.txt", notices / "Reference-Encoder-LICENSE.txt")
    # Keep the original header and all of its attribution and license choices.
    shutil.copy2(bcdec, notices / "bcdec-LICENSE.txt")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    parser.add_argument("--host", action="store_true")
    args = parser.parse_args()
    build(args.output.resolve(), args.host)
