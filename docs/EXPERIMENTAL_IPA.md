# Native iOS build and validation

Automatic CI validates the ARM64 iOS app and unsigned IPA packaging. The diagnostic build exercises launcher startup, JIT, and Steam downloads. Play requires an extension-enabled build.

To build with FMOD and Spine after confirming the applicable licenses, use Actions > Native Godot pre-IPA build > Run workflow > include_extensions, or set the repository Actions variable INCLUDE_NATIVE_EXTENSIONS=true to enable that build on pushes. The repository does not include game files or redistribute extension source archives itself. Check the applicable FMOD and Spine terms before sharing an extension-inclusive binary.

The extension-enabled build stages ARM64 FMOD and Spine frameworks inside the `.app`. At launch the verified Steam pack is cloned or copied, C# script placeholders are added, and its descriptors point Godot to those frameworks. The bundle includes the Spine runtime license and the launcher About screen credits FMOD.

The automatic build uploads the diagnostic `.app` as a tar archive and checks IPA packaging on the runner. **Build IPA** uploads the unsigned diagnostic IPA by default. With `include_extensions` enabled, it verifies the IPA with both native extensions and uploads a checksum report. Enable `upload_extension_ipa` on the same run to publish the extension-enabled IPA as a downloadable artifact.

Automatic CI also builds an app with two small test libraries to check the extension framework layout, install names and exported symbols. The test app disables Play and stays on the runner. Its validation report accompanies the diagnostic app artifact.

For local development signing, use an Apple Development certificate in your Mac's Keychain and a provisioning profile that covers the bundle identifier and registers the test iPad. The packaging script verifies the profile and signed debugger entitlement before writing the IPA.

```sh
python3 scripts/package_native_ipa.py \
  .native-build/licensed-app/godot-derived/Build/Products/Release-iphoneos/Sts2Host.app \
  --profile path/to/Development.mobileprovision \
  --identity 'Apple Development: Your Name' \
  --bundle-identifier org.example.sts2ioslauncher
```

Install the IPA on the registered iPad, enable JIT using the device's existing setup, sign in to Steam, download a game branch, and select Play. Include the build SHA, iPad model and OS, and the log from Tools when reporting device results. Keep Steam and Nexus credentials private.
