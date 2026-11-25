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
static NSString *const nexusService=@"org.sts2mobile.ioslauncher.nexus";

// Some sideloaded apps lack Keychain access. Keep the Steam token in app-private
// Application Support with iOS Data Protection and exclude it from backups.
static NSURL *steamSessionFile(bool create) {
    NSFileManager *files = NSFileManager.defaultManager;
    NSURL *support = [files URLsForDirectory:NSApplicationSupportDirectory inDomains:NSUserDomainMask].firstObject;
    NSURL *directory = [support URLByAppendingPathComponent:@"SteamSession" isDirectory:YES];
    if (create && (!directory || ![files createDirectoryAtURL:directory withIntermediateDirectories:YES
        attributes:@{NSFilePosixPermissions:@0700, NSFileProtectionKey:NSFileProtectionComplete} error:nullptr] ||
        ![directory setResourceValue:@YES forKey:NSURLIsExcludedFromBackupKey error:nullptr])) return nil;
    return [directory URLByAppendingPathComponent:@"login.json"];
}

static bool missingFile(NSError *error) {
    return [error.domain isEqualToString:NSCocoaErrorDomain] &&
        (error.code == NSFileNoSuchFileError || error.code == NSFileReadNoSuchFileError);
}

static int clearSteamSessionFile() {
    NSURL *url = steamSessionFile(false);
    NSError *error = nil;
    return !url || [NSFileManager.defaultManager removeItemAtURL:url error:&error] || missingFile(error) ? 0 : -1;
}

extern "C" __attribute__((visibility("default"))) int sts2_vault_write(const char *json) {
    int result = writeValue(steamService, json);
    if (result == 0) {
        clearSteamSessionFile();
        return 0;
    }
    if (result != errSecMissingEntitlement) return result;
    NSURL *url = steamSessionFile(true);
    if (!url) return -1;
    NSData *data = [NSData dataWithBytes:json length:strlen(json)];
    return [data writeToURL:url options:NSDataWritingAtomic | NSDataWritingFileProtectionComplete error:nullptr] ? 0 : -1;
}

extern "C" __attribute__((visibility("default"))) int sts2_vault_read(char *buffer, int capacity) {
    int result = readValue(steamService, buffer, capacity);
    if (result != errSecItemNotFound && result != errSecMissingEntitlement) return result;
    NSURL *url = steamSessionFile(false);
    if (!url) return errSecItemNotFound;
    NSError *error = nil;
    NSData *data = [NSData dataWithContentsOfURL:url options:0 error:&error];
    if (!data) return missingFile(error) ? errSecItemNotFound : -1;
    if (!buffer || capacity <= 0 || data.length >= (NSUInteger)capacity) return -1;
    memcpy(buffer, data.bytes, data.length);
    buffer[data.length] = 0;
    return (int)data.length;
}

extern "C" __attribute__((visibility("default"))) int sts2_vault_clear() {
    int result = clearValue(steamService);
    int fileResult = clearSteamSessionFile();
    return result != errSecMissingEntitlement && result != 0 ? result : fileResult;
}
extern "C" __attribute__((visibility("default"))) int sts2_webdav_write(const char *json) { return writeValue(webDavService, json); }
extern "C" __attribute__((visibility("default"))) int sts2_webdav_read(char *buffer, int capacity) { return readValue(webDavService, buffer, capacity); }
extern "C" __attribute__((visibility("default"))) int sts2_webdav_clear() { return clearValue(webDavService); }
extern "C" __attribute__((visibility("default"))) int sts2_nexus_write(const char *json) { return writeValue(nexusService, json); }
extern "C" __attribute__((visibility("default"))) int sts2_nexus_read(char *buffer, int capacity) { return readValue(nexusService, buffer, capacity); }
extern "C" __attribute__((visibility("default"))) int sts2_nexus_clear() { return clearValue(nexusService); }
