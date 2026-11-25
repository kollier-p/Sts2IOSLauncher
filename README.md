# Sts2IOSLauncher

Experimental iPadOS port of [Sts2MobileLauncher](https://github.com/kollier-p/Sts2MobileLauncher). The target is an 11-inch iPad Pro running iPadOS 17.6.3 with JIT enabled.

The iPad launcher supports Steam Guard, saved Steam sessions, branch and manifest selection, verified game downloads, and game imports from ZIP, 7z, RAR, and supported TAR archives. Launch profiles can pin installed versions and isolate saves and mods. The launcher also provides save snapshots, manual Steam Cloud and WebDAV sync, mod management, Workshop and Nexus imports, and a log viewer.

The ARM64 build links Godot with patched Mono and verifies unsigned IPA packaging. Enabled extensions, signing, and an iPad are required for end-to-end launch testing. Supply game files through your Steam account or your own Windows archive.

On first Play for each installed version, the launcher can convert supported desktop textures to ASTC 4×4 for the iPad GPU. It shows progress, resumes after an interruption, and reuses the finished pack on later launches. Textures that fail the quality check stay in their original format. **Optimize textures for iPad** in Launch settings controls this step. The original installation stays available for Safe launch.

Run the core and host checks with .NET 9.

```sh
DOTNET_ROLL_FORWARD=Major dotnet run --project tests/Launcher.Core.Tests/Launcher.Core.Tests.csproj -c Release
dotnet run --project tests/Launcher.Host.Tests/Launcher.Host.Tests.csproj -c Release
```

The native build uses Godot 4.5.1, patched Mono 9.0.20, and Xcode. [Build and extension licensing](docs/EXPERIMENTAL_IPA.md) · [Feature and verification status](docs/ROADMAP.md)
