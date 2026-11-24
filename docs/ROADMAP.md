# Roadmap and verification gates

Target device is a JIT-enabled 11-inch iPad Pro running iPadOS 17.6.3. Feature comparisons use [Sts2MobileLauncher](https://github.com/kollier-p/Sts2MobileLauncher).

| Android feature | iPadOS implementation and remaining work |
| --- | --- |
| Steam account and Guard | Password, mobile/email code, mobile approval, and Keychain session reuse are implemented. Verify with a live account on the iPad. |
| Steam game installation | Ownership, public and beta branches, exact manifest selection, resumable verified downloads, and version switching are implemented. Verify against the live Windows depot. |
| Archive import and profiles | Import ZIP, TAR, or gzip-compressed TAR from Files. Switch verified versions and remove inactive installations. Launch profiles can pin an installed version and isolate saves, mods, or both. Additional archive formats remain open. |
| Game startup | Patched ARM64 Mono, Godot handoff, and initial Harmony hooks are integrated. Verify on device with licensed FMOD and Spine frameworks. |
| Compatibility and input | Local mod paths and initial platform hooks are implemented. Full compatibility pack, touch adaptation, controller behavior, rendering, and audio need device work. |
| Save management | Manual snapshots, verified restore, WebDAV push and pull, and Steam Cloud upload and download are implemented. Automatic sync and live provider validation remain open. |
| Launcher utilities | The iPad interface groups Play, Install, and Saves. Persistent diagnostics are implemented. Mod management, Workshop, Nexus, file browser, localization, and settings remain open. |

The pre-IPA CI gate covers ARM64 compilation, linking and bundle staging; packaging is separate. Readiness for device testing also requires an authorized distribution and signing path. Steam sign-in, installation, and gameplay on a physical iPad are separate verification gates.

Download checks validate full chunk layouts before cache reuse, retry corrupt chunks on alternate CDN servers, and honor worker cancellation between requests. The installer checks available space, accounting for partial data already on disk. Launch preparation checks space if it needs to copy the PCK. Offline fixtures cover these paths, with live CDN behavior still to verify.

Inactive installations with intact receipts remain selectable for removal even when required game files are missing; activation still validates required files and inventory before switching. This permits storage recovery without deleting the active installation. Regression coverage passed on revision 79c095c.

Launch preparation uses a cancellable buffered copy when filesystem cloning is unavailable. The original verified pack remains untouched, and the host removes the disposable staging file on failure. Offline copy fixtures passed on revision 0dd26c3; actual iPad filesystem behavior remains unverified.

Installation and version activation now use the same cancellation-aware streaming SHA-256 implementation as inventory verification, rather than an uninterrupted full-pack hash. Cancellation regression checks are pending CI.
