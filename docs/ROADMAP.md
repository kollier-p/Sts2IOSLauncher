# Roadmap and verification gates

Target device is a JIT-enabled 11-inch iPad Pro running iPadOS 17.6.3. Feature comparisons use [Sts2MobileLauncher](https://github.com/kollier-p/Sts2MobileLauncher).

| Android feature | iPadOS implementation and remaining work |
| --- | --- |
| Steam account and Guard | Password, mobile/email code, mobile approval, and Keychain session reuse are implemented. Verify with a live account on the iPad. |
| Steam game installation | Ownership, public and beta branches, exact manifest selection, resumable verified downloads, and version switching are implemented. Verify against the live Windows depot. |
| Archive import and profiles | Files supports ZIP, 7z, RAR, and plain or gzip-, bzip2-, xz-, and zstd-compressed TAR. Switch verified versions and remove inactive installations. Launch profiles support version pins, independent save and mod folders, rename, and removal. The remaining Android archive formats and live Files testing remain open. |
| Game startup | Patched ARM64 Mono, Godot handoff, and initial Harmony hooks are integrated. Launch preparation checks the game assembly's entry point and required profile hooks before starting the engine. Verify on device with licensed FMOD and Spine frameworks. |
| Compatibility and input | Local mod paths and initial platform hooks are implemented. Full compatibility pack, touch adaptation, controller behavior, rendering, and audio need device work. |
| Save management | Snapshots, verified restore, Android-format snapshot ZIP import and export, normal and modded slot transfer, manual Steam Cloud transfers, and WebDAV transfers are implemented. WebDAV can download before play and upload changes from the previous game session on the next launch. Background upload and device and provider validation remain open. |
| Mod management | Per-profile archive imports support manifest normalization, package replacement, enable and disable, and removal. Installed mods offers a global pause, individual module controls, presets, and manifest dependency details. Workshop supports public browsing and search, authorized SteamPipe manifests, author snapshots, branch selection, tracking installed listings, and a dependency queue for SteamPipe installs. Nexus supports recent and trending feeds, mod lookup, file selection, and authorized download links. Runtime mod loading and live download validation remain open. |
| Launcher utilities | The iPad interface groups Play, Install, Saves, Mods, and Tools. Tools provides Launcher and Godot logs, file browsing with sorting, previews and multiselect, release update checks, and frame-limit and touch-to-mouse settings. Extended input features and localization remain open. |

CI covers ARM64 compilation, linking, bundle staging, and unsigned IPA packaging. The extension-enabled evaluation IPA passed packaging on revision 79c095c. Test delivery requires authorization to distribute the extensions and a signing path. Steam sign-in, installation, and gameplay still need verification on a physical iPad.

Download checks validate full chunk layouts before cache reuse, retry corrupt chunks on alternate CDN servers, and honor worker cancellation between requests. The installer checks available space, accounting for partial data already on disk. Launch preparation checks space if it needs to copy the PCK. Offline fixtures cover these paths, with live CDN behavior still to verify.

Inactive installations with intact receipts remain selectable for removal even when required game files are missing. Activation verifies required files and inventory before switching. Regression coverage passed on revision 79c095c.

Launch preparation uses a cancellable buffered copy when filesystem cloning is unavailable. The host removes the disposable staging file on failure. Offline copy fixtures passed on revision 0dd26c3. The copy path still needs iPad testing.

Installation and version activation use the same cancellation-aware streaming SHA-256 implementation as inventory verification.
