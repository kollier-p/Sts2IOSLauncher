// Godot 4.5.1 AppleEmbedded static extension symbol map.
#include <dlfcn.h>
#include <stdio.h>
extern void register_dynamic_symbol(char *,void *);
extern "C" unsigned char fmod_library_init(void*,void*,void*);
extern "C" unsigned char spine_godot_library_init(void*,void*,void*);
void sts2_register_extensions() {
    static bool registered=false;
    if(registered)return;
    static char fmod[]="fmod_library_init",spine[]="spine_godot_library_init";
    register_dynamic_symbol(fmod,(void*)&fmod_library_init);
    register_dynamic_symbol(spine,(void*)&spine_godot_library_init);
    registered=true;
}
extern "C" __attribute__((visibility("default"),used)) unsigned int *load_all_fmod_plugins(void *, unsigned int *count) {
    if(count)*count=0;return nullptr;
}

extern "C" __attribute__((visibility("default"),used)) int sts2_extensions_available(){return 1;}
