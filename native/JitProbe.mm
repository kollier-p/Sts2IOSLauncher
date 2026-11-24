#import <Foundation/Foundation.h>
#include <sys/mman.h>
#include <libkern/OSCacheControl.h>
#include <unistd.h>
#include <stdint.h>
#include "mono/MonoJitMemory.h"

// Test actual execution and the same alias mechanism used by patched Mono.
// Call only after the platform has granted executable-memory permission.
extern "C" int sts2_probe_jit(void) {
    size_t size = (size_t)getpagesize();
    void *page = mmap(nullptr, size, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANON, -1, 0);
    if (page == MAP_FAILED) return 1;
    uint32_t *code = (uint32_t *)page;
    code[0] = 0x52800540; // mov w0, #42
    code[1] = 0xd65f03c0; // ret
    if (mprotect(page, size, PROT_READ | PROT_EXEC) != 0) {
        munmap(page, size); return 2;
    }
    sys_icache_invalidate(page, 8);
    int value = ((int (*)())page)();
    munmap(page, size);
    return value == 42 ? 0 : 3;
}
