# Sts2IOSLauncher

Experimental iPadOS port of [Sts2MobileLauncher](https://github.com/kollier-p/Sts2MobileLauncher). The target is an 11-inch iPad Pro running iPadOS 17.6.3 with JIT enabled.

The UIKit launcher supports Steam Guard, Keychain sessions, branch and manifest selection, resumable downloads of the owned Windows depot, ZIP import, and switching verified installations. The ARM64 build stages the patched Mono runtime inside Godot and stops at an unsigned `.app`. Live Steam authentication, on-device launch, and gameplay need device validation. Game files come from the user's Steam account or their own Windows archive.

Run the core tests with .NET 8:
    dotnet build src/Launcher.Steam/Launcher.Steam.csproj -c Release
    dotnet run --project tests/Launcher.Core.Tests/Launcher.Core.Tests.csproj -c Release

The native build uses Godot 4.5.1, patched Mono 9.0.20, and Xcode. See [the pre-IPA build](docs/EXPERIMENTAL_IPA.md) for extension licensing and [the parity roadmap](docs/ROADMAP.md) for feature and verification status.
