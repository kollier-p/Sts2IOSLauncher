#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <string.h>
static NSDictionary *query() {
    return @{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService:@"org.sts2mobile.ioslauncher.steam",
        (__bridge id)kSecAttrAccount:@"session"};
}
extern "C" __attribute__((visibility("default"))) int sts2_vault_write(const char *json) {
    if(!json)return -1;
    NSData *data=[NSData dataWithBytes:json length:strlen(json)];
    if(data.length>65536)return -2;
    NSDictionary *value=@{(__bridge id)kSecValueData:data};
    OSStatus status=SecItemUpdate((__bridge CFDictionaryRef)query(),(__bridge CFDictionaryRef)value);
    if(status==errSecItemNotFound){
        NSMutableDictionary *item=[query() mutableCopy];
        [item addEntriesFromDictionary:value];
        item[(__bridge id)kSecAttrAccessible]=(__bridge id)kSecAttrAccessibleWhenUnlockedThisDeviceOnly;
        status=SecItemAdd((__bridge CFDictionaryRef)item,nullptr);
    }
    return (int)status;
}
extern "C" __attribute__((visibility("default"))) int sts2_vault_read(char *buffer,int capacity) {
    NSMutableDictionary *item=[query() mutableCopy];
    item[(__bridge id)kSecReturnData]=@YES;
    item[(__bridge id)kSecMatchLimit]=(__bridge id)kSecMatchLimitOne;
    CFTypeRef raw=nullptr;
    OSStatus status=SecItemCopyMatching((__bridge CFDictionaryRef)item,&raw);
    if(status!=errSecSuccess)return (int)status;
    NSData *data=CFBridgingRelease(raw);
    if(!buffer||capacity<=0||data.length>=(NSUInteger)capacity)return -1;
    memcpy(buffer,data.bytes,data.length);buffer[data.length]=0;return (int)data.length;
}
extern "C" __attribute__((visibility("default"))) int sts2_vault_clear() {
    OSStatus status=SecItemDelete((__bridge CFDictionaryRef)query());
    return status==errSecItemNotFound?0:(int)status;
}
