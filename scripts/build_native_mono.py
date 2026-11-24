#!/usr/bin/env python3
"""Build pinned ARM64 Mono runtime, without game assets."""
import pathlib, subprocess, os, shutil, argparse
ROOT = pathlib.Path(__file__).resolve().parents[1]
WORK = ROOT / ".native-build"
REV = "3879076d9a06ce098d37c3882fb1845a6627335b"
def run(*args, cwd=None):
    subprocess.run([str(x) for x in args], cwd=cwd, check=True)
def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--patched', action='store_true')
    args = parser.parse_args()
    WORK.mkdir(exist_ok=True)
    source = WORK / ("runtime-patched" if args.patched else "runtime")
    if not source.exists():
        run("git", "clone", "--depth", "1", "--branch", "v9.0.20", "https://github.com/dotnet/runtime.git", source)
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    if actual != REV:
        raise SystemExit("Unexpected Mono source revision")
    if args.patched:
        patch = ROOT / "native/mono/mono-ios-jit.patch"
        reverse = subprocess.run(["git", "apply", "--reverse", "--check", str(patch)], cwd=source, capture_output=True)
        if reverse.returncode:
            run("git", "apply", "--check", patch, cwd=source)
            run("git", "apply", patch, cwd=source)
        shutil.copyfile(ROOT / "native/mono/MonoJitMemory.h", source / "src/mono/mono/utils/sts2-ios-jit-memory.h")
    generated = source / "artifacts/obj"
    generated.mkdir(parents=True, exist_ok=True)
    (generated / "runtime_version.h").write_text("\n".join(
        f"#define Runtime{kind}{part} {value}" for kind, parts in (
            ("File", (("MajorVersion",9),("MinorVersion",0),("BuildVersion",20),("RevisionVersion",0))),
            ("Product", (("MajorVersion",9),("MinorVersion",0),("PatchVersion",20))))
        for part,value in parts) + "\n")
    (generated / "_version.c").write_text(f'static char sccsid[] __attribute__((used)) = "@(#)Version 9.0.20 @Commit: {REV}";\n')
    build = WORK / ("mono-patched" if args.patched else "mono")
    run("cmake", "-S", source / "src/mono", "-B", build, "-G", "Ninja",
        "-DCMAKE_SYSTEM_NAME=iOS", "-DCMAKE_OSX_ARCHITECTURES=arm64",
        "-DCMAKE_OSX_DEPLOYMENT_TARGET=16.0", "-DCMAKE_BUILD_TYPE=Release",
        "-DCLR_CMAKE_HOST_ARCH=arm64", "-DDISABLE_JIT=OFF", "-DDISABLE_AOT=ON",
        "-DDISABLE_REFLECTION_EMIT=OFF", "-DDISABLE_EXECUTABLES=ON",
        "-DDISABLE_EVENTPIPE=ON", "-DDISABLE_DEBUGGER_AGENT=ON", "-DDISABLE_LLDB=ON",
        "-DSTATIC_COMPONENTS=ON", "-DDISABLE_LINK_STATIC_COMPONENTS=ON",
        "-DDISABLE_SHARED_LIBS=ON", "-DENABLE_LAZY_GC_THREAD_CREATION=OFF",
        "-DENABLE_ICALL_EXPORT=ON", "-DGC_SUSPEND=preemptive")
    run("cmake", "--build", build, "-j", str(min(os.cpu_count() or 2,8)), "--target",
        "monosgen-static", "mono-component-marshal-ilgen-static",
        "mono-component-debugger-stub-static", "mono-component-hot_reload-stub-static",
        "mono-component-diagnostics_tracing-stub-static")
    for lib in (build / "mono/mini").glob("*.a"):
        run("xcrun", "lipo", "-verify_arch", "arm64", lib)
    if not (build / "mono/mini/libmonosgen-2.0.a").is_file():
        raise SystemExit("Mono archive missing")
if __name__ == "__main__":
    main()
