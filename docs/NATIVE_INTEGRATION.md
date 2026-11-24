# Experimental single-runtime launcher

The Android launcher and its compatibility submodule are the feature reference. The first native host implements Steam installation, branch selection and a shared Mono process. The narrow MIT runtime adapters are attributed in their source folders.

## First-launch compatibility

The first compatibility hooks mirror Android's platform initialization, system diagnostics, telemetry opt-out, local mod path and release information behaviour. Steamworks gameplay, cloud and Workshop are not replaced by these hooks. Exact method shapes are checked against the installed assembly. Unknown Harmony binaries are rejected by the adapter rather than blindly rewritten.

## Remaining native dependencies

The Godot shell is not yet a complete playable port. FMOD and Spine iOS extensions, game PCK managed script resource placeholders, native Steam substitutes, input/touch adjustments, full compatibility patches and device JIT validation remain necessary. The reference iOS port uses public and licensed native extension inputs; redistribution permissions and version compatibility must be verified before including those. No proprietary game assemblies are embedded in the IPA.

## Build acceptance

1. Linux managed host compile.
2. ARM64 patched Mono framework and runtime pack stage.
3. Godot export and unsigned IPA packaging.
4. Jailbroken iPad startup and JIT probe.
5. Live owned Steam download/install, then Godot startup.
6. Actual menu and combat play, saves, sound and touch.
