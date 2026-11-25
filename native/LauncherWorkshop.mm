// Embedded by HostGate.mm so Workshop can reuse the launcher's choice lists.
typedef void (^WorkshopReply)(NSString *);

@interface LauncherWorkshop : UIViewController<UITextFieldDelegate>
@property(nonatomic,copy) dispatch_block_t close;
- (void)showPrompt:(NSDictionary *)state reply:(WorkshopReply)reply;
- (void)showStatus:(NSString *)message progress:(double)progress busy:(BOOL)busy;
@end

@interface LauncherWorkshop ()
@property(nonatomic,strong) LauncherChoices *sidebar,*results;
@property(nonatomic,strong) UIStackView *columns,*form;
@property(nonatomic,strong) UILabel *titleLabel,*statusLabel;
@property(nonatomic,strong) UIProgressView *progress;
@property(nonatomic,strong) UITextField *input;
@property(nonatomic,strong) UIButton *submitButton;
@property(nonatomic,strong) NSLayoutConstraint *sidebarWidth,*sidebarHeight;
@property(nonatomic,copy) WorkshopReply reply;
@property(nonatomic,copy) NSArray<NSDictionary *> *categories;
@property(nonatomic,copy) NSString *savedPage;
@property(nonatomic,assign) CGPoint savedOffset;
@end

@implementation LauncherWorkshop
- (void)viewDidLoad {
    [super viewDidLoad];
    self.view.backgroundColor=UIColor.systemBackgroundColor;
    self.sidebar=[self addList];
    self.results=[self addList];

    UIButtonConfiguration *backStyle=UIButtonConfiguration.plainButtonConfiguration;
    backStyle.title=@"Mods";
    backStyle.image=[UIImage systemImageNamed:@"chevron.backward"];
    backStyle.imagePadding=5;
    UIButton *back=[UIButton buttonWithConfiguration:backStyle primaryAction:nil];
    [back addTarget:self action:@selector(closePressed) forControlEvents:UIControlEventTouchUpInside];
    UILabel *heading=[UILabel new];
    heading.text=@"Steam Workshop";
    heading.font=[UIFont preferredFontForTextStyle:UIFontTextStyleTitle2];
    heading.adjustsFontForContentSizeCategory=YES;
    heading.accessibilityTraits|=UIAccessibilityTraitHeader;
    UIStackView *navigation=[[UIStackView alloc] initWithArrangedSubviews:@[back,heading,[UIView new]]];
    navigation.alignment=UIStackViewAlignmentCenter;
    navigation.spacing=12;

    self.titleLabel=[UILabel new];
    self.titleLabel.font=[UIFont preferredFontForTextStyle:UIFontTextStyleHeadline];
    self.titleLabel.adjustsFontForContentSizeCategory=YES;
    self.titleLabel.accessibilityTraits|=UIAccessibilityTraitHeader;
    self.titleLabel.numberOfLines=0;
    self.statusLabel=[UILabel new];
    self.statusLabel.font=[UIFont preferredFontForTextStyle:UIFontTextStyleSubheadline];
    self.statusLabel.adjustsFontForContentSizeCategory=YES;
    self.statusLabel.textColor=UIColor.secondaryLabelColor;
    self.statusLabel.numberOfLines=0;
    self.progress=[UIProgressView new];
    self.input=[UITextField new];
    self.input.borderStyle=UITextBorderStyleRoundedRect;
    self.input.autocapitalizationType=UITextAutocapitalizationTypeNone;
    self.input.autocorrectionType=UITextAutocorrectionTypeNo;
    self.input.returnKeyType=UIReturnKeyGo;
    self.input.delegate=self;
    self.submitButton=[UIButton buttonWithType:UIButtonTypeSystem];
    self.submitButton.configuration=UIButtonConfiguration.filledButtonConfiguration;
    [self.submitButton addTarget:self action:@selector(submitPressed) forControlEvents:UIControlEventTouchUpInside];
    self.form=[[UIStackView alloc] initWithArrangedSubviews:@[self.input,self.submitButton]];
    self.form.spacing=8;
    self.form.hidden=YES;
    [self.submitButton setContentHuggingPriority:UILayoutPriorityRequired forAxis:UILayoutConstraintAxisHorizontal];

    UIStackView *content=[[UIStackView alloc] initWithArrangedSubviews:@[self.titleLabel,self.statusLabel,self.progress,self.form,self.results.view]];
    content.axis=UILayoutConstraintAxisVertical;
    content.spacing=8;
    self.columns=[[UIStackView alloc] initWithArrangedSubviews:@[self.sidebar.view,content]];
    self.columns.spacing=14;
    self.sidebarWidth=[self.sidebar.view.widthAnchor constraintEqualToConstant:220];
    self.sidebarHeight=[self.sidebar.view.heightAnchor constraintEqualToConstant:156];
    self.sidebarHeight.priority=UILayoutPriorityDefaultHigh;
    UIStackView *screen=[[UIStackView alloc] initWithArrangedSubviews:@[navigation,self.columns]];
    screen.axis=UILayoutConstraintAxisVertical;
    screen.spacing=12;
    screen.translatesAutoresizingMaskIntoConstraints=NO;
    [self.view addSubview:screen];
    [self.sidebar didMoveToParentViewController:self];
    [self.results didMoveToParentViewController:self];
    UILayoutGuide *safe=self.view.safeAreaLayoutGuide;
    NSLayoutConstraint *bottom=[screen.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor constant:-10];
    bottom.priority=UILayoutPriorityDefaultHigh;
    [NSLayoutConstraint activateConstraints:@[
        [screen.topAnchor constraintEqualToAnchor:safe.topAnchor constant:10],
        [screen.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor constant:16],
        [screen.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor constant:-16],
        bottom,[screen.bottomAnchor constraintLessThanOrEqualToAnchor:self.view.keyboardLayoutGuide.topAnchor constant:-10]
    ]];
}

- (LauncherChoices *)addList {
    LauncherChoices *list=[[LauncherChoices alloc] initWithStyle:UITableViewStyleInsetGrouped];
    list.embedded=YES;
    [self addChildViewController:list];
    return list;
}

- (void)viewDidLayoutSubviews {
    [super viewDidLayoutSubviews];
    BOOL compact=self.view.bounds.size.width<620;
    UILayoutConstraintAxis axis=compact?UILayoutConstraintAxisVertical:UILayoutConstraintAxisHorizontal;
    if(self.columns.axis==axis && (self.sidebarWidth.active || self.sidebarHeight.active))return;
    self.sidebarWidth.active=NO;
    self.sidebarHeight.active=NO;
    self.columns.axis=axis;
    if(compact) self.sidebarHeight.active=YES;
    else self.sidebarWidth.active=YES;
}

- (void)showPrompt:(NSDictionary *)state reply:(WorkshopReply)reply {
    NSString *kind=state[@"Prompt"];
    NSString *section=[state[@"WorkshopSection"] isKindOfClass:NSString.class]?state[@"WorkshopSection"]:@"";
    NSString *message=state[@"Message"];
    NSArray *choices=[state[@"Choices"] isKindOfClass:NSArray.class]?state[@"Choices"]:nil;
    NSArray *categories=[state[@"WorkshopSections"] isKindOfClass:NSArray.class]?state[@"WorkshopSections"]:@[];
    self.reply=reply;
    self.progress.hidden=YES;
    self.statusLabel.hidden=YES;
    self.sidebar.selectedValue=section;
    if(![self.categories isEqualToArray:categories]) {
        self.categories=categories;
        NSMutableArray *menu=[NSMutableArray arrayWithCapacity:categories.count];
        for(NSDictionary *category in categories)
            [menu addObject:@{@"Value":category[@"Value"],@"Title":category[@"Title"],@"Detail":@""}];
        self.sidebar.choices=menu;
    }
    [self.sidebar.tableView reloadData];
    BOOL categoriesAvailable=[kind hasPrefix:@"workshop-"];
    self.sidebar.tableView.allowsSelection=categoriesAvailable;
    BOOL home=[kind isEqual:@"workshop-home"];
    BOOL page=[kind isEqual:@"workshop-page"];
    NSString *pageID=page?[NSString stringWithFormat:@"%@/%@",section,message]:nil;
    self.titleLabel.text=home?@"Browse":message;
    if(home){
        self.statusLabel.text=message;
        self.statusLabel.hidden=NO;
        choices=@[];
    }
    if([kind isEqual:@"confirm"]){
        choices=@[@{@"Value":@"approve",@"Title":@"Wait for approval"},@{@"Value":@"code",@"Title":@"Enter code"}];
    }
    BOOL textInput=choices==nil;
    self.form.hidden=!textInput;
    if(textInput){
        self.input.text=@"";
        self.input.accessibilityLabel=message;
        self.input.secureTextEntry=[@[@"steam-password",@"password"] containsObject:kind];
        self.input.keyboardType=[kind isEqual:@"workshop-id"]?UIKeyboardTypeURL:UIKeyboardTypeDefault;
        self.input.textContentType=self.input.secureTextEntry?UITextContentTypePassword:
            [kind isEqual:@"steam-user"]?UITextContentTypeUsername:nil;
        UIButtonConfiguration *style=self.submitButton.configuration;
        style.title=[kind isEqual:@"workshop-query"]?@"Search":[kind isEqual:@"workshop-id"]?@"Open":@"Continue";
        self.submitButton.configuration=style;
    }
    self.results.choices=choices ?: @[];
    self.results.tableView.allowsSelection=YES;
    [self.results.tableView reloadData];
    [self.results.tableView layoutIfNeeded];
    CGPoint offset=page && [self.savedPage isEqual:pageID]?self.savedOffset:CGPointMake(0,-self.results.tableView.adjustedContentInset.top);
    [self.results.tableView setContentOffset:offset animated:NO];
    __weak LauncherWorkshop *weakSelf=self;
    self.sidebar.selectValue=^(NSString *value) {
        weakSelf.savedPage=nil;
        [weakSelf sendReply:[@"workshop-section:" stringByAppendingString:value]];
    };
    self.results.selectValue=^(NSString *value) {
        LauncherWorkshop *pane=weakSelf;
        if(page && ![@[@"previous",@"next"] containsObject:value]) {
            pane.savedPage=pageID;
            pane.savedOffset=pane.results.tableView.contentOffset;
        }else if(page) pane.savedPage=nil;
        [pane sendReply:value];
    };
}

- (void)showStatus:(NSString *)message progress:(double)progress busy:(BOOL)busy {
    self.reply=nil;
    self.input.text=@"";
    self.form.hidden=YES;
    self.statusLabel.hidden=NO;
    self.statusLabel.text=message;
    self.progress.hidden=progress<0;
    if(progress>=0) self.progress.progress=progress;
    self.sidebar.tableView.allowsSelection=NO;
    self.results.tableView.allowsSelection=NO;
    if(!busy) self.titleLabel.text=@"Workshop";
}

- (void)sendReply:(NSString *)value {
    WorkshopReply reply=self.reply;
    if(!reply)return;
    [self.view endEditing:YES];
    [self showStatus:@"Working…" progress:-1 busy:YES];
    reply(value);
}
- (void)submitPressed { [self sendReply:self.input.text ?: @""]; }
- (BOOL)textFieldShouldReturn:(UITextField *)textField { [self submitPressed];return YES; }
- (void)closePressed { if(self.close) self.close(); }
@end
