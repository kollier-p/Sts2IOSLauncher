# Native IPA test workflow

The automatic build is a diagnostic IPA without restricted third-party native game extensions. It supports startup, JIT and Steam download testing; play intentionally reports that the extension-enabled build is required.

To build with FMOD and Spine after confirming the applicable licenses, use Actions > Native Godot IPA (experimental) > Run workflow > include_extensions, or set the repository Actions variable INCLUDE_NATIVE_EXTENSIONS=true to enable that build on pushes. The repository does not include game files or redistribute extension source archives itself. Check the applicable FMOD and Spine terms before sharing an extension-inclusive binary.

The complete build attempts static extension linking and registers the symbols in Godot's AppleEmbedded table. At launch the verified Steam pack is cloned or copied, C# script placeholders added, and the two descriptors rewritten to select the static libraries. No Windows native library is loaded.

On the iPad, sideload/sign the IPA with the installed jailbreak-compatible method, launch and enable JIT if needed, sign in to Steam, select a branch and download, then select Play installed game. Device launch and gameplay are not yet verified. Report the exact build SHA, iPad model/OS and the logs under Documents/LauncherLogs. Do not share credentials or tokens.

Known remaining limits: actual game/API compatibility across branches, extension linkage and redistribution authorization, Steam networking/cloud/workshop, save/profile selection, Android touch and UI patches, and real device testing. A successful CI compilation is not a gameplay result.
