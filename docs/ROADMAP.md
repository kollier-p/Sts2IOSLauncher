# Roadmap and verification gates

Target: iPad Pro A1980, iPadOS 17.6.1, user-reported Dopamine JIT. Rootless/jailbreak details and process relaunch need confirmation. Original Android repository remains untouched.

1. Steam authentication, Guard email/mobile code and confirmation, Keychain token, entitlement, public branch/manifest, chunk/file verification, processing, staged install and persisted receipt. Test on device and in offline fixtures. Add resume, concurrency, storage preflight, branch list and true process relaunch. Current prototype only resets the UIKit controller.
2. Runtime spike: Godot 4.5-compatible ARM64 engine and Mono, actual dynamic assembly loading and Harmony patches, native libraries, Metal, audio, touch and controller. Stock iOS C# NativeAOT is not sufficient for arbitrary downloaded assemblies. Test boot, saves and gameplay on physical iPad.
3. Full UI/feature parity: dashboard, settings, archive import, version/profiles, mods and compatibility pack, Workshop, Nexus, Steam Cloud/WebDAV, saves and snapshots, file browser, logs, localization, input and overlays.
4. macOS/Xcode CI, unsigned IPA artifacts, device installation/JIT verification. Use user-provided IPA workflow example before settling build configuration.

No live Steam login or compiler/device test has passed yet. Keep proprietary game files, tokens and credentials out of this public repository.
