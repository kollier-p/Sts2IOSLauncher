#import <Foundation/Foundation.h>
#include <dlfcn.h>
#include <stdint.h>
#include <errno.h>
#include <libkern/OSCacheControl.h>
#include "mono/MonoJitMemory.h"

extern "C" int coreclr_initialize(const char *, const char *, int, const char **, const char **, void **, unsigned int *);
extern "C" int coreclr_create_delegate(void *, unsigned int, const char *, const char *, const char *, void **);
extern "C" int sts2_probe_jit(void);

static void *runtime;
static unsigned int domain;

// Native game process only: never call from the managed UIKit launcher.
extern "C" __attribute__((visibility("default"))) int sts2_initialize_game_runtime(const char *bclPath, const char *gamePath, const char *nativePath) {
    if (runtime) return 0;
    if (!bclPath || !nativePath) return -1;
    if (sts2_probe_jit() != 0) return -2;
    NSString *bcl = [NSString stringWithUTF8String:bclPath];
    NSString *game = gamePath ? [NSString stringWithUTF8String:gamePath] : bcl;
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
    if (!assemblies[@"system.private.corelib.dll"]) return -5;
    NSString *tpa = [assemblies.allValues componentsJoinedByString:@":"];
    NSString *paths = [@[game,bcl] componentsJoinedByString:@":"];
    setenv("MONO_THREADS_SUSPEND", "preemptive", 1);
    const char *keys[] = {"TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS", "APP_CONTEXT_BASE_DIRECTORY", "NATIVE_DLL_SEARCH_DIRECTORIES", "System.Globalization.Invariant", "System.Diagnostics.Tracing.EventSource.IsSupported"};
    const char *values[] = {tpa.UTF8String, paths.UTF8String, game.UTF8String, nativePath, "false", "false"};
    return coreclr_initialize(nullptr, "STS2Game", 6, keys, values, &runtime, &domain);
}

extern "C" __attribute__((visibility("default"))) bool godotsharp_game_main_init(void *godot, void *callbacks, void *interop, int size) {
    if (!runtime) return false;
    bool (*entry)(void *, void *, void *, int) = nullptr;
    int result = coreclr_create_delegate(runtime, domain, "sts2", "GodotPlugins.Game.Main", "InitializeFromGameProject", (void **)&entry);
    if(result != 0 || !entry || !entry(godot ?: dlopen(nullptr, RTLD_NOW), callbacks, interop, size))return false;
    int (*hooks)()=nullptr;
    result=coreclr_create_delegate(runtime,domain,"Launcher.Host","Launcher.Host.Entry","StartGameHooks",(void**)&hooks);
    return result==0 && hooks && hooks()==0;
}

extern "C" __attribute__((visibility("default"))) int sts2_host_delegate(const char *method,void **entry) {
    if(!runtime || !method || !entry) return -1;
    return coreclr_create_delegate(runtime,domain,"Launcher.Host","Launcher.Host.Entry",method,entry);
}
extern "C" __attribute__((visibility("default"))) int sts2_jit_patch(void *target,const void *source,int size,void *backup,int backupSize) {
    if(!target||!source||size<=0)return EINVAL;
    void *alias=sts2_jit_writable(target,size);
    if(alias==target)return ENOTSUP;
    if(backup&&backupSize>=size)memcpy(backup,target,size);
    memcpy(alias,source,size);
    sys_icache_invalidate(target,size);
    return 0;
}
