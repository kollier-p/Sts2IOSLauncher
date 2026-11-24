#import <Foundation/Foundation.h>
extern "C" int sts2_initialize_game_runtime(const char *, const char *, const char *);

static BOOL inside(NSString *path, NSString *root) {
    NSString *p = path.stringByResolvingSymlinksInPath.stringByStandardizingPath;
    NSString *r = root.stringByResolvingSymlinksInPath.stringByStandardizingPath;
    return [p hasPrefix:[r stringByAppendingString:@"/"]];
}

// Invoked before Godot initializes its managed extension, in the native host.
extern "C" NSDictionary *sts2_read_launch_request(NSString *requestPath, NSString *payloadRoot, NSString *saveRoot, NSError **error) {
    NSData *data = [NSData dataWithContentsOfFile:requestPath options:0 error:error];
    if (!data || data.length > 65536) return nil;
    id object = [NSJSONSerialization JSONObjectWithData:data options:0 error:error];
    if (![object isKindOfClass:NSDictionary.class]) return nil;
    NSDictionary *request = object;
    if (![request[@"Schema"] isEqual:@1]) return nil;
    for (NSString *key in @[@"PayloadId",@"Pack",@"Assemblies",@"GameAssembly",@"UserData"])
        if (![request[key] isKindOfClass:NSString.class]) return nil;
    NSString *payload = [payloadRoot stringByAppendingPathComponent:[@"payload-" stringByAppendingString:request[@"PayloadId"]]];
    if (!inside(payload,payloadRoot)) return nil;
    for (NSString *key in @[@"Pack",@"Assemblies",@"GameAssembly"])
        if (!inside(request[key],payload) || ![[NSFileManager defaultManager] fileExistsAtPath:request[key]]) return nil;
    if (!inside(request[@"UserData"],saveRoot)) return nil;
    if (![request[@"GameAssembly"] isEqual:[request[@"Assemblies"] stringByAppendingPathComponent:@"sts2.dll"]]) return nil;
    return request;
}
extern "C" int sts2_start_requested_runtime(NSDictionary *request, NSString *bcl, NSString *frameworks) {
    if (!request) return -10;
    return sts2_initialize_game_runtime(bcl.UTF8String, [request[@"Assemblies"] UTF8String], frameworks.UTF8String);
}
