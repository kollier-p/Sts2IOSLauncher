# Roadmap and verification gates

Target device is a JIT-enabled 11-inch iPad Pro running iPadOS 17.6.3. Feature comparisons use [Sts2MobileLauncher](https://github.com/kollier-p/Sts2MobileLauncher).

| Android feature | iPadOS implementation and remaining work |
| --- | --- |
| Steam account and Guard | Password, mobile/email code, mobile approval, and Keychain session reuse are implemented. Verify with a live account on the iPad. |
| Steam game installation | Ownership, public and beta branches, exact manifest selection, resumable verified downloads, and version switching are implemented. Verify against the live Windows depot. |
| Archive import and profiles | Import a ZIP with one Windows installation from Files and switch verified versions. Additional archive formats and isolated save/mod profiles remain open. |
| Game startup | Patched ARM64 Mono, Godot handoff, and initial Harmony hooks are integrated. Verify on device with licensed FMOD and Spine frameworks. |
| Compatibility and input | Local mod paths and initial platform hooks are implemented. Full compatibility pack, touch adaptation, controller behavior, rendering, and audio need device work. |
| Launcher utilities | Persistent launcher diagnostics are implemented. Mod management, Workshop, Nexus, Steam Cloud, WebDAV, save snapshots, file browser, localization, and settings remain open. |

The current build gate is ARM64 compilation, linking, and pre-IPA staging in CI. Steam sign-in, installation and gameplay on a physical iPad are separate verification gates.

Download hardening: validate full chunk layouts before cache reuse, retry corrupt CDN chunks on alternate servers, and honor worker cancellation between CDN attempts. Offline fixture coverage added; live CDN behavior remains unverified. Remaining storage work includes free-space preflight and cleanup of inactive versions.
