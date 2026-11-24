#import <UIKit/UIKit.h>
#import <objc/runtime.h>
#include <dlfcn.h>
#include <unistd.h>
#include <stdint.h>
#include <stdio.h>

extern "C" int csops(pid_t,unsigned int,void*,size_t);
using InitRuntime = int (*)(const char*,const char*,const char*);
using GetDelegate = int (*)(const char*,void**);
using InitHost = int (*)(const char*,const char*,const char*);
using Command = int (*)(const char*);
using Simple = int (*)();
using Status = int (*)(char*,int);
static BOOL (*godotLaunch)(id,SEL,UIApplication*,NSDictionary*);
static NSMutableDictionary<NSString*,NSValue*> *lifecycle;
static BOOL started = NO;
static InitHost initHost;
static Command beginDownload, reply;
static Simple cancelOperation, prepareGame, forgetSession;
static Status copyStatus;

__attribute__((visibility("default")))
@interface LauncherGate : UIViewController
@property(nonatomic,strong) id service;
@property(nonatomic,strong) UIApplication *application;
@property(nonatomic,strong) NSDictionary *options;
@property(nonatomic,strong) UIWindow *window;
@property(nonatomic,strong) UITextField *account,*password,*branch,*manifest,*beta;
@property(nonatomic,strong) UILabel *message;
@property(nonatomic,strong) UIProgressView *progress;
@property(nonatomic,strong) UIButton *download,*play,*cancel,*saved,*forget;
@property(nonatomic,strong) UISwitch *remember;
@property(nonatomic,strong) NSTimer *timer;
@property(nonatomic,strong) UIAlertController *prompt;
@property(nonatomic,copy) NSString *promptSignature;
@property(nonatomic,assign) BOOL initialized, initializing;
- (void)tick;
@end
static LauncherGate *gate;
static BOOL delayedLaunch(id service,SEL selector,UIApplication *application,NSDictionary *options) {
    gate=[LauncherGate new];gate.service=service;gate.application=application;gate.options=options;
    gate.window=[[UIWindow alloc] initWithFrame:UIScreen.mainScreen.bounds];
    gate.window.rootViewController=gate;
    [service setValue:gate.window forKey:@"window"];
    [gate.window makeKeyAndVisible];
    return YES;
}
static void forward(id service,SEL selector,UIApplication *application) {
    if(!started)return;
    auto original=(void(*)(id,SEL,UIApplication*))lifecycle[NSStringFromSelector(selector)].pointerValue;
    if(original)original(service,selector,application);
}
@implementation LauncherGate
+ (void)load {
    Class service=NSClassFromString(@"GDTAppDelegateService");
    Method method=class_getInstanceMethod(service,@selector(application:didFinishLaunchingWithOptions:));
    if(!method)return; // CI inspects the generated host for this symbol.
    godotLaunch=(BOOL(*)(id,SEL,UIApplication*,NSDictionary*))method_setImplementation(method,(IMP)delayedLaunch);
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
    UIButton *b=[UIButton buttonWithType:UIButtonTypeSystem];[b setTitle:title forState:UIControlStateNormal];[b addTarget:self action:action forControlEvents:UIControlEventTouchUpInside];return b;
}
- (void)viewDidLoad {
    [super viewDidLoad];self.view.backgroundColor=UIColor.systemBackgroundColor;
    UILabel *heading=[UILabel new];heading.text=@"Slay the Spire 2";heading.font=[UIFont boldSystemFontOfSize:30];
    UILabel *subtitle=[UILabel new];subtitle.text=@"Game installation • Steam • iOS";subtitle.textColor=UIColor.secondaryLabelColor;
    self.account=[self field:@"Steam account name" secure:NO];self.password=[self field:@"Steam password" secure:YES];
    self.branch=[self field:@"Branch: public, beta name or ? to list" secure:NO];self.branch.text=@"?";
    self.manifest=[self field:@"Exact manifest ID (optional)" secure:NO];
    self.beta=[self field:@"Private beta password (optional)" secure:YES];
    self.download=[self button:@"Sign in & install game" action:@selector(downloadPressed)];
    self.remember=[UISwitch new];
    UILabel *rememberLabel=[UILabel new];rememberLabel.text=@"Remember Steam session in Keychain";
    UIStackView *rememberRow=[[UIStackView alloc]initWithArrangedSubviews:@[rememberLabel,self.remember]];
    rememberRow.axis=UILayoutConstraintAxisHorizontal;rememberRow.spacing=8;
    self.saved=[self button:@"Use saved Steam session" action:@selector(savedPressed)];
    self.forget=[self button:@"Forget saved Steam session" action:@selector(forgetPressed)];
    self.play=[self button:@"Play installed game" action:@selector(playPressed)];
    self.cancel=[self button:@"Cancel operation" action:@selector(cancelPressed)];
    self.progress=[UIProgressView new];self.message=[UILabel new];self.message.numberOfLines=0;self.message.text=@"Waiting for JIT permission";
    UIStackView *stack=[[UIStackView alloc]initWithArrangedSubviews:@[heading,subtitle,self.account,self.password,self.branch,self.manifest,self.beta,rememberRow,self.download,self.saved,self.forget,self.play,self.progress,self.message,self.cancel]];
    stack.axis=UILayoutConstraintAxisVertical;stack.spacing=12;stack.translatesAutoresizingMaskIntoConstraints=NO;
    UIScrollView *scroll=[UIScrollView new];scroll.translatesAutoresizingMaskIntoConstraints=NO;[self.view addSubview:scroll];[scroll addSubview:stack];
    UILayoutGuide *safe=self.view.safeAreaLayoutGuide;
    [NSLayoutConstraint activateConstraints:@[[scroll.topAnchor constraintEqualToAnchor:safe.topAnchor],[scroll.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor],[scroll.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor],[scroll.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor],[stack.topAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.topAnchor constant:20],[stack.bottomAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.bottomAnchor constant:-20],[stack.leadingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.leadingAnchor constant:24],[stack.trailingAnchor constraintEqualToAnchor:scroll.contentLayoutGuide.trailingAnchor constant:-24],[stack.widthAnchor constraintEqualToAnchor:scroll.frameLayoutGuide.widthAnchor constant:-48]]];
    self.download.enabled=self.play.enabled=self.cancel.enabled=self.saved.enabled=self.forget.enabled=NO;
    self.timer=[NSTimer scheduledTimerWithTimeInterval:0.4 target:self selector:@selector(tick) userInfo:nil repeats:YES];
}
- (void)tick {
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
        dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0),^{
            void *lib=dlopen(library.fileSystemRepresentation,RTLD_NOW|RTLD_GLOBAL);
            auto init=lib?(InitRuntime)dlsym(lib,"sts2_initialize_game_runtime"):nullptr;
            auto get=lib?(GetDelegate)dlsym(lib,"sts2_host_delegate"):nullptr;
            int rc=-1;
            if(init&&get&&(rc=init(managed.UTF8String,nullptr,frameworks.UTF8String))==0 &&
               get("Initialize",(void**)&initHost)==0 && get("BeginDownload",(void**)&beginDownload)==0 &&
               get("Reply",(void**)&reply)==0 && get("Cancel",(void**)&cancelOperation)==0 &&
               get("ForgetSession",(void**)&forgetSession)==0 && get("Prepare",(void**)&prepareGame)==0 && get("CopyStatus",(void**)&copyStatus)==0)
                rc=initHost(payload.UTF8String,docs.UTF8String,managed.UTF8String);
            else if(rc==0)rc=-20;
            dispatch_async(dispatch_get_main_queue(),^{self.initializing=NO;self.initialized=rc==0;if(rc!=0){[self.timer invalidate];self.message.text=[NSString stringWithFormat:@"Runtime initialization failed (%d). Restart after enabling JIT.",rc];}});
        });return;
    }
    char buffer[65536];int length=copyStatus(buffer,sizeof(buffer));if(length<=0)return;
    NSDictionary *state=[NSJSONSerialization JSONObjectWithData:[NSData dataWithBytes:buffer length:length] options:0 error:nil];
    if(![state isKindOfClass:NSDictionary.class])return;
    NSString *stage=state[@"Stage"];NSString *message=state[@"Message"];
    BOOL idle=[@[@"idle",@"error",@"installed"] containsObject:stage];self.download.enabled=self.play.enabled=self.saved.enabled=self.forget.enabled=idle;self.cancel.enabled=!idle;
    double total=[state[@"Total"] doubleValue],done=[state[@"Completed"] doubleValue],speed=[state[@"Speed"] doubleValue];
    self.progress.progress=total>0?done/total:0;
    self.message.text=total>0?[NSString stringWithFormat:@"%@\n%.1f / %.1f MiB • %.2f MiB/s",message,done/1048576,total/1048576,speed/1048576]:message;
    if([stage isEqual:@"prompt"]){[self showPrompt:state];return;}
    self.promptSignature=nil;
    if(self.prompt){[self.prompt dismissViewControllerAnimated:YES completion:nil];self.prompt=nil;}
    if([stage isEqual:@"ready"]){
        [self.timer invalidate];self.message.text=@"Starting Godot…";
        UIWindow *old=self.window;BOOL ok=godotLaunch(self.service,@selector(application:didFinishLaunchingWithOptions:),self.application,self.options);
        if(ok){started=YES;forward(self.service,@selector(applicationDidBecomeActive:),self.application);old.hidden=YES;gate=nil;}
        else{self.message.text=@"Godot startup failed. Restart the app.";}
    }
}
- (void)showPrompt:(NSDictionary*)state {
    NSString *kind=state[@"Prompt"],*message=state[@"Message"];
    NSString *signature=[NSString stringWithFormat:@"%@:%@:%@",kind,message,state[@"PromptId"]];if([self.promptSignature isEqual:signature])return;
    self.promptSignature=signature;
    UIAlertController *alert=[UIAlertController alertControllerWithTitle:@"Steam" message:message preferredStyle:UIAlertControllerStyleAlert];self.prompt=alert;
    if([kind isEqual:@"branch"]){
        for(NSString *name in state[@"Branches"]) [alert addAction:[UIAlertAction actionWithTitle:name style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){reply(name.UTF8String);self.prompt=nil;}]];
    }else if([kind isEqual:@"confirm"]){
        [alert addAction:[UIAlertAction actionWithTitle:@"Wait for approval" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){reply("approve");self.prompt=nil;}]];
        [alert addAction:[UIAlertAction actionWithTitle:@"Enter code" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){reply("code");self.prompt=nil;}]];
    }else{
        [alert addTextFieldWithConfigurationHandler:^(UITextField *field){field.secureTextEntry=[kind isEqual:@"password"];field.autocapitalizationType=UITextAutocapitalizationTypeNone;}];
        [alert addAction:[UIAlertAction actionWithTitle:@"Submit" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){reply(alert.textFields.firstObject.text.UTF8String);self.prompt=nil;}]];
    }
    [alert addAction:[UIAlertAction actionWithTitle:@"Cancel" style:UIAlertActionStyleCancel handler:^(__unused UIAlertAction *a){cancelOperation();self.prompt=nil;}]];
    [self presentViewController:alert animated:YES completion:nil];
}
- (void)downloadPressed { [self startDownload:NO]; }
- (void)savedPressed { [self startDownload:YES]; }
- (void)forgetPressed { if(forgetSession()!=0)self.message.text=@"Unable to clear saved session."; }
- (void)startDownload:(BOOL)saved {
    NSDictionary *request=@{@"Username":self.account.text?:@"",@"Password":self.password.text?:@"",@"Branch":self.branch.text?:@"public",@"Manifest":self.manifest.text?:@"",@"BetaPassword":self.beta.text?:@"",@"Remember":@(self.remember.on),@"Saved":@(saved)};
    NSData *data=[NSJSONSerialization dataWithJSONObject:request options:0 error:nil];NSString *json=[[NSString alloc]initWithData:data encoding:NSUTF8StringEncoding];
    self.password.text=@"";self.beta.text=@"";[self.view endEditing:YES];int rc=beginDownload(json.UTF8String);
    if(rc!=0)self.message.text=[NSString stringWithFormat:@"Unable to start download (%d).",rc];
}
- (void)playPressed { [self.view endEditing:YES];if(prepareGame()!=0)self.message.text=@"Another operation is active."; }
- (void)cancelPressed {cancelOperation();}
@end
