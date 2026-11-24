# Native iOS build and validation

The automatic build stops at a validated unsigned `.app`. Use the diagnostic build to test startup, JIT, and Steam downloads. Play requires an extension-enabled build.

To build with FMOD and Spine after confirming the applicable licenses, use Actions > Native Godot pre-IPA build > Run workflow > include_extensions, or set the repository Actions variable INCLUDE_NATIVE_EXTENSIONS=true to enable that build on pushes. The repository does not include game files or redistribute extension source archives itself. Check the applicable FMOD and Spine terms before sharing an extension-inclusive binary.

The extension-enabled build stages ARM64 FMOD and Spine frameworks inside the `.app`. At launch the verified Steam pack is cloned or copied, C# script placeholders are added, and its descriptors point Godot to those frameworks. The workflow validates the extension-enabled app on the runner and keeps the extension binaries out of public artifacts.

The automatic build uploads the diagnostic `.app` as a tar archive. IPA packaging is a separate step and is outside the pre-IPA workflow.

Automatic CI also builds an app with two small test libraries to check the extension framework layout, install names and exported symbols. The test app disables Play and stays on the runner. Its validation report accompanies the diagnostic app artifact.

On the iPad, sideload/sign the IPA with the installed jailbreak-compatible method, launch and enable JIT if needed, sign in to Steam, select a branch and download, then select Play installed game. Device launch and gameplay are not yet verified. Report the exact build SHA, iPad model/OS and the logs under Documents/LauncherLogs. Do not share credentials or tokens.

Known remaining limits: actual game/API compatibility across branches, extension linkage and redistribution authorization, Steam networking/cloud/workshop, save/profile selection, Android touch and UI patches, and real device testing. A successful CI compilation is not a gameplay result.
