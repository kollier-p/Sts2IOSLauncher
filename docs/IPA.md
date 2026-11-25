# ARM64 IPA builds

Open **Actions → Build IPA → Run workflow** to build the Godot launcher and package it as an unsigned IPA. With **include_extensions** off, the workflow uploads `STS2IOSLauncher-diagnostic-ipa`. It exercises launcher workflows on a JIT-enabled device. Starting the game requires the FMOD and Spine frameworks.

Enable **include_extensions** and **upload_extension_ipa** to publish the unsigned IPA with FMOD and Spine as `STS2IOSLauncher-unsigned-ipa`. The verification report includes the IPA size and SHA-256 checksum. Enable the upload for builds cleared for distribution.

Extract `STS2IOSLauncher.ipa` from the artifact ZIP and sign it using your sideloading setup. For local development signing, use a profile that includes the target iPad and permits debugging. [Native iOS build and signing](EXPERIMENTAL_IPA.md)
