#import <UIKit/UIKit.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#import <QuickLook/QuickLook.h>

@interface LauncherFile : NSObject
@property(nonatomic,strong) NSURL *url;
@property(nonatomic,strong) NSDate *modified;
@property(nonatomic) unsigned long long size;
@property(nonatomic) BOOL directory;
@end

@implementation LauncherFile
@end

static BOOL launcherFileInside(NSURL *location, NSURL *root) {
    NSString *path=location.URLByStandardizingPath.path;
    NSString *base=root.URLByStandardizingPath.path;
    return [path isEqualToString:location.URLByResolvingSymlinksInPath.URLByStandardizingPath.path] &&
        ([path isEqualToString:base] || [path hasPrefix:[base stringByAppendingString:@"/"]]);
}

@interface LauncherFiles : UITableViewController<UIDocumentPickerDelegate,QLPreviewControllerDataSource>
@property(nonatomic,strong) NSURL *root;
@property(nonatomic,strong) NSURL *folder;
@property(nonatomic,strong) NSURL *preview;
@property(nonatomic,copy) NSArray<LauncherFile *> *files;
@property(nonatomic,copy) NSString *sort;
@property(nonatomic) NSUInteger loadGeneration;
@end

@implementation LauncherFiles
- (void)viewDidLoad {
    [super viewDidLoad];
    BOOL top=[self.folder isEqual:self.root];
    self.title=top?@"Launcher files":self.folder.lastPathComponent;
    if(top) self.navigationItem.leftBarButtonItem=[[UIBarButtonItem alloc] initWithBarButtonSystemItem:UIBarButtonSystemItemDone
        target:self action:@selector(closeFiles)];
    __weak LauncherFiles *weakSelf=self;
    self.navigationItem.rightBarButtonItems=@[
        [[UIBarButtonItem alloc] initWithImage:[UIImage systemImageNamed:@"plus"] menu:
            [UIMenu menuWithChildren:@[
                [UIAction actionWithTitle:@"Import files" image:[UIImage systemImageNamed:@"square.and.arrow.down"] identifier:nil
                    handler:^(__unused UIAction *a){ [weakSelf importFiles]; }],
                [UIAction actionWithTitle:@"New folder" image:[UIImage systemImageNamed:@"folder.badge.plus"] identifier:nil
                    handler:^(__unused UIAction *a){ [weakSelf askForName:nil]; }]
            ]]],
        [[UIBarButtonItem alloc] initWithImage:[UIImage systemImageNamed:@"line.3.horizontal.decrease"] menu:[self sortMenu]]
    ];
    self.tableView.rowHeight=56;
    self.refreshControl=[UIRefreshControl new];
    [self.refreshControl addTarget:self action:@selector(loadFiles) forControlEvents:UIControlEventValueChanged];
}
- (void)viewWillAppear:(BOOL)animated {
    [super viewWillAppear:animated];
    [self loadFiles];
}
- (void)closeFiles { [self dismissViewControllerAnimated:YES completion:nil]; }
- (UIMenu *)sortMenu {
    NSMutableArray<UIAction *> *actions=[NSMutableArray array];
    NSDictionary<NSString *,NSString *> *names=@{@"name":@"Name",@"modified":@"Date modified",@"size":@"Size"};
    __weak LauncherFiles *weakSelf=self;
    for(NSString *key in @[@"name",@"modified",@"size"]){
        UIAction *action=[UIAction actionWithTitle:names[key] image:nil identifier:nil handler:^(__unused UIAction *a){
            LauncherFiles *browser=weakSelf;
            if(!browser)return;
            browser.sort=key;
            browser.navigationItem.rightBarButtonItems.lastObject.menu=[browser sortMenu];
            [browser loadFiles];
        }];
        action.state=[self.sort isEqualToString:key]?UIMenuElementStateOn:UIMenuElementStateOff;
        [actions addObject:action];
    }
    return [UIMenu menuWithTitle:@"Sort by" children:actions];
}
- (void)loadFiles {
    NSURL *folder=self.folder,*root=self.root;
    NSString *sort=self.sort;
    NSUInteger generation=++self.loadGeneration;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0),^{
        NSFileManager *manager=[NSFileManager defaultManager];
        NSArray<NSURLResourceKey> *keys=@[NSURLIsSymbolicLinkKey,NSURLIsDirectoryKey,NSURLIsRegularFileKey,NSURLFileSizeKey,NSURLContentModificationDateKey];
        NSError *error=nil;
        NSArray<NSURL *> *items=launcherFileInside(folder,root)?[manager contentsOfDirectoryAtURL:folder
            includingPropertiesForKeys:keys options:NSDirectoryEnumerationSkipsHiddenFiles error:&error]:nil;
        NSMutableArray<LauncherFile *> *rows=[NSMutableArray array];
        for(NSURL *url in items){
            NSDictionary<NSURLResourceKey,id> *values=[url resourceValuesForKeys:keys error:nil];
            BOOL directory=[values[NSURLIsDirectoryKey] boolValue];
            if([values[NSURLIsSymbolicLinkKey] boolValue] || (!directory && ![values[NSURLIsRegularFileKey] boolValue]))continue;
            LauncherFile *item=[LauncherFile new];
            item.url=url;
            item.directory=directory;
            item.size=[values[NSURLFileSizeKey] unsignedLongLongValue];
            item.modified=values[NSURLContentModificationDateKey];
            [rows addObject:item];
        }
        [rows sortUsingComparator:^NSComparisonResult(LauncherFile *left,LauncherFile *right){
            if(left.directory!=right.directory)return left.directory?NSOrderedAscending:NSOrderedDescending;
            if([sort isEqualToString:@"modified"] && left.modified && right.modified){
                NSComparisonResult date=[right.modified compare:left.modified];
                if(date!=NSOrderedSame)return date;
            }
            if([sort isEqualToString:@"size"] && left.size!=right.size)return left.size>right.size?NSOrderedAscending:NSOrderedDescending;
            return [left.url.lastPathComponent localizedStandardCompare:right.url.lastPathComponent];
        }];
        dispatch_async(dispatch_get_main_queue(),^{
            if(generation!=self.loadGeneration)return;
            self.files=rows;
            [self.refreshControl endRefreshing];
            UILabel *empty=[UILabel new];
            empty.text=items?@"This folder is empty.":@"This folder could not be opened.";
            empty.textColor=UIColor.secondaryLabelColor;
            empty.textAlignment=NSTextAlignmentCenter;
            self.tableView.backgroundView=rows.count?nil:empty;
            [self.tableView reloadData];
        });
    });
}
- (NSInteger)tableView:(UITableView *)tableView numberOfRowsInSection:(NSInteger)section { return self.files.count; }
- (UITableViewCell *)tableView:(UITableView *)tableView cellForRowAtIndexPath:(NSIndexPath *)indexPath {
    UITableViewCell *cell=[tableView dequeueReusableCellWithIdentifier:@"File"];
    if(!cell)cell=[[UITableViewCell alloc] initWithStyle:UITableViewCellStyleSubtitle reuseIdentifier:@"File"];
    LauncherFile *file=self.files[indexPath.row];
    cell.textLabel.text=file.url.lastPathComponent;
    cell.detailTextLabel.text=file.directory?@"Folder":[NSByteCountFormatter stringFromByteCount:(long long)file.size countStyle:NSByteCountFormatterCountStyleFile];
    cell.imageView.image=[UIImage systemImageNamed:file.directory?@"folder":@"doc"];
    cell.accessoryType=file.directory?UITableViewCellAccessoryDisclosureIndicator:UITableViewCellAccessoryNone;
    return cell;
}
- (void)tableView:(UITableView *)tableView didSelectRowAtIndexPath:(NSIndexPath *)indexPath {
    [tableView deselectRowAtIndexPath:indexPath animated:YES];
    LauncherFile *file=self.files[indexPath.row];
    if(!launcherFileInside(file.url,self.root)){[self showFileError:@"This file is no longer available."];return;}
    if(file.directory){
        LauncherFiles *next=[[LauncherFiles alloc] initWithStyle:UITableViewStyleInsetGrouped];
        next.root=self.root;next.folder=file.url;next.sort=self.sort;
        [self.navigationController pushViewController:next animated:YES];
        return;
    }
    if(![QLPreviewController canPreviewItem:(id<QLPreviewItem>)file.url]){[self exportFile:file.url];return;}
    self.preview=file.url;
    QLPreviewController *viewer=[QLPreviewController new];
    viewer.dataSource=self;
    [self.navigationController pushViewController:viewer animated:YES];
}
- (NSInteger)numberOfPreviewItemsInPreviewController:(QLPreviewController *)controller { return self.preview?1:0; }
- (id<QLPreviewItem>)previewController:(QLPreviewController *)controller previewItemAtIndex:(NSInteger)index {
    return (id<QLPreviewItem>)self.preview;
}
- (UIContextMenuConfiguration *)tableView:(UITableView *)tableView contextMenuConfigurationForRowAtIndexPath:(NSIndexPath *)indexPath
    point:(CGPoint)point {
    NSURL *item=self.files[indexPath.row].url;
    __weak LauncherFiles *weakSelf=self;
    return [UIContextMenuConfiguration configurationWithIdentifier:nil previewProvider:nil actionProvider:^UIMenu *(__unused NSArray *suggested){
        UIAction *exportAction=[UIAction actionWithTitle:@"Export copy" image:[UIImage systemImageNamed:@"square.and.arrow.up"] identifier:nil
            handler:^(__unused UIAction *a){[weakSelf exportFile:item];}];
        UIAction *rename=[UIAction actionWithTitle:@"Rename" image:[UIImage systemImageNamed:@"pencil"] identifier:nil
            handler:^(__unused UIAction *a){[weakSelf askForName:item];}];
        UIAction *remove=[UIAction actionWithTitle:@"Delete" image:[UIImage systemImageNamed:@"trash"] identifier:nil
            handler:^(__unused UIAction *a){[weakSelf confirmDeletion:item];}];
        remove.attributes=UIMenuElementAttributesDestructive;
        return [UIMenu menuWithChildren:@[exportAction,rename,remove]];
    }];
}
- (void)showFileError:(NSString *)message {
    UIAlertController *alert=[UIAlertController alertControllerWithTitle:@"Launcher files" message:message preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:@"OK" style:UIAlertActionStyleDefault handler:nil]];
    [self presentViewController:alert animated:YES completion:nil];
}
- (void)askForName:(NSURL *)item {
    UIAlertController *alert=[UIAlertController alertControllerWithTitle:item?@"Rename":@"New folder" message:nil preferredStyle:UIAlertControllerStyleAlert];
    [alert addTextFieldWithConfigurationHandler:^(UITextField *field){field.text=item.lastPathComponent;field.placeholder=@"Name";}];
    [alert addAction:[UIAlertAction actionWithTitle:@"Cancel" style:UIAlertActionStyleCancel handler:nil]];
    __weak UIAlertController *weakAlert=alert;
    [alert addAction:[UIAlertAction actionWithTitle:item?@"Rename":@"Create" style:UIAlertActionStyleDefault handler:^(__unused UIAlertAction *a){
        NSString *name=[weakAlert.textFields.firstObject.text stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
        NSCharacterSet *invalid=[NSCharacterSet characterSetWithCharactersInString:@"/\\:"];
        if(name.length==0 || [name hasPrefix:@"."] || [name rangeOfCharacterFromSet:invalid].location!=NSNotFound ||
            [name rangeOfCharacterFromSet:NSCharacterSet.controlCharacterSet].location!=NSNotFound){
            [self showFileError:@"Enter a name without slashes, colons or leading dots."];return;
        }
        NSURL *destination=[self.folder URLByAppendingPathComponent:name];
        if(!launcherFileInside(destination,self.root) || (item && !launcherFileInside(item,self.root))){
            [self showFileError:@"Choose a location within the launcher’s files."];return;
        }
        NSFileManager *manager=[NSFileManager defaultManager];
        if([item isEqual:destination])return;
        BOOL success=item?[manager moveItemAtURL:item toURL:destination error:nil]:
            [manager createDirectoryAtURL:destination withIntermediateDirectories:NO attributes:nil error:nil];
        if(!success)[self showFileError:@"The file operation failed. Check the location and name."];
        [self loadFiles];
    }]];
    [self presentViewController:alert animated:YES completion:nil];
}
- (void)confirmDeletion:(NSURL *)item {
    UIAlertController *alert=[UIAlertController alertControllerWithTitle:@"Delete file or folder?" message:item.lastPathComponent
        preferredStyle:UIAlertControllerStyleAlert];
    [alert addAction:[UIAlertAction actionWithTitle:@"Cancel" style:UIAlertActionStyleCancel handler:nil]];
    [alert addAction:[UIAlertAction actionWithTitle:@"Delete" style:UIAlertActionStyleDestructive handler:^(__unused UIAlertAction *a){
        if(!launcherFileInside(item,self.root) || ![[NSFileManager defaultManager] removeItemAtURL:item error:nil])
            [self showFileError:@"The item could not be deleted."];
        [self loadFiles];
    }]];
    [self presentViewController:alert animated:YES completion:nil];
}
- (void)exportFile:(NSURL *)item {
    if(!launcherFileInside(item,self.root) || ![[NSFileManager defaultManager] fileExistsAtPath:item.path]){
        [self showFileError:@"This file is no longer available."];return;
    }
    UIDocumentPickerViewController *picker=[[UIDocumentPickerViewController alloc] initForExportingURLs:@[item] asCopy:YES];
    [self presentViewController:picker animated:YES completion:nil];
}
- (void)importFiles {
    UIDocumentPickerViewController *picker=[[UIDocumentPickerViewController alloc] initForOpeningContentTypes:@[UTTypeData] asCopy:NO];
    picker.allowsMultipleSelection=YES;
    picker.delegate=self;
    [self presentViewController:picker animated:YES completion:nil];
}
- (void)documentPicker:(UIDocumentPickerViewController *)controller didPickDocumentsAtURLs:(NSArray<NSURL *> *)urls {
    NSURL *folder=self.folder,*root=self.root;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0),^{
        NSUInteger failed=0;
        for(NSURL *source in urls){
            BOOL scope=[source startAccessingSecurityScopedResource];
            NSString *name=source.lastPathComponent;
            NSURL *destination=[folder URLByAppendingPathComponent:name];
            if(name.length==0 || [name hasPrefix:@"."] || !launcherFileInside(destination,root)){
                failed++;
            }else{
                NSFileCoordinator *coordinator=[[NSFileCoordinator alloc] initWithFilePresenter:nil];
                NSError *error=nil;
                __block BOOL copied=NO;
                [coordinator coordinateReadingItemAtURL:source options:NSFileCoordinatorReadingWithoutChanges error:&error
                    byAccessor:^(NSURL *readable){
                        NSNumber *regular=nil,*link=nil;
                        [readable getResourceValue:&regular forKey:NSURLIsRegularFileKey error:nil];
                        [readable getResourceValue:&link forKey:NSURLIsSymbolicLinkKey error:nil];
                        if(regular.boolValue && !link.boolValue)
                            copied=[[NSFileManager defaultManager] copyItemAtURL:readable toURL:destination error:nil];
                    }];
                if(!copied)failed++;
            }
            if(scope)[source stopAccessingSecurityScopedResource];
        }
        dispatch_async(dispatch_get_main_queue(),^{
            [self loadFiles];
            if(failed)[self showFileError:@"Some files could not be copied. Existing files were kept."];
        });
    });
}
@end

static UIViewController *launcherFileBrowser(NSURL *documents) {
    LauncherFiles *browser=[[LauncherFiles alloc] initWithStyle:UITableViewStyleInsetGrouped];
    browser.root=documents.URLByResolvingSymlinksInPath.URLByStandardizingPath;
    browser.folder=browser.root;
    browser.sort=@"name";
    return [[UINavigationController alloc] initWithRootViewController:browser];
}
