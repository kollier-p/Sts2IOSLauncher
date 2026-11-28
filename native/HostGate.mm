#import <UIKit/UIKit.h>
#import <AVFoundation/AVFoundation.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#import <ImageIO/ImageIO.h>
#import <objc/runtime.h>
#include <dlfcn.h>
#include <unistd.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include "LauncherFiles.mm"

extern "C" int csops(pid_t,unsigned int,void*,size_t);
extern "C" int sts2_extensions_available();
using InitRuntime = int (*)(const char*,const char*,const char*);
using GetDelegate = int (*)(const char*,void**);
using InitHost = int (*)(const char*,const char*,const char*);
using Command = int (*)(const char*);
using KeyCommand = int (*)(const char*,int);
using Reply = int (*)(const char*,int64_t);
using Simple = int (*)();
using Status = int (*)(char*,int);
enum class ArchivePicker { Game, Mod, Snapshot };
static BOOL (*godotLaunch)(id,SEL,UIApplication*,NSDictionary*);
static NSMutableDictionary<NSString*,NSValue*> *lifecycle;
enum class GameState { Ready, Running, Closing, Closed };
static GameState gameState = GameState::Ready;
static UIWindow *gameWindow;
static void (*audioInterruption)(id,SEL,NSNotification *);
static InitHost initHost;
static Command beginDownload, beginImport, beginModImport, beginSnapshotImport;
static Reply reply;
static Simple browseVersions, browseProfiles, browseSnapshots, browseWebDav, browseSteamCloud, browseMods, browseWorkshop, browseNexus,
    browseLaunchOptions, cancelOperation, prepareGame, prepareSafeGame, signInSteam, forgetSession;
static Simple beginGameBackgroundSync, gameBackgroundSyncActive, cancelGameBackgroundSync;
static Simple releaseGameKeys, reportGameMemoryWarning, prepareGameShutdown, gameDidQuit;
static KeyCommand sendGameKey;
static UIBackgroundTaskIdentifier gameSyncTask=UIBackgroundTaskInvalid;
static NSTimer *gameSyncTimer;
static Status copyStatus;

static NSString *startManagedLauncher(NSString *library, NSString *managed, NSString *frameworks,
                                     NSString *payload, NSString *documents) {
    void *lib = dlopen(library.fileSystemRepresentation, RTLD_NOW | RTLD_GLOBAL);
    if (!lib) return [NSString stringWithFormat:@"Could not load the Mono framework (%s).", dlerror() ?: "unknown error"];

    auto init = (InitRuntime)dlsym(lib, "sts2_initialize_game_runtime");
    auto get = (GetDelegate)dlsym(lib, "sts2_host_delegate");
    if (!init || !get) return @"The Mono framework is missing a launcher entry point.";

    int result = init(managed.UTF8String, nullptr, frameworks.UTF8String);
    if (result == -2) return @"The launcher’s JIT execution check failed. Enable JIT for this app and reopen it.";
    if (result != 0) return [NSString stringWithFormat:@"Mono configuration failed (%d).", result];

    const struct { const char *name; void **target; } bindings[] = {
        {"Initialize", (void **)&initHost},
        {"BeginDownload", (void **)&beginDownload},
        {"BeginImport", (void **)&beginImport},
        {"BrowseVersions", (void **)&browseVersions},
        {"BrowseProfiles", (void **)&browseProfiles},
        {"BrowseSnapshots", (void **)&browseSnapshots},
        {"BeginSnapshotImport", (void **)&beginSnapshotImport},
        {"BrowseWebDav", (void **)&browseWebDav},
        {"BrowseSteamCloud", (void **)&browseSteamCloud},
        {"BrowseMods", (void **)&browseMods},
        {"BeginModImport", (void **)&beginModImport},
        {"BrowseWorkshop", (void **)&browseWorkshop},
        {"BrowseNexus", (void **)&browseNexus},
        {"BrowseLaunchOptions", (void **)&browseLaunchOptions},
        {"BeginGameBackgroundSync", (void **)&beginGameBackgroundSync},
        {"GameBackgroundSyncActive", (void **)&gameBackgroundSyncActive},
        {"CancelGameBackgroundSync", (void **)&cancelGameBackgroundSync},
        {"Reply", (void **)&reply},
        {"Cancel", (void **)&cancelOperation},
        {"ForgetSession", (void **)&forgetSession},
        {"SignInSteam", (void **)&signInSteam},
        {"Prepare", (void **)&prepareGame},
        {"PrepareSafe", (void **)&prepareSafeGame},
        {"SendGameKey", (void **)&sendGameKey},
        {"ReleaseGameKeys", (void **)&releaseGameKeys},
        {"ReportGameMemoryWarning", (void **)&reportGameMemoryWarning},
        {"PrepareGameShutdown", (void **)&prepareGameShutdown},
        {"GameDidQuit", (void **)&gameDidQuit},
        {"CopyStatus", (void **)&copyStatus},
    };
    for (const auto &binding : bindings) {
        result = get(binding.name, binding.target);
        if (result != 0 || !*binding.target)
            return [NSString stringWithFormat:@"Could not bind Launcher.Host.%s (0x%08x).", binding.name, (unsigned)result];
    }

    result = initHost(payload.UTF8String, documents.UTF8String, managed.UTF8String);
    return result == 0 ? nil : [NSString stringWithFormat:@"Managed launcher startup failed (%d).", result];
}

// Mono writes its native crash reports and unhandled exceptions to stderr, and
// nothing on the device keeps it. Send stdout and stderr to LauncherLogs so the
// next launch can show why the previous session ended.
static void captureConsole(NSString *documents) {
    static bool captured;
    if (captured) return;
    NSString *directory = [documents stringByAppendingPathComponent:@"LauncherLogs"];
    if (!directory || ![[NSFileManager defaultManager] createDirectoryAtPath:directory
        withIntermediateDirectories:YES attributes:nil error:nullptr]) return;
    NSString *path = [directory stringByAppendingPathComponent:@"console.log"];
    NSString *previous = [directory stringByAppendingPathComponent:@"console.log.previous"];
    [[NSFileManager defaultManager] removeItemAtPath:previous error:nullptr];
    [[NSFileManager defaultManager] moveItemAtPath:path toPath:previous error:nullptr];
    int file = open(path.fileSystemRepresentation, O_WRONLY | O_CREAT | O_TRUNC | O_APPEND, 0600);
    if (file < 0) return;
    fflush(stdout);
    fflush(stderr);
    dup2(file, STDOUT_FILENO);
    dup2(file, STDERR_FILENO);
    close(file);
    setvbuf(stdout, nullptr, _IOLBF, 0);
    setvbuf(stderr, nullptr, _IONBF, 0);
    captured = true;
    fprintf(stderr, "console capture started %s\n", NSDate.date.description.UTF8String);
}

static void recordStartupStatus(NSString *documents, NSString *status) {
    NSString *directory = [documents stringByAppendingPathComponent:@"LauncherLogs"];
    if (!directory || ![[NSFileManager defaultManager] createDirectoryAtPath:directory
        withIntermediateDirectories:YES attributes:nil error:nullptr]) return;
    NSString *revision = [NSBundle.mainBundle objectForInfoDictionaryKey:@"STS2GitRevision"] ?: @"unknown";
    NSString *log = [NSString stringWithFormat:@"%@\nBuild %@\n%@\n", NSDate.date, revision, status];
    [log writeToFile:[directory stringByAppendingPathComponent:@"startup.log"] atomically:YES
            encoding:NSUTF8StringEncoding error:nullptr];
}

@interface LauncherChoices : UITableViewController<NSURLSessionTaskDelegate>
@property(nonatomic,copy) NSArray<NSDictionary *> *choices;
@property(nonatomic,copy) NSString *selectedValue;
@property(nonatomic,assign) BOOL embedded;
@property(nonatomic,copy) void (^selectValue)(NSString *value);
@property(nonatomic,copy) void (^cancelSelection)(void);
@property(nonatomic,strong) NSURLSession *imageSession;
@property(nonatomic,strong) NSCache<NSURL *,UIImage *> *images;
@end

@implementation LauncherChoices
- (void)viewDidLoad {
    [super viewDidLoad];
    self.tableView.rowHeight=UITableViewAutomaticDimension;
    self.tableView.estimatedRowHeight=56;
    if(!self.embedded)
        self.navigationItem.leftBarButtonItem = [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemCancel
            target:self action:@selector(cancelPressed)];
}
- (void)viewDidDisappear:(BOOL)animated {
    [super viewDidDisappear:animated];
    [self.imageSession invalidateAndCancel];
    self.imageSession=nil;
}
static BOOL workshopImageURL(NSURL *url) {
    if(![url.scheme.lowercaseString isEqualToString:@"https"] || url.user.length || url.password.length ||
        (url.port && url.port.integerValue!=443))return NO;
    NSString *host=url.host.lowercaseString;
    return [host hasSuffix:@".steamusercontent.com"] || [host hasSuffix:@".steamstatic.com"] || [host hasSuffix:@".akamaihd.net"];
}
- (void)URLSession:(NSURLSession *)session task:(NSURLSessionTask *)task willPerformHTTPRedirection:(NSHTTPURLResponse *)response
    newRequest:(NSURLRequest *)request completionHandler:(void (^)(NSURLRequest *))completionHandler {
    completionHandler(workshopImageURL(request.URL)?request:nil);
}
- (NSInteger)tableView:(UITableView *)tableView numberOfRowsInSection:(NSInteger)section {
    return self.choices.count;
}
- (UITableViewCell *)tableView:(UITableView *)tableView cellForRowAtIndexPath:(NSIndexPath *)indexPath {
    UITableViewCell *cell = [tableView dequeueReusableCellWithIdentifier:@"Choice"];
    if (!cell) cell = [[UITableViewCell alloc] initWithStyle:UITableViewCellStyleSubtitle reuseIdentifier:@"Choice"];
    NSDictionary *choice = self.choices[indexPath.row];
    NSString *value=choice[@"Value"];
    BOOL selectable=value.length && ![value isEqualToString:@"loading"];
    cell.selectionStyle=selectable?UITableViewCellSelectionStyleDefault:UITableViewCellSelectionStyleNone;
    cell.textLabel.text = choice[@"Title"];
    cell.textLabel.numberOfLines=2;
    cell.detailTextLabel.text = choice[@"Detail"];
    cell.detailTextLabel.numberOfLines=0;
    cell.accessoryType=[self.selectedValue isEqual:choice[@"Value"]]?UITableViewCellAccessoryCheckmark:UITableViewCellAccessoryNone;
    NSString *badge=[choice[@"Badge"] isKindOfClass:NSString.class]?choice[@"Badge"]:nil;
    cell.accessoryView=nil;
    NSNumber *isOn=[choice[@"IsOn"] isKindOfClass:NSNumber.class]?choice[@"IsOn"]:nil;
    if(isOn && value.length){
        UISwitch *toggle=[UISwitch new];
        toggle.on=isOn.boolValue;
        toggle.accessibilityLabel=choice[@"Title"];
        __weak LauncherChoices *weakList=self;
        [toggle addAction:[UIAction actionWithHandler:^(UIAction *action){
            UISwitch *sender=(UISwitch *)action.sender;
            LauncherChoices *list=weakList;
            if(!list.selectValue)return;
            sender.enabled=NO;
            list.selectValue([NSString stringWithFormat:@"%@:%@",sender.on?@"enable":@"disable",value]);
        }] forControlEvents:UIControlEventValueChanged];
        [toggle sizeToFit];
        cell.accessoryView=toggle;
    }else if(badge.length){
        UIButtonConfiguration *style=UIButtonConfiguration.tintedButtonConfiguration;
        style.title=badge;
        style.buttonSize=UIButtonConfigurationSizeMini;
        style.baseForegroundColor=[badge isEqualToString:@"Installed"]?UIColor.systemGreenColor:UIColor.secondaryLabelColor;
        UIButton *pill=[UIButton buttonWithConfiguration:style primaryAction:nil];
        pill.userInteractionEnabled=NO;
        pill.isAccessibilityElement=NO;
        [pill sizeToFit];
        cell.accessoryView=pill;
    }
    cell.accessibilityLabel=badge.length?[NSString stringWithFormat:@"%@, %@, %@",choice[@"Title"],badge,choice[@"Detail"] ?: @""]:nil;
    cell.imageView.image=nil;
    NSString *thumb=[choice[@"Thumbnail"] isKindOfClass:NSString.class]?choice[@"Thumbnail"]:nil;
    NSURL *imageURL=thumb?[NSURL URLWithString:thumb]:nil;
    if(workshopImageURL(imageURL)) [self loadThumbnail:imageURL at:indexPath in:cell];
    return cell;
}
- (void)loadThumbnail:(NSURL *)url at:(NSIndexPath *)indexPath in:(UITableViewCell *)cell {
    if(!self.images){self.images=[NSCache new];self.images.countLimit=64;}
    UIImage *cached=[self.images objectForKey:url];
    if(cached){cell.imageView.image=cached;return;}
    cell.imageView.image=[UIImage systemImageNamed:@"photo"];
    if(!self.imageSession){
        NSURLSessionConfiguration *configuration=NSURLSessionConfiguration.ephemeralSessionConfiguration;
        configuration.HTTPShouldSetCookies=NO;
        configuration.timeoutIntervalForRequest=15;
        self.imageSession=[NSURLSession sessionWithConfiguration:configuration delegate:self delegateQueue:nil];
    }
    __weak LauncherChoices *weakSelf=self;
    [[self.imageSession dataTaskWithURL:url completionHandler:^(NSData *data,NSURLResponse *response,NSError *error){
        if(error || data.length==0 || data.length>2*1024*1024 || !workshopImageURL(response.URL))return;
        CGImageSourceRef source=CGImageSourceCreateWithData((__bridge CFDataRef)data,nullptr);
        if(!source)return;
        NSDictionary *options=@{(__bridge NSString *)kCGImageSourceCreateThumbnailFromImageAlways:@YES,
            (__bridge NSString *)kCGImageSourceThumbnailMaxPixelSize:@80};
        CGImageRef preview=CGImageSourceCreateThumbnailAtIndex(source,0,(__bridge CFDictionaryRef)options);
        CFRelease(source);
        if(!preview)return;
        UIImage *image=[UIImage imageWithCGImage:preview scale:2.0 orientation:UIImageOrientationUp];
        CGImageRelease(preview);
        dispatch_async(dispatch_get_main_queue(),^{
            LauncherChoices *list=weakSelf;
            [list.images setObject:image forKey:url];
            UITableViewCell *visible=[list.tableView cellForRowAtIndexPath:indexPath];
            if(visible && indexPath.row<list.choices.count &&
               [list.choices[indexPath.row][@"Thumbnail"] isEqual:url.absoluteString]){
                visible.imageView.image=image;
                [visible setNeedsLayout];
            }
        });
    }] resume];
}
- (void)tableView:(UITableView *)tableView didSelectRowAtIndexPath:(NSIndexPath *)indexPath {
    [tableView deselectRowAtIndexPath:indexPath animated:YES];
    NSString *value=self.choices[indexPath.row][@"Value"];
    if(value.length && ![value isEqualToString:@"loading"] && self.selectValue) self.selectValue(value);
}
- (void)cancelPressed { self.cancelSelection(); }
@end

#include "LauncherWorkshop.mm"
#include "LauncherInstalledMods.mm"
#include "LauncherGameKeys.mm"

@interface LauncherDiagnostics : UIViewController
@property(nonatomic,copy) NSURL *directory;
@property(nonatomic,strong) UITextView *output;
@property(nonatomic,strong) UISegmentedControl *source;
@end

@implementation LauncherDiagnostics
- (void)viewDidLoad {
    [super viewDidLoad];
    self.title=@"Logs";
    self.view.backgroundColor=UIColor.systemBackgroundColor;
    self.navigationItem.leftBarButtonItem=[[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemDone
        target:self action:@selector(closeLog)];
    self.navigationItem.rightBarButtonItems=@[
        [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemAction target:self action:@selector(shareLog:)],
        [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemSearch target:self action:@selector(searchLog)],
        [[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemRefresh target:self action:@selector(refreshLog)]
    ];
    self.source=[[UISegmentedControl alloc] initWithItems:@[@"Launcher",@"Godot",@"Previous",@"Startup",@"Console",@"Last console"]];
    self.source.selectedSegmentIndex=0;
    [self.source addTarget:self action:@selector(refreshLog) forControlEvents:UIControlEventValueChanged];
    self.navigationItem.titleView=self.source;
    self.output=[UITextView new];
    self.output.editable=NO;
    self.output.findInteractionEnabled=YES;
    self.output.font=[UIFont monospacedSystemFontOfSize:13 weight:UIFontWeightRegular];
    self.output.translatesAutoresizingMaskIntoConstraints=NO;
    [self.view addSubview:self.output];
    UILayoutGuide *guide=self.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[
        [self.output.topAnchor constraintEqualToAnchor:guide.topAnchor],
        [self.output.bottomAnchor constraintEqualToAnchor:guide.bottomAnchor],
        [self.output.leadingAnchor constraintEqualToAnchor:guide.leadingAnchor],
        [self.output.trailingAnchor constraintEqualToAnchor:guide.trailingAnchor]
    ]];
    [self refreshLog];
}
- (void)refreshLog {
    NSFileHandle *file=[NSFileHandle fileHandleForReadingFromURL:[self logURL] error:nil];
    unsigned long long length=0;
    NSString *text=nil;
    if([file seekToEndReturningOffset:&length error:nil]){
        const NSUInteger limit=256*1024;
        BOOL clipped=length>limit;
        if([file seekToOffset:clipped?length-limit:0 error:nil]){
            NSData *data=[file readDataUpToLength:limit error:nil];
            if(clipped && data.length>0){
                const uint8_t *bytes=(const uint8_t *)data.bytes;
                NSUInteger start=0;
                while(start<data.length && bytes[start++]!='\n') {}
                data=[data subdataWithRange:NSMakeRange(start,data.length-start)];
            }
            text=[[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
        }
        [file closeAndReturnError:nil];
    }
    self.output.text=text.length?text:@"No entries are available for this log.";
    self.navigationItem.rightBarButtonItems[0].enabled=text.length>0;
    self.navigationItem.rightBarButtonItems[1].enabled=text.length>0;
    [self.output scrollRangeToVisible:NSMakeRange(self.output.text.length,0)];
}
- (NSURL *)logURL {
    NSArray<NSString *> *sources=@[@"launcher.log",@"godot.log",@"godot.log.previous",@"startup.log",@"console.log",@"console.log.previous"];
    return [self.directory URLByAppendingPathComponent:sources[self.source.selectedSegmentIndex]];
}
- (void)shareLog:(UIBarButtonItem *)sender {
    UIActivityViewController *share=[[UIActivityViewController alloc] initWithActivityItems:@[[self logURL]] applicationActivities:nil];
    share.popoverPresentationController.barButtonItem=sender;
    [self presentViewController:share animated:YES completion:nil];
}
- (void)searchLog { [self.output.findInteraction presentFindNavigatorShowingReplace:NO]; }
- (void)closeLog { [self dismissViewControllerAnimated:YES completion:nil]; }
@end

__attribute__((visibility("default")))
@interface LauncherGate : UIViewController<UIDocumentPickerDelegate>
@property(nonatomic,strong) id service;
@property(nonatomic,strong) UIApplication *application;
@property(nonatomic,strong) NSDictionary *options;
@property(nonatomic,strong) UIWindow *window;
@property(nonatomic,strong) UITextField *branch,*manifest,*beta;
@property(nonatomic,strong) UILabel *message,*installation,*profile,*steamAccount;
@property(nonatomic,strong) UIProgressView *progress;
@property(nonatomic,strong) UIButton *download,*play,*cancel,*steamSignIn,*forget,*importGame,*versions,*profiles,*snapshots,*importSnapshot,*webDav,*steamCloud,*mods,*importMod,*workshop,*nexus,*diagnostics,*files,*launchOptions;
@property(nonatomic,strong) UIButton *safePlay;
@property(nonatomic,copy) NSArray<UIStackView *> *sections;
@property(nonatomic,strong) NSTimer *timer;
@property(nonatomic,strong) UIViewController *prompt;
@property(nonatomic,strong) UIScrollView *launcherScroll;
@property(nonatomic,strong) LauncherWorkshop *workshopPane;
@property(nonatomic,strong) LauncherInstalledMods *installedModsPane;
@property(nonatomic,assign) BOOL leavingPane;
@property(nonatomic,strong) NSURL *scopedArchive;
@property(nonatomic,assign) ArchivePicker archivePicker;
@property(nonatomic,assign) int64_t shownPromptId;
@property(nonatomic,assign) BOOL initialized, initializing;
@property(nonatomic,assign) BOOL canContinueInBackground;
@property(nonatomic,assign) UIBackgroundTaskIdentifier launcherBackgroundTask;
- (void)tick;
- (void)startPolling;
@end
static LauncherGate *gate;
static LauncherGameKeys *gameKeys;

@protocol LauncherGodotView
- (void)stopRendering;
@end
static BOOL delayedLaunch(id service,SEL selector,UIApplication *application,NSDictionary *options) {
    gate=[LauncherGate new];gate.service=service;gate.application=application;gate.options=options;
    gate.window=[[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
    gate.window.rootViewController=gate;
    [service setValue:gate.window forKey:@"window"];
    [gate.window makeKeyAndVisible];
    return YES;
}
static void endGameSync(UIApplication *application, BOOL cancel) {
    if(gameSyncTask==UIBackgroundTaskInvalid)return;
    if(cancel)cancelGameBackgroundSync();
    [gameSyncTimer invalidate];
    gameSyncTimer=nil;
    UIBackgroundTaskIdentifier task=gameSyncTask;
    gameSyncTask=UIBackgroundTaskInvalid;
    [application endBackgroundTask:task];
}
static void startGameSync(UIApplication *application) {
    if(gameSyncTask!=UIBackgroundTaskInvalid || beginGameBackgroundSync()!=0)return;
    gameSyncTask=[application beginBackgroundTaskWithName:@"Sync game saves" expirationHandler:^{
        endGameSync(application,YES);
    }];
    if(gameSyncTask==UIBackgroundTaskInvalid){cancelGameBackgroundSync();return;}
    gameSyncTimer=[NSTimer scheduledTimerWithTimeInterval:0.5 repeats:YES block:^(__unused NSTimer *timer){
        if(!gameBackgroundSyncActive())endGameSync(application,NO);
    }];
}
static void callGodotLifecycle(id service,SEL selector,UIApplication *application) {
    auto original=(void(*)(id,SEL,UIApplication*))lifecycle[NSStringFromSelector(selector)].pointerValue;
    if(original)original(service,selector,application);
}
static void forward(id service,SEL selector,UIApplication *application) {
    if(gameState!=GameState::Running)return;
    if(selector==@selector(applicationWillResignActive:)){
        releaseGameKeys();
        [gameKeys resetModifiers];
    }
    if(selector==@selector(applicationWillEnterForeground:) || selector==@selector(applicationWillTerminate:))
        endGameSync(application,YES);
    callGodotLifecycle(service,selector,application);
    if(selector==@selector(applicationDidEnterBackground:))startGameSync(application);
}
static void forwardAudioInterruption(id service,SEL selector,NSNotification *notification) {
    if(gameState==GameState::Running && audioInterruption)audioInterruption(service,selector,notification);
}

extern "C" __attribute__((visibility("default"),used)) int sts2_return_to_launcher() {
    if(!NSThread.isMainThread){
        dispatch_async(dispatch_get_main_queue(), ^{sts2_return_to_launcher();});
        return 1;
    }
    if(gameState==GameState::Closing || gameState==GameState::Closed)return 1;
    if(gameState!=GameState::Running || !gate || !gameWindow ||
        !lifecycle[NSStringFromSelector(@selector(applicationWillTerminate:))].pointerValue)return 0;
    gameState=GameState::Closing;
    dispatch_async(dispatch_get_main_queue(), ^{
        UIViewController *controller=gameWindow.rootViewController;
        UIView *view=controller.viewIfLoaded;
        callGodotLifecycle(gate.service,@selector(applicationWillResignActive:),gate.application);
        if([view respondsToSelector:@selector(stopRendering)])[(id<LauncherGodotView>)view stopRendering];
        prepareGameShutdown();
        [gameKeys removeFromSuperview];
        gameKeys=nil;
        // UIKit can query the old controller's orientation and gesture settings
        // while switching key windows. Those callbacks still need live Godot.
        gate.view.userInteractionEnabled=NO;
        gate.message.text=@"Closing game…";
        [gate.window makeKeyAndVisible];
        gameWindow.hidden=YES;
        // The template's termination callback runs apple_embedded_finish and
        // tears down Godot's main loop, audio, renderers, and game resources.
        callGodotLifecycle(gate.service,@selector(applicationWillTerminate:),gate.application);
        [NSNotificationCenter.defaultCenter removeObserver:gate.service
            name:AVAudioSessionInterruptionNotification object:AVAudioSession.sharedInstance];
        [gate.service setValue:gate.window forKey:@"window"];
        gameWindow.rootViewController=nil;
        // The template retains this controller internally. Drop its GDTView so
        // it releases UIKit rendering and motion resources after engine cleanup.
        controller.view=[UIView new];
        gameWindow=nil;
        gameState=GameState::Closed;
        gameDidQuit();
        startGameSync(gate.application);
        [gate startPolling];
        [gate tick];
        gate.view.userInteractionEnabled=YES;
    });
    return 1;
}

extern "C" __attribute__((visibility("default"),used)) void sts2_game_keyboard_ready() {
    dispatch_async(dispatch_get_main_queue(), ^{
        if(gameState!=GameState::Running || !gameWindow || gameKeys)return;
        gameKeys=[LauncherGameKeys new];
        gameKeys.sendKey=^(NSString *key,int modifiers){
            if(gameState==GameState::Running)sendGameKey(key.UTF8String,modifiers);
        };
        [gameKeys installInWindow:gameWindow];
    });
}

@implementation LauncherGate
+ (void)load {
    Class service=NSClassFromString(@"GDTAppDelegateService");
    Method method=class_getInstanceMethod(service,@selector(application:didFinishLaunchingWithOptions:));
    if(!method)return; // CI inspects the generated host for this symbol.
    godotLaunch=(BOOL(*)(id,SEL,UIApplication*,NSDictionary*))method_setImplementation(method,(IMP)delayedLaunch);
    Method interruption=class_getInstanceMethod(service,NSSelectorFromString(@"onAudioInterruption:"));
    if(interruption)audioInterruption=(void(*)(id,SEL,NSNotification *))method_setImplementation(interruption,(IMP)forwardAudioInterruption);
    lifecycle=[NSMutableDictionary new];
    for(NSString *name in @[@"applicationDidBecomeActive:",@"applicationWillResignActive:",@"applicationDidEnterBackground:",@"applicationWillEnterForeground:",@"applicationDidReceiveMemoryWarning:",@"applicationWillTerminate:"]){
        Method m=class_getInstanceMethod(service,NSSelectorFromString(name));
        if(m)lifecycle[name]=[NSValue valueWithPointer:(const void*)method_setImplementation(m,(IMP)forward)];
    }
}
- (UITextField*)field:(NSString*)placeholder secure:(BOOL)secure {
    UITextField *f=[UITextField new];f.placeholder=placeholder;f.secureTextEntry=secure;f.borderStyle=UITextBorderStyleRoundedRect;f.autocorrectionType=UITextAutocorrectionTypeNo;f.autocapitalizationType=UITextAutocapitalizationTypeNone;return f;
}
- (UIButton*)button:(NSString*)title action:(SEL)action {
    UIButtonConfiguration *style=UIButtonConfiguration.grayButtonConfiguration;
    style.title=title;
    style.buttonSize=UIButtonConfigurationSizeMedium;
    style.cornerStyle=UIButtonConfigurationCornerStyleMedium;
    style.titleAlignment=UIButtonConfigurationTitleAlignmentLeading;
    UIButton *button=[UIButton buttonWithConfiguration:style primaryAction:nil];
    button.contentHorizontalAlignment=UIControlContentHorizontalAlignmentLeading;
    [button addTarget:self action:action forControlEvents:UIControlEventTouchUpInside];
    [button.heightAnchor constraintGreaterThanOrEqualToConstant:44].active=YES;
    return button;
}
- (UIStackView *)section:(NSArray<UIView *> *)views {
    UIStackView *stack=[[UIStackView alloc] initWithArrangedSubviews:views];
    stack.axis=UILayoutConstraintAxisVertical;
    stack.spacing=12;
    return stack;
}
- (void)sectionChanged:(UISegmentedControl *)control {
    [self.view endEditing:YES];
    for(NSUInteger index=0;index<self.sections.count;index++) self.sections[index].hidden=index!=(NSUInteger)control.selectedSegmentIndex;
}
- (void)viewDidLoad {
    [super viewDidLoad];self.view.backgroundColor=UIColor.systemBackgroundColor;
    self.view.tintColor=UIColor.systemIndigoColor;
    self.launcherBackgroundTask=UIBackgroundTaskInvalid;
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(launcherDidEnterBackground:)
        name:UIApplicationDidEnterBackgroundNotification object:self.application];
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(launcherDidBecomeActive:)
        name:UIApplicationDidBecomeActiveNotification object:self.application];
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(gameDidReceiveMemoryWarning:)
        name:UIApplicationDidReceiveMemoryWarningNotification object:self.application];
    UILabel *heading=[UILabel new];heading.text=@"Slay the Spire 2";
    heading.font=[UIFont preferredFontForTextStyle:UIFontTextStyleLargeTitle];
    heading.adjustsFontForContentSizeCategory=YES;heading.accessibilityTraits|=UIAccessibilityTraitHeader;heading.numberOfLines=0;
    self.installation=[UILabel new];self.installation.text=@"No game installed";self.installation.textColor=UIColor.secondaryLabelColor;
    self.profile=[UILabel new];self.profile.textColor=UIColor.secondaryLabelColor;
    self.steamAccount=[UILabel new];self.steamAccount.textColor=UIColor.secondaryLabelColor;
    self.steamAccount.numberOfLines=0;self.steamAccount.text=@"Steam: Signed out";
    self.branch=[self field:@"Branch: public, beta name or ? to list" secure:NO];self.branch.text=@"?";
    self.manifest=[self field:@"Exact manifest ID (optional)" secure:NO];
    self.beta=[self field:@"Private beta password (optional)" secure:YES];
    self.download=[self button:@"Install from Steam" action:@selector(downloadPressed)];
    self.steamSignIn=[self button:@"Sign in to Steam" action:@selector(steamSignInPressed)];
    self.forget=[self button:@"Sign out of Steam" action:@selector(forgetPressed)];
    self.play=[self button:@"Play installed game" action:@selector(playPressed)];
    self.safePlay=[self button:@"Launch without mods or preload" action:@selector(safePlayPressed)];
    self.importGame=[self button:@"Import game archive" action:@selector(importPressed)];
    self.versions=[self button:@"Installed versions" action:@selector(versionsPressed)];
    self.profiles=[self button:@"Launch profiles" action:@selector(profilesPressed)];
    self.snapshots=[self button:@"Snapshots and save slots" action:@selector(snapshotsPressed)];
    self.importSnapshot=[self button:@"Import save snapshot" action:@selector(importSnapshotPressed)];
    self.webDav=[self button:@"WebDAV save sync" action:@selector(webDavPressed)];
    self.steamCloud=[self button:@"Steam Cloud saves" action:@selector(steamCloudPressed)];
    self.mods=[self button:@"Installed mods" action:@selector(modsPressed)];
    self.importMod=[self button:@"Import mod archive" action:@selector(importModPressed)];
    self.workshop=[self button:@"Steam Workshop" action:@selector(workshopPressed)];
    UIButton *workshopWebsite=[self button:@"Open Workshop in browser" action:@selector(workshopWebsitePressed)];
    self.nexus=[self button:@"Nexus Mods" action:@selector(nexusPressed)];
    UIButton *nexusWebsite=[self button:@"Open Nexus Mods in browser" action:@selector(nexusWebsitePressed)];
    self.diagnostics=[self button:@"View logs" action:@selector(logPressed)];
    self.files=[self button:@"Launcher files" action:@selector(filesPressed)];
    self.launchOptions=[self button:@"Game launch settings" action:@selector(launchOptionsPressed)];
    UIButton *about=[self button:@"About and updates" action:@selector(aboutPressed)];
    self.cancel=[self button:@"Cancel operation" action:@selector(cancelPressed)];
    self.progress=[UIProgressView new];self.message=[UILabel new];self.message.numberOfLines=0;self.message.text=@"Waiting for JIT permission";
    self.message.font=[UIFont preferredFontForTextStyle:UIFontTextStyleFootnote];
    self.message.adjustsFontForContentSizeCategory=YES;
    self.message.textColor=UIColor.secondaryLabelColor;
    UIButtonConfiguration *playStyle=UIButtonConfiguration.filledButtonConfiguration;
    playStyle.title=@"Play installed game";
    self.play.configuration=playStyle;
    UIButtonConfiguration *downloadStyle=UIButtonConfiguration.filledButtonConfiguration;
    downloadStyle.title=@"Install from Steam";
    self.download.configuration=downloadStyle;

    UISegmentedControl *navigation=[[UISegmentedControl alloc] initWithItems:@[@"Play",@"Install",@"Saves",@"Mods",@"Steam",@"Tools"]];
    navigation.selectedSegmentIndex=0;
    [navigation addTarget:self action:@selector(sectionChanged:) forControlEvents:UIControlEventValueChanged];
    UIStackView *playSection=[self section:@[self.installation,self.profile,self.play,self.safePlay,self.profiles,self.versions]];
    UIStackView *installSection=[self section:@[self.importGame,self.branch,self.manifest,self.beta,self.download]];
    UIStackView *steamSection=[self section:@[self.steamAccount,self.steamSignIn,self.forget]];
    UIStackView *savesSection=[self section:@[self.snapshots,self.importSnapshot,self.steamCloud,self.webDav]];
    UIStackView *modsSection=[self section:@[self.mods,self.importMod,self.workshop,workshopWebsite,self.nexus,nexusWebsite]];
    UIStackView *toolsSection=[self section:@[self.launchOptions,self.files,self.diagnostics,about]];
    self.sections=@[playSection,installSection,savesSection,modsSection,steamSection,toolsSection];
    UIStackView *stack=[self section:@[heading,navigation,playSection,installSection,savesSection,modsSection,steamSection,toolsSection,self.progress,self.message,self.cancel]];
    stack.spacing=16;
    stack.translatesAutoresizingMaskIntoConstraints=NO;
    [self sectionChanged:navigation];
    UIScrollView *scroll=[UIScrollView new];self.launcherScroll=scroll;
    scroll.translatesAutoresizingMaskIntoConstraints=NO;[self.view addSubview:scroll];[scroll addSubview:stack];
    UILayoutGuide *safe=self.view.safeAreaLayoutGuide;
    NSLayoutConstraint *preferredWidth=[stack.widthAnchor constraintEqualToAnchor:scroll.frameLayoutGuide.widthAnchor constant:-48];
    preferredWidth.priority=UILayoutPriorityDefaultHigh;
    [NSLayoutConstraint activateConstraints:@[[scroll.topAnchor constraintEqualToAnchor:safe.topAnchor],[scroll.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor],[scroll.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor],[scroll.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor],[scroll.contentLayoutGuide.widthAnchor constraintEqualToAnchor:scroll.frameLayoutGuide.widthAnchor],[stack.topAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.topAnchor constant:20],[stack.bottomAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.bottomAnchor constant:-20],[stack.centerXAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.centerXAnchor],[stack.widthAnchor constraintLessThanOrEqualToConstant:680],preferredWidth]];
    self.download.enabled=self.play.enabled=self.cancel.enabled=self.forget.enabled=self.importGame.enabled=self.versions.enabled=self.profiles.enabled=self.snapshots.enabled=self.webDav.enabled=self.steamCloud.enabled=NO;
    self.mods.enabled=self.importMod.enabled=self.workshop.enabled=self.nexus.enabled=NO;
    self.safePlay.enabled=NO;
    self.files.enabled=self.launchOptions.enabled=self.importSnapshot.enabled=NO;
    self.steamSignIn.enabled=NO;
    [self startPolling];
}
- (void)startPolling {
    [self.timer invalidate];
    self.timer=[NSTimer timerWithTimeInterval:0.4 target:self selector:@selector(tick) userInfo:nil repeats:YES];
    [NSRunLoop.mainRunLoop addTimer:self.timer forMode:NSRunLoopCommonModes];
}
- (void)tick {
    if(gameState==GameState::Running || gameState==GameState::Closing)return;
    if(!self.initialized){
        if(self.initializing || self.application.applicationState!=UIApplicationStateActive)return;
        uint32_t flags=0;
        if(csops(getpid(),0,&flags,sizeof(flags))!=0||!(flags&0x10000000)){self.message.text=@"Enable JIT for this process, then return to the launcher.";return;}
        self.initializing=YES;self.message.text=@"Initializing Mono JIT…";
        NSString *frameworks=NSBundle.mainBundle.privateFrameworksPath;
        NSString *library=[frameworks stringByAppendingPathComponent:@"sts2.framework/sts2"];
        NSString *managed=[NSBundle.mainBundle.resourcePath stringByAppendingPathComponent:@"Managed"];
        NSString *docs=NSSearchPathForDirectoriesInDomains(NSDocumentDirectory,NSUserDomainMask,YES).firstObject;
        NSString *support=NSSearchPathForDirectoriesInDomains(NSApplicationSupportDirectory,NSUserDomainMask,YES).firstObject;
        NSString *payload=[support stringByAppendingPathComponent:@"STS2Payloads"];
        captureConsole(docs);
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0),^{
            NSString *failure = startManagedLauncher(library, managed, frameworks, payload, docs);
            if (failure) {
                NSLog(@"Launcher startup failed: %@", failure);
                recordStartupStatus(docs, failure);
            }
            dispatch_async(dispatch_get_main_queue(), ^{
                self.initializing = NO;
                self.initialized = failure == nil;
                if (failure) {
                    [self.timer invalidate];
                    self.message.text = failure;
                }
            });
        });return;
    }
    char buffer[8192];
    int length=copyStatus(buffer,sizeof(buffer));
    NSData *data;
    if(length>0) data=[NSData dataWithBytes:buffer length:length];
    else if(length<0 && length>=-4*1024*1024) {
        NSMutableData *storage=[NSMutableData dataWithLength:-length];
        length=copyStatus((char*)storage.mutableBytes,(int)storage.length);
        if(length<=0)return;
        storage.length=length;
        data=storage;
    } else return;
    NSDictionary *state=[NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if(![state isKindOfClass:NSDictionary.class])return;
    NSString *stage=state[@"Stage"];NSString *message=state[@"Message"];
    self.canContinueInBackground=[@[@"auth",@"resolve",@"download",@"process",@"import",@"mods",@"cloud",@"webdav",@"snapshots",@"textures"] containsObject:stage] ||
        ([stage isEqual:@"prompt"] &&
        [@[@"confirm",@"code"] containsObject:state[@"Prompt"]]);
    if(!self.canContinueInBackground) [self endLauncherBackgroundTask];
    BOOL idle=[@[@"idle",@"error",@"installed"] containsObject:stage];
    BOOL closed=gameState==GameState::Closed;
    NSString *installed=[state[@"Installed"] isKindOfClass:NSString.class]?state[@"Installed"]:nil;
    NSString *profile=[state[@"Profile"] isKindOfClass:NSString.class]?state[@"Profile"]:nil;
    self.installation.text=installed?[NSString stringWithFormat:@"Installed: %@",installed]:@"No game installed";
    self.profile.text=profile?[NSString stringWithFormat:@"Profile: %@",profile]:@"Launch profile unavailable";
    NSString *steamAccount=[state[@"SteamAccount"] isKindOfClass:NSString.class]?state[@"SteamAccount"]:nil;
    self.steamAccount.text=steamAccount?[NSString stringWithFormat:@"Steam: %@%@",steamAccount,
        [state[@"SteamSessionTemporary"] boolValue]?@" (saved for this launch only)":@""]:@"Steam: Signed out";
    self.download.enabled=self.importGame.enabled=self.versions.enabled=self.profiles.enabled=idle;
    self.forget.enabled=idle && steamAccount!=nil;
    self.forget.hidden=steamAccount==nil;
    self.steamSignIn.enabled=idle;
    self.steamSignIn.hidden=steamAccount!=nil;
    self.play.enabled=idle && !closed && installed!=nil && profile!=nil;
    NSString *playTitle=closed?@"Reopen app to play":@"Play installed game";
    UIButtonConfiguration *playStyle=self.play.configuration;
    if(![playStyle.title isEqualToString:playTitle]){
        playStyle.title=playTitle;
        self.play.configuration=playStyle;
    }
    self.safePlay.enabled=self.play.enabled;
    self.safePlay.hidden=closed;
    BOOL syncingSaves=gameSyncTask!=UIBackgroundTaskInvalid;
    self.snapshots.enabled=self.importSnapshot.enabled=self.webDav.enabled=self.steamCloud.enabled=idle && profile!=nil && !syncingSaves;
    self.mods.enabled=self.importMod.enabled=self.workshop.enabled=self.nexus.enabled=idle && profile!=nil;
    self.diagnostics.enabled=idle;
    self.files.enabled=idle && !syncingSaves;
    self.launchOptions.enabled=idle;
    self.cancel.enabled=!idle;
    if(idle && self.scopedArchive){[self.scopedArchive stopAccessingSecurityScopedResource];self.scopedArchive=nil;}
    double total=[state[@"Total"] doubleValue],done=[state[@"Completed"] doubleValue],speed=[state[@"Speed"] doubleValue];
    self.progress.progress=total>0?done/total:0;
    self.progress.hidden=total<=0;
    if(total>0 && [stage isEqual:@"cloud"]){
        NSString *transferred=[NSByteCountFormatter stringFromByteCount:[state[@"Completed"] longLongValue] countStyle:NSByteCountFormatterCountStyleFile];
        NSString *expected=[NSByteCountFormatter stringFromByteCount:[state[@"Total"] longLongValue] countStyle:NSByteCountFormatterCountStyleFile];
        message=[message stringByAppendingFormat:@"\n%@ / %@",transferred,expected];
    }else if(total>0){
        if(![stage isEqual:@"textures"])message=[message stringByAppendingFormat:@"\n%.1f / %.1f MiB",done/1048576,total/1048576];
    }
    else if(done>0) message=[message stringByAppendingFormat:@"\n%.1f MiB imported",done/1048576];
    if(speed>0) message=[message stringByAppendingFormat:@" • %.2f MiB/s",speed/1048576];
    if([state[@"Eta"] isKindOfClass:NSString.class]) message=[message stringByAppendingFormat:@" • %@ remaining",state[@"Eta"]];
    self.message.text=message;
    if(self.leavingPane){
        if(!idle)return;
        self.leavingPane=NO;
    }
    if([stage isEqual:@"prompt"]){
        if(self.application.applicationState==UIApplicationStateActive)[self showPrompt:state];
        return;
    }
    self.shownPromptId=0;
    if(self.prompt){[self.prompt dismissViewControllerAnimated:YES completion:nil];self.prompt=nil;}
    if(self.workshopPane)[self.workshopPane showStatus:message progress:total>0?done/total:-1 busy:!idle];
    if(self.installedModsPane)[self.installedModsPane showStatus:message];
    if([stage isEqual:@"ready"] && gameState==GameState::Ready){
        if(self.application.applicationState!=UIApplicationStateActive)return;
        [self.timer invalidate];self.message.text=@"Starting Godot…";
        NSString *documents=NSSearchPathForDirectoriesInDomains(NSDocumentDirectory,NSUserDomainMask,YES).firstObject;
        recordStartupStatus(documents, @"Starting Godot");
        UIWindow *old=self.window;BOOL ok=godotLaunch(self.service,@selector(application:didFinishLaunchingWithOptions:),self.application,self.options);
        if(ok){
            gameWindow=[self.service valueForKey:@"window"];
            gameState=GameState::Running;
            forward(self.service,@selector(applicationDidBecomeActive:),self.application);
            old.hidden=YES;
        }
        else{self.message.text=@"Godot startup failed. Restart the app.";}
    }
}
- (void)launcherDidEnterBackground:(NSNotification *)notification {
    [self tick];
    if(!self.canContinueInBackground || self.launcherBackgroundTask!=UIBackgroundTaskInvalid) return;
    __weak LauncherGate *weakGate=self;
    self.launcherBackgroundTask=[self.application beginBackgroundTaskWithName:@"Launcher operation" expirationHandler:^{
        [weakGate endLauncherBackgroundTask];
    }];
}
- (void)launcherDidBecomeActive:(NSNotification *)notification { [self endLauncherBackgroundTask]; }
- (void)gameDidReceiveMemoryWarning:(NSNotification *)notification {
    if(gameState==GameState::Running && reportGameMemoryWarning)reportGameMemoryWarning();
}
- (void)endLauncherBackgroundTask {
    if(self.launcherBackgroundTask==UIBackgroundTaskInvalid) return;
    UIBackgroundTaskIdentifier task=self.launcherBackgroundTask;
    self.launcherBackgroundTask=UIBackgroundTaskInvalid;
    [self.application endBackgroundTask:task];
}
- (void)showPrompt:(NSDictionary*)state {
    NSString *kind=state[@"Prompt"],*message=state[@"Message"];
    int64_t identifier=[state[@"PromptId"] longLongValue];
    if(self.shownPromptId==identifier || self.presentedViewController)return;
    self.shownPromptId=identifier;
    __weak LauncherGate *weakGate=self;
    void (^submit)(NSString *)=^(NSString *value){reply(value.UTF8String,identifier);weakGate.prompt=nil;};
    void (^cancel)(void)=^{cancelOperation();weakGate.prompt=nil;};
    if(self.workshopPane){
        [self.workshopPane showPrompt:state reply:submit];
        return;
    }
    if(self.installedModsPane && [kind isEqualToString:@"mod"]){
        NSArray *choices=[state[@"Choices"] isKindOfClass:NSArray.class]?state[@"Choices"]:@[];
        [self.installedModsPane showChoices:choices message:message reply:submit];
        return;
    }
    if([state[@"Choices"] isKindOfClass:NSArray.class]){
        LauncherChoices *list=[[LauncherChoices alloc] initWithStyle:UITableViewStyleInsetGrouped];
        list.title=message;
        list.choices=state[@"Choices"];
        __weak LauncherChoices *weakList=list;
        list.selectValue=^(NSString *value){[weakList dismissViewControllerAnimated:YES completion:nil];submit(value);};
        list.cancelSelection=^{[weakList dismissViewControllerAnimated:YES completion:nil];cancel();};
        UINavigationController *sheet=[[UINavigationController alloc] initWithRootViewController:list];
        sheet.modalPresentationStyle=UIModalPresentationFormSheet;
        sheet.modalInPresentation=YES;
        self.prompt=sheet;
        [self presentViewController:sheet animated:YES completion:nil];
        return;
    }
    NSString *title=@"Steam";
    if([kind isEqual:@"profile-name"]) title=@"Launch profile";
    if([kind hasPrefix:@"webdav-"]) title=@"WebDAV";
    if([kind hasPrefix:@"nexus-"]) title=@"Nexus Mods";
    if([kind hasPrefix:@"mod-"]) title=@"Mods";
    UIAlertController *alert=[UIAlertController alertControllerWithTitle:title message:message preferredStyle:UIAlertControllerStyleAlert];
    self.prompt=alert;
    if([kind isEqual:@"confirm"]){
        [alert addAction:[UIAlertAction actionWithTitle:@"Wait for approval" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){submit(@"approve");}]];
        [alert addAction:[UIAlertAction actionWithTitle:@"Enter code" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){submit(@"code");}]];
    }else{
        [alert addTextFieldWithConfigurationHandler:^(UITextField *field){
            field.secureTextEntry=[@[@"password",@"webdav-password",@"steam-password",@"nexus-key",@"nexus-link"] containsObject:kind];
            field.autocapitalizationType=UITextAutocapitalizationTypeNone;
            field.autocorrectionType=UITextAutocorrectionTypeNo;
            if([@[@"webdav-url",@"workshop-id",@"nexus-link"] containsObject:kind]) field.keyboardType=UIKeyboardTypeURL;
            if([kind isEqual:@"webdav-slot"]) field.placeholder=@"global";
        }];
        __weak UIAlertController *weakAlert=alert;
        [alert addAction:[UIAlertAction actionWithTitle:@"Submit" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){submit(weakAlert.textFields.firstObject.text ?: @"");}]];
    }
    [alert addAction:[UIAlertAction actionWithTitle:@"Cancel" style:UIAlertActionStyleCancel handler:^(__unused UIAlertAction *a){cancel();}]];
    [self presentViewController:alert animated:YES completion:nil];
}
- (void)showError:(NSString *)message {
    UIAlertController *alert=[UIAlertController alertControllerWithTitle:nil message:message preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:@"OK" style:UIAlertActionStyleDefault handler:nil]];
    [self presentViewController:alert animated:YES completion:nil];
}
- (void)versionsPressed { [self.view endEditing:YES];if(browseVersions()!=0)[self showError:@"Another operation is active."]; }
- (void)profilesPressed { [self.view endEditing:YES];if(browseProfiles()!=0)[self showError:@"Another operation is active."]; }
- (void)snapshotsPressed { [self.view endEditing:YES];if(browseSnapshots()!=0)[self showError:@"Another operation is active."]; }
- (void)webDavPressed { [self.view endEditing:YES];if(browseWebDav()!=0)[self showError:@"Another operation is active."]; }
- (void)steamCloudPressed { [self.view endEditing:YES];if(browseSteamCloud()!=0)[self showError:@"Another operation is active."]; }
- (void)modsPressed {
    [self.view endEditing:YES];
    if(browseMods()!=0){[self showError:@"Another operation is active."];return;}
    LauncherInstalledMods *pane=[LauncherInstalledMods new];
    __weak LauncherGate *weakGate=self;
    pane.close=^{[weakGate closeInstalledMods];};
    self.installedModsPane=pane;
    [self attachPane:pane];
    [pane showStatus:@"Reading installed mods…"];
}
- (void)closeInstalledMods {
    if(!self.installedModsPane)return;
    [self detachPane:self.installedModsPane];
    self.installedModsPane=nil;
}
- (void)workshopPressed {
    [self.view endEditing:YES];
    if(browseWorkshop()!=0){[self showError:@"Another operation is active."];return;}
    LauncherWorkshop *pane=[LauncherWorkshop new];
    __weak LauncherGate *weakGate=self;
    pane.close=^{[weakGate closeWorkshop];};
    self.workshopPane=pane;
    [self attachPane:pane];
    [pane showStatus:@"Opening Steam Workshop…" progress:-1 busy:YES];
}
- (void)closeWorkshop {
    if(!self.workshopPane)return;
    [self detachPane:self.workshopPane];
    self.workshopPane=nil;
}
- (void)attachPane:(UIViewController *)pane {
    self.launcherScroll.hidden=YES;
    [self addChildViewController:pane];
    pane.view.translatesAutoresizingMaskIntoConstraints=NO;
    [self.view addSubview:pane.view];
    [NSLayoutConstraint activateConstraints:@[
        [pane.view.topAnchor constraintEqualToAnchor:self.view.topAnchor],
        [pane.view.bottomAnchor constraintEqualToAnchor:self.view.bottomAnchor],
        [pane.view.leadingAnchor constraintEqualToAnchor:self.view.leadingAnchor],
        [pane.view.trailingAnchor constraintEqualToAnchor:self.view.trailingAnchor]
    ]];
    [pane didMoveToParentViewController:self];
}
- (void)detachPane:(UIViewController *)pane {
    self.leavingPane=YES;
    cancelOperation();
    [pane.view endEditing:YES];
    [pane willMoveToParentViewController:nil];
    [pane.view removeFromSuperview];
    [pane removeFromParentViewController];
    self.launcherScroll.hidden=NO;
}
- (void)nexusPressed { [self.view endEditing:YES];if(browseNexus()!=0)[self showError:@"Another operation is active."]; }
- (void)nexusWebsitePressed {
    NSURL *url=[NSURL URLWithString:@"https://www.nexusmods.com/slaythespire2/mods/"];
    [self.application openURL:url options:@{} completionHandler:nil];
}
- (void)launchOptionsPressed { if(browseLaunchOptions()!=0)[self showError:@"Another operation is active."]; }
- (void)filesPressed {
    NSURL *documents=[[NSFileManager defaultManager] URLsForDirectory:NSDocumentDirectory inDomains:NSUserDomainMask].firstObject;
    UIViewController *browser=launcherFileBrowser(documents);
    [self presentViewController:browser animated:YES completion:nil];
}
- (void)aboutPressed {
    NSDictionary *info=NSBundle.mainBundle.infoDictionary;
    NSString *version=info[@"CFBundleShortVersionString"]?:@"0";
    NSString *commit=info[@"STS2GitRevision"]?:@"";
    if(commit.length>12)commit=[commit substringToIndex:12];
    NSString *message=commit.length?[NSString stringWithFormat:@"Version %@ · %@",version,commit]:[NSString stringWithFormat:@"Version %@",version];
    if(sts2_extensions_available())message=[message stringByAppendingString:@"\n\nFMOD by Firelight Technologies Pty Ltd."];
    UIAlertController *alert=[UIAlertController alertControllerWithTitle:@"STS2 Launcher" message:message preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:@"Check for updates" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){
        [self checkForUpdates:version];
    }]];
    [alert addAction:[UIAlertAction actionWithTitle:@"Open source repository" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){
        NSURL *url=[NSURL URLWithString:@"https://github.com/kollier-p/Sts2IOSLauncher"];
        [self.application openURL:url options:@{} completionHandler:nil];
    }]];
    [alert addAction:[UIAlertAction actionWithTitle:@"Done" style:UIAlertActionStyleCancel handler:nil]];
    [self presentViewController:alert animated:YES completion:nil];
}
- (void)checkForUpdates:(NSString *)installed {
    NSURL *url=[NSURL URLWithString:@"https://api.github.com/repos/kollier-p/Sts2IOSLauncher/releases/latest"];
    NSMutableURLRequest *request=[NSMutableURLRequest requestWithURL:url cachePolicy:NSURLRequestUseProtocolCachePolicy timeoutInterval:15];
    [request setValue:@"application/vnd.github+json" forHTTPHeaderField:@"Accept"];
    [[NSURLSession.sharedSession dataTaskWithRequest:request completionHandler:^(NSData *data,NSURLResponse *response,NSError *error){
        NSInteger code=[response isKindOfClass:NSHTTPURLResponse.class]?((NSHTTPURLResponse *)response).statusCode:0;
        NSDictionary *release=nil;
        if(code==200 && data.length && data.length<1024*1024){
            id value=[NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
            if([value isKindOfClass:NSDictionary.class])release=value;
        }
        NSString *tag=[release[@"tag_name"] isKindOfClass:NSString.class]?release[@"tag_name"]:@"";
        NSString *version=[tag hasPrefix:@"v"]?[tag substringFromIndex:1]:tag;
        BOOL valid=[version rangeOfString:@"^[0-9]+[.][0-9]+[.][0-9]+$" options:NSRegularExpressionSearch].location!=NSNotFound;
        BOOL available=valid && [version compare:installed options:NSNumericSearch]==NSOrderedDescending;
        NSURL *page=valid?[NSURL URLWithString:[@"https://github.com/kollier-p/Sts2IOSLauncher/releases/tag/" stringByAppendingString:tag]]:nil;
        NSString *message=available?[NSString stringWithFormat:@"Version %@ is available.",version]:
            valid?@"You have the latest published version.":
            code==404?@"No stable launcher release is published.":@"The update check could not finish.";
        dispatch_async(dispatch_get_main_queue(),^{
            if(self.presentedViewController || !self.view.window)return;
            UIAlertController *alert=[UIAlertController alertControllerWithTitle:@"Launcher updates" message:message preferredStyle:UIAlertControllerStyleAlert];
            if(available && page)[alert addAction:[UIAlertAction actionWithTitle:@"Open release" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){
                [self.application openURL:page options:@{} completionHandler:nil];
            }]];
            [alert addAction:[UIAlertAction actionWithTitle:@"Done" style:UIAlertActionStyleCancel handler:nil]];
            [self presentViewController:alert animated:YES completion:nil];
        });
    }] resume];
}
- (void)logPressed {
    NSURL *documents=[[NSFileManager defaultManager] URLsForDirectory:NSDocumentDirectory inDomains:NSUserDomainMask].firstObject;
    LauncherDiagnostics *viewer=[LauncherDiagnostics new];
    viewer.directory=[documents URLByAppendingPathComponent:@"LauncherLogs" isDirectory:YES];
    UINavigationController *navigation=[[UINavigationController alloc] initWithRootViewController:viewer];
    [self presentViewController:navigation animated:YES completion:nil];
}
- (void)workshopWebsitePressed {
    NSURL *url=[NSURL URLWithString:@"https://steamcommunity.com/app/2868840/workshop/"];
    [self.application openURL:url options:@{} completionHandler:nil];
}
- (void)importModPressed {
    [self selectArchive:ArchivePicker::Mod];
}
- (void)importPressed {
    [self selectArchive:ArchivePicker::Game];
}
- (void)importSnapshotPressed {
    [self selectArchive:ArchivePicker::Snapshot];
}
- (void)selectArchive:(ArchivePicker)kind {
    [self.view endEditing:YES];
    self.archivePicker=kind;
    NSMutableArray<UTType *> *types=[NSMutableArray arrayWithObject:UTTypeZIP];
    if(kind!=ArchivePicker::Snapshot){
        [types addObject:UTTypeArchive];
        for(NSString *extension in @[@"7z",@"rar",@"tar",@"gz",@"tgz",@"bz2",@"tbz",@"tbz2",@"xz",@"txz",@"zst",@"tzst",@"lz"]){
            UTType *type=[UTType typeWithFilenameExtension:extension];
            if(type && ![types containsObject:type]) [types addObject:type];
        }
    }
    UIDocumentPickerViewController *picker=[[UIDocumentPickerViewController alloc] initForOpeningContentTypes:types asCopy:NO];
    picker.delegate=self;
    [self presentViewController:picker animated:YES completion:nil];
}
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
    NSURL *archive=urls.firstObject;
    if(!archive)return;
    if([archive startAccessingSecurityScopedResource])self.scopedArchive=archive;
    Command importer=beginImport;
    if(self.archivePicker==ArchivePicker::Mod) importer=beginModImport;
    if(self.archivePicker==ArchivePicker::Snapshot) importer=beginSnapshotImport;
    if(importer(archive.fileSystemRepresentation)!=0){
        [self.scopedArchive stopAccessingSecurityScopedResource];self.scopedArchive=nil;
        [self showError:@"Unable to open the selected archive."];
    }
}
- (void)steamSignInPressed { if(signInSteam()!=0)[self showError:@"Unable to start Steam sign-in."]; }
- (void)forgetPressed { if(forgetSession()!=0)[self showError:@"Unable to clear saved Steam session."]; }
- (void)downloadPressed {
    NSDictionary *request=@{@"Branch":self.branch.text?:@"public",@"Manifest":self.manifest.text?:@"",@"BetaPassword":self.beta.text?:@""};
    NSData *data=[NSJSONSerialization dataWithJSONObject:request options:0 error:nil];NSString *json=[[NSString alloc]initWithData:data encoding:NSUTF8StringEncoding];
    self.beta.text=@"";[self.view endEditing:YES];int rc=beginDownload(json.UTF8String);
    if(rc==-2)[self showError:@"Enter your Steam account name and password."];
    else if(rc==-3)[self showError:@"Enter the numeric Steam manifest ID or leave it blank."];
    else if(rc!=0)[self showError:@"Unable to start the Steam download."];
}
- (void)playPressed {
    [self.view endEditing:YES];
    if(gameState!=GameState::Ready)return;
    if(prepareGame()!=0)[self showError:@"Another operation is active."];
}
- (void)safePlayPressed {
    [self.view endEditing:YES];
    if(gameState==GameState::Ready && prepareSafeGame()!=0)[self showError:@"Another operation is active."];
}
- (void)cancelPressed {cancelOperation();}
@end
