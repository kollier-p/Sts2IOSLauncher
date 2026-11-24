# Experimental single-runtime launcher

The Android launcher and its compatibility submodule are the feature reference. The first native host implements Steam installation, branch selection and a shared Mono process. The narrow MIT runtime adapters are attributed in their source folders.

## First-launch compatibility

The first compatibility hooks cover platform initialization, system diagnostics, telemetry opt-out, local mod paths and release information. The adapter checks the Harmony checksum and version before rewriting. Steamworks multiplayer, cloud, and Workshop require additional integration.

Launch profiles can isolate saves and mods independently and pin an installed game version. The default profile uses the game's existing save routing, the shared `Documents/mods` folder, and the global version. Isolated data lives under `Documents/LaunchProfiles`. Isolated saves require the game's three save path methods from the Android compatibility contract. Save snapshots use the profile's isolated saves or the shared `Documents/default/1` account directory. Restoring verifies the snapshot and backs up existing saves before switching directories.

The Saves section supports manual WebDAV upload and download over HTTPS. Enter the same remote slot on both launchers to share `SlayTheSpire2/saves/<slot>/`. Connection credentials are stored in Keychain. Sync compares file content, preserves normal and modded save trees, and backs up local data before applying a download. A conflict prompt asks which copy to keep. [Android save sync reference](https://github.com/kollier-p/Sts2MobileLauncher/blob/main/android/src/com/godot/game/webdav/WebDavSyncManager.java)

## Native dependencies

The launch copy of the PCK receives managed script placeholders and descriptors for the bundled FMOD and Spine frameworks. Extension-inclusive builds require the applicable licenses. Touch, controller, rendering and audio behavior must be verified on the target iPad.

## Build acceptance

1. Linux managed host compile.
2. ARM64 patched Mono framework and runtime pack stage.
3. Godot project export, ARM64 app link, and pre-IPA bundle validation.
4. Jailbroken iPad startup and JIT probe.
5. Live owned Steam download/install, then Godot startup.
6. Actual menu and combat play, saves, sound and touch.
