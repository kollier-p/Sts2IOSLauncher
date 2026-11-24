#import <Foundation/Foundation.h>
#import <Security/Security.h>
#include <string.h>
static NSDictionary *query(NSString *service) {
    return @{(__bridge id)kSecClass:(__bridge id)kSecClassGenericPassword,
        (__bridge id)kSecAttrService:service,
        (__bridge id)kSecAttrAccount:@"session"};
}
static int writeValue(NSString *service, const char *json) {
    if(!json)return -1;
    NSData *data=[NSData dataWithBytes:json length:strlen(json)];
    if(data.length>65536)return -2;
    NSDictionary *value=@{(__bridge id)kSecValueData:data};
    OSStatus status=SecItemUpdate((__bridge CFDictionaryRef)query(service),(__bridge CFDictionaryRef)value);
    if(status==errSecItemNotFound){
        NSMutableDictionary *item=[query(service) mutableCopy];
        [item addEntriesFromDictionary:value];
        item[(__bridge id)kSecAttrAccessible]=(__bridge id)kSecAttrAccessibleWhenUnlockedThisDeviceOnly;
        status=SecItemAdd((__bridge CFDictionaryRef)item,nullptr);
    }
    return (int)status;
}
static int readValue(NSString *service, char *buffer, int capacity) {
    NSMutableDictionary *item=[query(service) mutableCopy];
    item[(__bridge id)kSecReturnData]=@YES;
    item[(__bridge id)kSecMatchLimit]=(__bridge id)kSecMatchLimitOne;
    CFTypeRef raw=nullptr;
    OSStatus status=SecItemCopyMatching((__bridge CFDictionaryRef)item,&raw);
    if(status!=errSecSuccess)return (int)status;
    NSData *data=CFBridgingRelease(raw);
    if(!buffer||capacity<=0||data.length>=(NSUInteger)capacity)return -1;
    memcpy(buffer,data.bytes,data.length);buffer[data.length]=0;return (int)data.length;
}
static int clearValue(NSString *service) {
    OSStatus status=SecItemDelete((__bridge CFDictionaryRef)query(service));
    return status==errSecItemNotFound?0:(int)status;
}

static NSString *const steamService=@"org.sts2mobile.ioslauncher.steam";
static NSString *const webDavService=@"org.sts2mobile.ioslauncher.webdav";

extern "C" __attribute__((visibility("default"))) int sts2_vault_write(const char *json) { return writeValue(steamService, json); }
extern "C" __attribute__((visibility("default"))) int sts2_vault_read(char *buffer, int capacity) { return readValue(steamService, buffer, capacity); }
extern "C" __attribute__((visibility("default"))) int sts2_vault_clear() { return clearValue(steamService); }
extern "C" __attribute__((visibility("default"))) int sts2_webdav_write(const char *json) { return writeValue(webDavService, json); }
extern "C" __attribute__((visibility("default"))) int sts2_webdav_read(char *buffer, int capacity) { return readValue(webDavService, buffer, capacity); }
extern "C" __attribute__((visibility("default"))) int sts2_webdav_clear() { return clearValue(webDavService); }
