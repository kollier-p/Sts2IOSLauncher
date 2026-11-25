# ARM64 IPA builds

Open **Actions → Build IPA → Run workflow** to build the Godot launcher and package it as an unsigned IPA. With **include_extensions** off, the workflow uploads `STS2IOSLauncher-diagnostic-ipa`. It exercises launcher workflows on a JIT-enabled device. Starting the game requires the FMOD and Spine frameworks.

With **include_extensions** on, the workflow builds both frameworks and verifies the unsigned IPA on the runner. The downloadable verification report includes archive size and checksum. The framework binaries and extension-enabled IPA remain on the runner.

For an installable development IPA, follow the local signing procedure with a profile that includes the target iPad and permits debugging. Check the extension licenses before sharing the signed build. [Native iOS build and signing](EXPERIMENTAL_IPA.md)
