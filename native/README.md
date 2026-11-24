# Native runtime integration

The native framework is built separately from the managed launcher. The host must read the launch request before starting Godot, initialize the patched Mono runtime with the iOS BCL and installed game assemblies, then allow Godot to call godotsharp_game_main_init.

The launch request reader validates path containment but is not a substitute for the installer's cryptographic verification. A production host must revalidate the selected payload at startup and consume the request atomically. The iOS CoreLib and Harmony adapters, native Godot export, platform extensions, and device JIT activation remain required. Do not package this framework into the existing .NET UIKit process and initialize a second Mono runtime.
