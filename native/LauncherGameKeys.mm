#include <cmath>

@interface LauncherGameKeys : UIView
@property(nonatomic,copy) void (^sendKey)(NSString *key, int modifiers);
- (void)installInWindow:(UIWindow *)window;
- (void)resetModifiers;
@end

@interface LauncherGameKeys ()
@property(nonatomic,strong) UIButton *toggle;
@property(nonatomic,strong) UISegmentedControl *pages;
@property(nonatomic,strong) UIStackView *panel,*rows;
@property(nonatomic,copy) NSArray<UIButton *> *modifierButtons;
@property(nonatomic,strong) NSLayoutConstraint *top,*width;
@property(nonatomic,weak) UIWindow *gameWindow;
@property(nonatomic,assign) int modifierMask;
@property(nonatomic,assign) CGFloat dragStart;
@end

@implementation LauncherGameKeys

- (UIButton *)keyButton:(NSString *)title code:(NSString *)code {
    UIButtonConfiguration *style=UIButtonConfiguration.grayButtonConfiguration;
    style.title=title;
    style.buttonSize=UIButtonConfigurationSizeMini;
    style.contentInsets=NSDirectionalEdgeInsetsMake(4,2,4,2);
    UIButton *button=[UIButton buttonWithConfiguration:style primaryAction:nil];
    button.accessibilityIdentifier=code;
    if(code)button.accessibilityLabel=[NSString stringWithFormat:@"Press %@",title];
    [button.heightAnchor constraintGreaterThanOrEqualToConstant:34].active=YES;
    return button;
}

- (void)installInWindow:(UIWindow *)window {
    self.gameWindow=window;
    self.translatesAutoresizingMaskIntoConstraints=NO;
    self.backgroundColor=UIColor.secondarySystemGroupedBackgroundColor;
    self.layer.cornerRadius=13;
    self.layer.shadowColor=UIColor.blackColor.CGColor;
    self.layer.shadowOpacity=0.25;
    self.layer.shadowRadius=6;

    UIButtonConfiguration *style=UIButtonConfiguration.tintedButtonConfiguration;
    style.image=[UIImage systemImageNamed:@"keyboard"];
    style.cornerStyle=UIButtonConfigurationCornerStyleMedium;
    style.contentInsets=NSDirectionalEdgeInsetsMake(8,8,8,8);
    self.toggle=[UIButton buttonWithConfiguration:style primaryAction:nil];
    self.toggle.accessibilityLabel=@"Show game keyboard";
    [self.toggle.widthAnchor constraintEqualToConstant:44].active=YES;
    [self.toggle.heightAnchor constraintEqualToConstant:44].active=YES;
    [self.toggle addTarget:self action:@selector(togglePanel) forControlEvents:UIControlEventTouchUpInside];
    UIPanGestureRecognizer *pan=[[UIPanGestureRecognizer alloc] initWithTarget:self action:@selector(movePanel:)];
    [self.toggle addGestureRecognizer:pan];

    self.pages=[[UISegmentedControl alloc] initWithItems:@[@"Letters",@"Function keys",@"Controls"]];
    self.pages.selectedSegmentIndex=0;
    [self.pages addTarget:self action:@selector(showKeys) forControlEvents:UIControlEventValueChanged];
    self.rows=[[UIStackView alloc] init];
    self.rows.axis=UILayoutConstraintAxisVertical;
    self.rows.spacing=4;

    NSMutableArray<UIButton *> *modifiers=[NSMutableArray new];
    for(NSString *label in @[@"Shift",@"Ctrl",@"Alt",@"Cmd"]){
        UIButton *button=[self keyButton:label code:nil];
        button.tag=modifiers.count;
        [button addTarget:self action:@selector(modifierPressed:) forControlEvents:UIControlEventTouchUpInside];
        [modifiers addObject:button];
    }
    self.modifierButtons=modifiers;
    UIStackView *modifierRow=[[UIStackView alloc] initWithArrangedSubviews:modifiers];
    modifierRow.spacing=4;
    modifierRow.distribution=UIStackViewDistributionFillEqually;
    self.panel=[[UIStackView alloc] initWithArrangedSubviews:@[self.pages,modifierRow,self.rows]];
    self.panel.axis=UILayoutConstraintAxisVertical;
    self.panel.spacing=6;
    self.panel.hidden=YES;

    UIView *handle=[UIView new];
    handle.translatesAutoresizingMaskIntoConstraints=NO;
    [handle addSubview:self.toggle];
    self.toggle.translatesAutoresizingMaskIntoConstraints=NO;
    [NSLayoutConstraint activateConstraints:@[
        [self.toggle.topAnchor constraintEqualToAnchor:handle.topAnchor],
        [self.toggle.trailingAnchor constraintEqualToAnchor:handle.trailingAnchor],
        [self.toggle.bottomAnchor constraintEqualToAnchor:handle.bottomAnchor]
    ]];
    UIStackView *content=[[UIStackView alloc] initWithArrangedSubviews:@[handle,self.panel]];
    content.axis=UILayoutConstraintAxisVertical;
    content.spacing=6;
    content.translatesAutoresizingMaskIntoConstraints=NO;
    [self addSubview:content];
    [window addSubview:self];
    UILayoutGuide *safe=window.safeAreaLayoutGuide;
    self.top=[self.topAnchor constraintEqualToAnchor:safe.topAnchor constant:76];
    self.width=[self.widthAnchor constraintEqualToConstant:52];
    self.width.priority=UILayoutPriorityDefaultHigh;
    [NSLayoutConstraint activateConstraints:@[
        self.top,self.width,[self.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor constant:-8],
        [self.widthAnchor constraintLessThanOrEqualToAnchor:safe.widthAnchor constant:-16],
        [content.topAnchor constraintEqualToAnchor:self.topAnchor constant:4],
        [content.bottomAnchor constraintEqualToAnchor:self.bottomAnchor constant:-4],
        [content.leadingAnchor constraintEqualToAnchor:self.leadingAnchor constant:4],
        [content.trailingAnchor constraintEqualToAnchor:self.trailingAnchor constant:-4]
    ]];
}

- (void)togglePanel {
    self.panel.hidden=!self.panel.hidden;
    BOOL collapsed=self.panel.hidden;
    self.width.constant=collapsed?52:366;
    UIButtonConfiguration *style=self.toggle.configuration;
    style.image=[UIImage systemImageNamed:collapsed?@"keyboard":@"keyboard.chevron.compact.down"];
    self.toggle.configuration=style;
    self.toggle.accessibilityLabel=collapsed?@"Show game keyboard":@"Minimize game keyboard";
    if(collapsed)[self resetModifiers];
    else [self showKeys];
    [self.gameWindow layoutIfNeeded];
    [self moveToTop:self.top.constant];
}

- (void)moveToTop:(CGFloat)position {
    CGFloat maxTop=MAX(4,self.gameWindow.safeAreaLayoutGuide.layoutFrame.size.height-self.bounds.size.height-4);
    CGFloat clamped=MIN(MAX(4,position),maxTop);
    if(std::abs(self.top.constant-clamped)>0.5)self.top.constant=clamped;
}

- (void)movePanel:(UIPanGestureRecognizer *)gesture {
    if(gesture.state==UIGestureRecognizerStateBegan)self.dragStart=self.top.constant;
    [self moveToTop:self.dragStart+[gesture translationInView:self.gameWindow].y];
}

- (void)safeAreaInsetsDidChange {
    [super safeAreaInsetsDidChange];
    [self moveToTop:self.top.constant];
}

- (void)layoutSubviews {
    [super layoutSubviews];
    [self moveToTop:self.top.constant];
}

- (void)modifierPressed:(UIButton *)sender {
    self.modifierMask^=1 << sender.tag;
    [self updateModifiers];
}

- (void)resetModifiers {
    self.modifierMask=0;
    [self updateModifiers];
}

- (void)updateModifiers {
    for(UIButton *button in self.modifierButtons){
        BOOL selected=(self.modifierMask & (1 << button.tag))!=0;
        UIButtonConfiguration *style=button.configuration;
        style.baseBackgroundColor=selected?UIColor.systemIndigoColor:nil;
        style.baseForegroundColor=selected?UIColor.whiteColor:nil;
        button.configuration=style;
        if(selected)button.accessibilityTraits|=UIAccessibilityTraitSelected;
        else button.accessibilityTraits&=~UIAccessibilityTraitSelected;
    }
}

- (void)sendPressed:(UIButton *)button {
    if(self.sendKey)self.sendKey(button.accessibilityIdentifier,self.modifierMask);
    [self resetModifiers];
}

- (void)showKeys {
    for(UIView *row in self.rows.arrangedSubviews){
        [self.rows removeArrangedSubview:row];
        [row removeFromSuperview];
    }
    NSArray<NSArray<NSString *> *> *pages;
    NSDictionary<NSString *,NSString *> *aliases=@{
        @"Esc":@"Escape",@"⌫":@"Backspace",@"↵":@"Enter",@"`":@"Quoteleft",@"−":@"Minus",
        @"=":@"Equal",@"[":@"Bracketleft",@"]":@"Bracketright",@"\\":@"Backslash",
        @";":@"Semicolon",@"'":@"Apostrophe",@",":@"Comma",@".":@"Period",@"/":@"Slash",
        @"↑":@"Up",@"↓":@"Down",@"←":@"Left",@"→":@"Right",@"PgUp":@"Pageup",@"PgDn":@"Pagedown"
    };
    switch(self.pages.selectedSegmentIndex){
        case 1:
            pages=@[@[@"F1",@"F2",@"F3",@"F4",@"F5",@"F6"],@[@"F7",@"F8",@"F9",@"F10",@"F11",@"F12"]];
            break;
        case 2:
            pages=@[@[@"Esc",@"Tab",@"↵",@"Space",@"⌫"],@[@"←",@"↑",@"↓",@"→"],@[@"Home",@"End",@"PgUp",@"PgDn",@"Delete"],
                    @[@"`",@"−",@"=",@"[",@"]",@"\\"],@[@";",@"'",@",",@".",@"/"]];
            break;
        default:
            pages=@[@[@"1",@"2",@"3",@"4",@"5",@"6",@"7",@"8",@"9",@"0"],
                    @[@"Q",@"W",@"E",@"R",@"T",@"Y",@"U",@"I",@"O",@"P"],
                    @[@"A",@"S",@"D",@"F",@"G",@"H",@"J",@"K",@"L"],
                    @[@"Z",@"X",@"C",@"V",@"B",@"N",@"M"]];
            break;
    }
    for(NSArray<NSString *> *labels in pages){
        NSMutableArray<UIButton *> *buttons=[NSMutableArray new];
        for(NSString *label in labels){
            NSString *code=aliases[label]?:label;
            if(code.length==1 && [NSCharacterSet.decimalDigitCharacterSet characterIsMember:[code characterAtIndex:0]])
                code=[@"Key" stringByAppendingString:code];
            UIButton *key=[self keyButton:label code:code];
            [key addTarget:self action:@selector(sendPressed:) forControlEvents:UIControlEventTouchUpInside];
            [buttons addObject:key];
        }
        UIStackView *row=[[UIStackView alloc] initWithArrangedSubviews:buttons];
        row.spacing=4;
        row.distribution=UIStackViewDistributionFillEqually;
        [self.rows addArrangedSubview:row];
    }
}
@end
