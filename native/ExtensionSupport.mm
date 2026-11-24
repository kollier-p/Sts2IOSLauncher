// The FMOD GDExtension calls this hook to load optional audio plugins.
extern "C" __attribute__((visibility("default"), used)) unsigned int *load_all_fmod_plugins(
        void *, unsigned int *count) {
    if (count) *count = 0;
    return nullptr;
}

extern "C" __attribute__((visibility("default"), used)) int sts2_extensions_available() {
#ifdef STS2_EXTENSION_FIXTURES
    return 0;
#else
    return 1;
#endif
}
