"""Build small test libraries with the public extension entry-point names."""

import subprocess

from integrate_extensions import FMOD_NAME, SPINE_SOURCE


SOURCES = {
    "fmod": """
extern unsigned int *load_all_fmod_plugins(void *, unsigned int *);
unsigned char fmod_library_init(void *get_proc, void *library, void *init) {
    unsigned int count;
    load_all_fmod_plugins(library, &count);
    return 0;
}
int FMOD_System_Create(void **system, unsigned int version) { return 1; }
int FMOD_Studio_System_Create(void **system, unsigned int version) { return 1; }
""",
    "spine": """
unsigned char spine_godot_library_init(void *get_proc, void *library, void *init) { return 0; }
""",
}


def build_extension_fixtures(directory):
    sdk = subprocess.check_output(["xcrun", "--sdk", "iphoneos", "--show-sdk-path"], text=True).strip()
    binaries = {
        "fmod": directory / "fmod" / f"{FMOD_NAME}.dylib",
        "spine": directory / "spine" / f"{SPINE_SOURCE}.framework" / SPINE_SOURCE,
    }
    for name, binary in binaries.items():
        binary.parent.mkdir(parents=True, exist_ok=True)
        source = binary.parent / f"{name}_fixture.c"
        source.write_text(SOURCES[name])
        flags = ["-Wl,-undefined,dynamic_lookup"] if name == "fmod" else []
        subprocess.run([
            "xcrun", "--sdk", "iphoneos", "clang", "-dynamiclib", "-arch", "arm64",
            "-miphoneos-version-min=16.0", "-isysroot", sdk, "-Wl,-headerpad_max_install_names",
            *flags, "-o", binary, source,
        ], check=True)
