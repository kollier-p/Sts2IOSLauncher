# Native Mono compatibility dependency

The three low-level Mono patch and memory alias files are reused under MIT from https://github.com/kollier-p/StS2-Launcher-iOS/tree/77c45949825c7c7f99774b5ac06c96d37d0b31e7/ios/jit. The upstream copyright and license are retained. This is a narrow runtime dependency, not a reuse of the reference launcher, UI, game hooks or Steam implementation. The patch targets the pinned .NET 9.0.20 revision. Compilation alone does not establish executable memory support on the target device.
