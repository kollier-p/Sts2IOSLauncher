# Experimental single-runtime launcher

The Android launcher and its compatibility submodule are the feature reference. The first native host implements Steam installation, branch selection and a shared Mono process. The narrow MIT runtime adapters are attributed in their source folders.

## First-launch compatibility

The compatibility hooks cover platform initialization, system diagnostics, telemetry opt-out, local mod paths and release information. Launch preparation checks the game assembly for the managed entry point and required hook signatures. The adapter verifies the Harmony checksum and version before rewriting. Steamworks multiplayer remains open.

Launch profiles can isolate saves and mods independently and pin an installed game version. The default profile uses the game's existing save routing, the shared `Documents/mods` folder, and the global version. Isolated data lives under `Documents/LaunchProfiles`. Isolated saves require the game's three save path methods from the Android compatibility contract. Save snapshots use the profile's isolated saves or the shared `Documents/default/1` account directory. Restoring verifies the snapshot and backs up existing saves before switching directories.

The Saves section supports WebDAV upload and download over HTTPS. Enter the same remote slot on both launchers to share `SlayTheSpire2/saves/<slot>/`. Connection credentials are stored in Keychain. Optional automatic sync checks before play and can upload changes from the previous session. Strong server ETags let the launcher reuse known checksums for unchanged files. Sync preserves normal and modded save trees and backs up local data before applying a download. A conflict prompt asks which copy to keep. [Android save sync reference](https://github.com/kollier-p/Sts2MobileLauncher/blob/main/android/src/com/godot/game/webdav/WebDavSyncManager.java)

The **Steam** tab manages sign-in and sign-out. The first sign-in saves a refresh token for game installation, Steam Cloud and Workshop across app restarts. The launcher uses Keychain or app-private iOS Data Protection storage when the app has no Keychain entitlement. **Mods → Steam Workshop** opens the Workshop browser in the launcher. Its sidebar switches between public listings, search, subscriptions and installed items. Installed badges follow the active profile, including verified folders named by Workshop ID. The browser returns to the same results after each installation and offers the installed game’s branch first.

The same save conflict checks apply to manual Steam Cloud sync. Downloads show file and byte progress, then back up and apply the verified saves. Steam Cloud preserves normal and modded save paths.

**Tools → Game launch settings → Startup render warmup** is optional. Godot draws selected card materials, UI textures, and canvas shaders in a small offscreen viewport over several frames when the main menu is ready. The installed renderer builds its own pipelines on the device. Compare cold launches on the same iPad to measure the effect.

## Native dependencies

The launch copy of the PCK receives managed script placeholders and descriptors for the bundled FMOD and Spine frameworks. Extension-inclusive builds require the applicable licenses. Touch, controller, rendering and audio behavior must be verified on the target iPad.

## Build acceptance

1. Linux managed host compile.
2. ARM64 patched Mono framework and runtime pack stage.
3. Godot project export, ARM64 app link, dependency validation, and unsigned IPA packaging.
4. Install a signed test build and run the JIT probe on the iPad.
5. Sign in to Steam, download an owned build, and start Godot.
6. Verify menus, combat, saves, sound, and touch on the iPad.
