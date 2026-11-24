# Experimental unsigned IPA

Use Actions > Build IPA > Run workflow. The macOS 26 Apple Silicon runner installs the .NET 10 iOS workload and uses its default Xcode. This is an initial toolchain choice, not yet a verified compatibility pair. The first run may require pinning the SDK, workload and Xcode together.

The build runs .NET 8 core tests first, builds the launcher with signing disabled, packages the app and scans filenames for known game assets and nested archives. The scan is a guardrail, not a proof that arbitrary binaries contain no game data. No Steam account, Apple signing certificate or game files are needed in CI.

Manual builds upload the IPA for seven days. Version tags of the form v1.2.3 also publish a prerelease after the build passes. Do not tag until device validation and dependency licensing review are complete. AltStore metadata and icons are deferred until a real app icon and tested installation exist. Unsigned does not mean directly installable on stock iPadOS. The launcher is not yet a playable game runtime.
