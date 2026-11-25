#import <Foundation/Foundation.h>
#include <dlfcn.h>
#include <stdint.h>
#include <errno.h>
#include <libkern/OSCacheControl.h>
#include "mono/MonoJitMemory.h"

extern "C" int monovm_initialize(int, const char **, const char **);
extern "C" int monovm_create_delegate(const char *, const char *, const char *, void **);
extern "C" int sts2_probe_jit(void);

// Mono configures the loader here and starts the runtime on the first delegate request.
// Its CoreCLR compatibility API never assigns the host handle or domain ID.
static bool runtimeConfigured;

extern "C" __attribute__((visibility("default"))) int sts2_initialize_game_runtime(const char *bclPath, const char *gamePath, const char *nativePath) {
    if (runtimeConfigured) return 0;
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
    int result = monovm_initialize(sizeof(keys) / sizeof(keys[0]), keys, values);
    runtimeConfigured = result == 0;
    return result;
}

extern "C" __attribute__((visibility("default"))) bool godotsharp_game_main_init(void *godot, void *callbacks, void *interop, int size) {
    if (!runtimeConfigured) return false;
    bool (*entry)(void *, void *, void *, int) = nullptr;
    int result = monovm_create_delegate("sts2", "GodotPlugins.Game.Main", "InitializeFromGameProject", (void **)&entry);
    if(result != 0 || !entry || !entry(godot ?: dlopen(nullptr, RTLD_NOW), callbacks, interop, size))return false;
    int (*hooks)()=nullptr;
    result=monovm_create_delegate("Launcher.Host","Launcher.Host.Entry","StartGameHooks",(void**)&hooks);
    return result==0 && hooks && hooks()==0;
}

extern "C" __attribute__((visibility("default"))) int sts2_host_delegate(const char *method,void **entry) {
    if(!runtimeConfigured || !method || !entry) return -1;
    return monovm_create_delegate("Launcher.Host","Launcher.Host.Entry",method,entry);
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
