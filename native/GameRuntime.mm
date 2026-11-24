#import <Foundation/Foundation.h>
#include <dlfcn.h>
#include <stdint.h>

extern "C" int coreclr_initialize(const char *, const char *, int, const char **, const char **, void **, unsigned int *);
extern "C" int coreclr_create_delegate(void *, unsigned int, const char *, const char *, const char *, void **);
extern "C" int sts2_probe_jit(void);

static void *runtime;
static unsigned int domain;

// Native game process only: never call from the managed UIKit launcher.
extern "C" int sts2_initialize_game_runtime(const char *bclPath, const char *gamePath, const char *nativePath) {
    if (runtime) return 0;
    if (!bclPath || !gamePath || !nativePath) return -1;
    if (sts2_probe_jit() != 0) return -2;
    NSString *bcl = [NSString stringWithUTF8String:bclPath];
    NSString *game = [NSString stringWithUTF8String:gamePath];
    if (!bcl || !game) return -3;
    NSMutableDictionary<NSString *, NSString *> *assemblies = [NSMutableDictionary dictionary];
    for (NSString *directory in @[game, bcl]) {
        NSError *error = nil;
        NSArray<NSString *> *files = [[NSFileManager defaultManager] contentsOfDirectoryAtPath:directory error:&error];
        if (!files) return -4;
        for (NSString *name in files)
            if ([name.pathExtension.lowercaseString isEqualToString:@"dll"])
                assemblies[name.lowercaseString] = [directory stringByAppendingPathComponent:name];
    }
    if (!assemblies[@"sts2.dll"] || !assemblies[@"system.private.corelib.dll"]) return -5;
    NSString *tpa = [assemblies.allValues componentsJoinedByString:@":"];
    NSString *paths = [@[game,bcl] componentsJoinedByString:@":"];
    const char *keys[] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS", "APP_CONTEXT_BASE_DIRECTORY", "NATIVE_DLL_SEARCH_DIRECTORIES"};
    const char *values[] = {tpa.UTF8String, paths.UTF8String, game.UTF8String, nativePath};
    return coreclr_initialize(nullptr, "STS2Game", 4, keys, values, &runtime, &domain);
}

extern "C" __attribute__((visibility("default"))) bool godotsharp_game_main_init(void *godot, void *callbacks, void *interop, int size) {
    if (!runtime) return false;
    bool (*entry)(void *, void *, void *, int) = nullptr;
    int result = coreclr_create_delegate(runtime, domain, "sts2", "GodotPlugins.Game.Main", "InitializeFromGameProject", (void **)&entry);
    return result == 0 && entry && entry(godot ?: dlopen(nullptr, RTLD_NOW), callbacks, interop, size);
}
