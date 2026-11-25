// Embedded in HostGate.mm to share the launcher's choice table.
@interface LauncherInstalledMods : UIViewController
@property(nonatomic,copy) dispatch_block_t close;
- (void)showChoices:(NSArray<NSDictionary *> *)choices message:(NSString *)message reply:(void (^)(NSString *))reply;
- (void)showStatus:(NSString *)message;
@end

@interface LauncherInstalledMods ()
@property(nonatomic,strong) LauncherChoices *list;
@property(nonatomic,strong) UISearchTextField *search;
@property(nonatomic,strong) UILabel *status;
@property(nonatomic,copy) NSArray<NSDictionary *> *allChoices;
@end

@implementation LauncherInstalledMods
- (void)viewDidLoad {
    [super viewDidLoad];
    self.view.backgroundColor=UIColor.systemGroupedBackgroundColor;

    UIButtonConfiguration *backStyle=UIButtonConfiguration.plainButtonConfiguration;
    backStyle.title=@"Mods";
    backStyle.image=[UIImage systemImageNamed:@"chevron.backward"];
    backStyle.imagePadding=6;
    UIButton *back=[UIButton buttonWithConfiguration:backStyle primaryAction:nil];
    [back addTarget:self action:@selector(closePressed) forControlEvents:UIControlEventTouchUpInside];

    UILabel *heading=[UILabel new];
    heading.text=@"Installed mods";
    heading.font=[UIFont preferredFontForTextStyle:UIFontTextStyleTitle2];
    heading.adjustsFontForContentSizeCategory=YES;
    heading.accessibilityTraits|=UIAccessibilityTraitHeader;
    heading.numberOfLines=0;
    UIStackView *navigation=[[UIStackView alloc] initWithArrangedSubviews:@[back,heading,[UIView new]]];
    navigation.alignment=UIStackViewAlignmentCenter;
    navigation.spacing=8;
    [back setContentHuggingPriority:UILayoutPriorityRequired forAxis:UILayoutConstraintAxisHorizontal];

    self.search=[UISearchTextField new];
    self.search.placeholder=@"Search installed mods";
    self.search.accessibilityLabel=@"Search installed mods";
    [self.search addTarget:self action:@selector(filterChanged) forControlEvents:UIControlEventEditingChanged];

    self.status=[UILabel new];
    self.status.font=[UIFont preferredFontForTextStyle:UIFontTextStyleFootnote];
    self.status.adjustsFontForContentSizeCategory=YES;
    self.status.textColor=UIColor.secondaryLabelColor;
    self.status.numberOfLines=0;

    self.list=[[LauncherChoices alloc] initWithStyle:UITableViewStyleInsetGrouped];
    self.list.embedded=YES;
    [self addChildViewController:self.list];
    self.list.tableView.keyboardDismissMode=UIScrollViewKeyboardDismissModeOnDrag;
    UIStackView *stack=[[UIStackView alloc] initWithArrangedSubviews:@[navigation,self.search,self.status,self.list.view]];
    stack.axis=UILayoutConstraintAxisVertical;
    stack.spacing=10;
    stack.translatesAutoresizingMaskIntoConstraints=NO;
    [self.view addSubview:stack];
    [self.list didMoveToParentViewController:self];

    UILayoutGuide *safe=self.view.safeAreaLayoutGuide;
    NSLayoutConstraint *width=[stack.widthAnchor constraintEqualToAnchor:safe.widthAnchor constant:-32];
    width.priority=UILayoutPriorityDefaultHigh;
    NSLayoutConstraint *bottom=[stack.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor constant:-10];
    bottom.priority=UILayoutPriorityDefaultHigh;
    [NSLayoutConstraint activateConstraints:@[
        [stack.topAnchor constraintEqualToAnchor:safe.topAnchor constant:12],
        [stack.centerXAnchor constraintEqualToAnchor:safe.centerXAnchor],
        [stack.widthAnchor constraintLessThanOrEqualToConstant:780],width,bottom,
        [stack.bottomAnchor constraintLessThanOrEqualToAnchor:self.view.keyboardLayoutGuide.topAnchor constant:-10]
    ]];
}

- (void)showChoices:(NSArray<NSDictionary *> *)choices message:(NSString *)message reply:(void (^)(NSString *))reply {
    self.allChoices=choices;
    self.status.text=message;
    self.list.view.userInteractionEnabled=YES;
    __weak LauncherInstalledMods *weakSelf=self;
    self.list.selectValue=^(NSString *selection){
        LauncherInstalledMods *pane=weakSelf;
        if(!pane || !pane.list.view.userInteractionEnabled)return;
        pane.list.view.userInteractionEnabled=NO;
        [pane.view endEditing:YES];
        reply(selection);
    };
    [self refreshList];
}

- (void)showStatus:(NSString *)message {
    self.status.text=message;
    self.list.view.userInteractionEnabled=NO;
}

- (void)filterChanged {
    [self refreshList];
    [self.list.tableView setContentOffset:CGPointMake(0,-self.list.tableView.adjustedContentInset.top) animated:NO];
}

- (void)refreshList {
    NSString *query=[self.search.text stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    NSMutableArray<NSDictionary *> *rows=[NSMutableArray new];
    NSUInteger packages=0;
    for(NSDictionary *choice in self.allChoices){
        NSString *key=choice[@"Value"];
        BOOL setting=[@[@"loading",@"presets",@"groups"] containsObject:key];
        if(setting && query.length)continue;
        if(!setting && query.length && ![choice[@"Title"] localizedCaseInsensitiveContainsString:query]
            && ![choice[@"Detail"] localizedCaseInsensitiveContainsString:query])continue;
        if(!setting)packages++;
        [rows addObject:choice];
    }
    if(!packages && self.allChoices)
        [rows addObject:@{@"Value":@"",@"Title":query.length?@"No matching mods":@"No mods installed",
                          @"Detail":query.length?@"Try another name.":@"Import an archive or install a mod from Workshop."}];
    self.list.choices=rows;
    [self.list.tableView reloadData];
}

- (void)closePressed { if(self.close)self.close(); }
@end
