# Sts2IOSLauncher

Experimental iPadOS port of [Sts2MobileLauncher](https://github.com/kollier-p/Sts2MobileLauncher). Target device: iPad Pro 11-inch A1980, iPadOS 17.6.1, user-reported jailbreak and working JIT.

Uncompiled Stage 1 prototype. SteamKit2 3.5.0 authentication and authorized Windows depot downloads, PCK processing, payload installation and UIKit host. No game files or credentials included. The Godot/Mono runtime, actual process relaunch, complete UI parity and resumable downloads are pending. No working IPA yet.

Run the core tests with .NET 8:
    dotnet build src/Launcher.Steam/Launcher.Steam.csproj -c Release
    dotnet run --project tests/Launcher.Core.Tests/Launcher.Core.Tests.csproj -c Release

The UIKit host targets .NET 10 iOS (ARM64, iPadOS 16+), and requires matching Xcode and iOS workload. The interpreter is a launcher-only spike, not a game runtime. SteamKit2 is LGPL-2.1-only; comply with its licence for binary distribution. Original project MIT licence retained. See docs/ROADMAP.md.
