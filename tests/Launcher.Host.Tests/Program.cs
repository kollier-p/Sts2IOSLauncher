using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Launcher.Adapters;
using Launcher.Host;
using Launcher.Steam;
using Mono.Cecil;
using Mono.Cecil.Cil;

unsafe
{
    delegate* unmanaged[Cdecl]<nint, nint, nint, int> initialize = &Entry.Initialize;
    delegate* unmanaged[Cdecl]<nint, int, int> copyStatus = &Entry.CopyStatus;
    delegate* unmanaged[Cdecl]<nint, int> download = &Entry.BeginDownload;
    delegate* unmanaged[Cdecl]<nint, int> import = &Entry.BeginImport;
    delegate* unmanaged[Cdecl]<nint, long, int> reply = &Entry.Reply;
    delegate* unmanaged[Cdecl]<int> versions = &Entry.BrowseVersions;
    delegate* unmanaged[Cdecl]<int> profiles = &Entry.BrowseProfiles;
    delegate* unmanaged[Cdecl]<int> snapshots = &Entry.BrowseSnapshots;
    delegate* unmanaged[Cdecl]<nint, int> importSnapshot = &Entry.BeginSnapshotImport;
    delegate* unmanaged[Cdecl]<int> webDav = &Entry.BrowseWebDav;
    delegate* unmanaged[Cdecl]<int> steamCloud = &Entry.BrowseSteamCloud;
    delegate* unmanaged[Cdecl]<int> mods = &Entry.BrowseMods;
    delegate* unmanaged[Cdecl]<nint, int> importMod = &Entry.BeginModImport;
    delegate* unmanaged[Cdecl]<int> workshop = &Entry.BrowseWorkshop;
    delegate* unmanaged[Cdecl]<int> nexus = &Entry.BrowseNexus;
    delegate* unmanaged[Cdecl]<int> launchOptions = &Entry.BrowseLaunchOptions;
    delegate* unmanaged[Cdecl]<int> beginGameSync = &Entry.BeginGameBackgroundSync;
    delegate* unmanaged[Cdecl]<int> gameSyncActive = &Entry.GameBackgroundSyncActive;
    delegate* unmanaged[Cdecl]<int> cancel = &Entry.Cancel;
    delegate* unmanaged[Cdecl]<int> prepare = &Entry.Prepare;
    delegate* unmanaged[Cdecl]<int> forget = &Entry.ForgetSession;
    delegate* unmanaged[Cdecl]<int> signIn = &Entry.SignInSteam;
    delegate* unmanaged[Cdecl]<int> startHooks = &Entry.StartGameHooks;

    Check(prepare() == -1, "An uninitialized host rejects launch");
    Check(cancel() == 0, "Cancel is safe while idle");
    Check(reply(0, 1) == -1, "A reply needs a live Steam prompt");
    Check(import(0) == -2, "Import rejects an invalid archive path");
    Check(versions() == -1, "Version selection needs an initialized host");
    Check(profiles() == -1, "Profile selection needs an initialized host");
    Check(snapshots() == -1, "Save snapshots need an initialized host");
    Check(importSnapshot(0) == -2, "Snapshot import validates the archive path");
    Check(webDav() == -1, "WebDAV needs an initialized host");
    Check(steamCloud() == -1, "Steam Cloud needs an initialized host");
    Check(mods() == -1 && importMod(0) == -2, "Mod management validates its native entry arguments");
    Check(workshop() == -1, "Workshop needs an initialized host");
    Check(nexus() == -1, "Nexus Mods needs an initialized host");
    Check(launchOptions() == -1, "Launch settings need an initialized host");
    Check(beginGameSync() == -1 && gameSyncActive() == 0, "Background save sync requires the opt-in game launch context");
    Check(startHooks() == -1, "Compatibility requires a prepared game");
    Check(forget != null, "Keychain delegate is callable through the native ABI");
    Check(signIn() == -1, "The Steam session command waits for the launcher host");

    var temp = Directory.CreateTempSubdirectory("sts2-native-host-");
    var root = Marshal.StringToCoTaskMemUTF8(Path.Combine(temp.FullName, "payloads"));
    var docs = Marshal.StringToCoTaskMemUTF8(Path.Combine(temp.FullName, "documents"));
    var managed = Marshal.StringToCoTaskMemUTF8(AppContext.BaseDirectory);
    var request = Marshal.StringToCoTaskMemUTF8("""{"Username":"fixture","Password":"fixture","Branch":"public","Manifest":"invalid"}""");
    try
    {
        Check(initialize(root, docs, managed) == 0, "Native initialization succeeds");
        Check(download(request) == -3, "Invalid manifest is rejected before contacting Steam");
        var capacity = -copyStatus(0, 0);
        Span<byte> status = stackalloc byte[capacity];
        fixed (byte* buffer = status)
        {
            Check(copyStatus((nint)buffer, capacity) == capacity - 1, "Status uses a null-terminated UTF-8 buffer");
            Check(status[^1] == 0, "The buffer ends with a null terminator");
        }
        using var data = JsonDocument.Parse(status[..^1].ToArray());
        Check(data.RootElement.GetProperty("Stage").GetString() == "idle", "Native status reports the launcher's idle state");
        Check(data.RootElement.GetProperty("Profile").GetString() == "Default", "Native status shows the active launch profile");
    }
    finally
    {
        Marshal.FreeCoTaskMem(root);
        Marshal.FreeCoTaskMem(docs);
        Marshal.FreeCoTaskMem(managed);
        Marshal.FreeCoTaskMem(request);
        temp.Delete(recursive: true);
    }
}

Check(SteamCloudSaves.LocalPath("%GameInstall%\\modded\\profile2\\saves\\progress.save", false) ==
    "modded/profile2/saves/progress.save", "Steam Auto-Cloud paths preserve modded saves");
Check(SteamCloudSaves.LocalPath("%GameInstall%/settings.save", false) is null &&
    SteamCloudSaves.LocalPath("%GameInstall%/settings.save", true) == "settings.save", "Steam Cloud settings sync is optional");
Check(SteamCloudSaves.LocalPath("%GameInstall%/profile1/saves/history/../bad.run", true) is null,
    "Steam Cloud paths cannot escape the save directory");
Check(WorkshopFiles.ParseId("18446744073709551615") == ulong.MaxValue, "Workshop IDs retain unsigned 64-bit values");
Check(WorkshopFiles.ParseId("https://steamcommunity.com/sharedfiles/filedetails/?id=456&searchtext=test") == 456,
    "Workshop item links resolve to their item ID");
Check(WorkshopFiles.ParseId("https://other.example/sharedfiles/filedetails/?id=456") is null, "Only Steam Community item links are accepted");
var workshopBranches = new[]
{
    WorkshopManifest.Snapshot(100, "public", "public-beta", 50, "Author snapshot"),
    WorkshopManifest.Snapshot(101, "public-beta", "public-beta", 70, "Author snapshot"),
    new WorkshopManifest(102, "public", "", "", 80, "Default manifest")
};
Check(WorkshopManifest.ResolveBranch(workshopBranches, "public-beta")?.Id == 101 &&
    WorkshopManifest.ResolveBranch(workshopBranches, "public")?.Id == 100 &&
    WorkshopManifest.ResolveBranch(workshopBranches, "unknown") is null,
    "Workshop author snapshots preserve branch bounds and do not mislabel the default manifest");
Check(WorkshopManifest.Recommended(workshopBranches, "public-beta")?.Id == 101 &&
    WorkshopManifest.Recommended(workshopBranches, "preview") is { Id: 102, Branch: "preview" },
    "Workshop defaults to the installed branch and uses the default manifest when Steam has no matching snapshot");
const string workshopJson = """
    {"queries":[{"queryKey":["workshop_browse",{"appid":2868840}],"state":{"data":{"eresult":1,"current_page":2,
    "total_pages":5,"results":[{"consumer_appid":2868840,"publishedfileid":"18446744073709551615",
    "title":"Fixture & tools","short_description":"A Steam Workshop fixture","preview_url":"https://images.steamusercontent.com/ugc/preview/"},
    {"consumer_appid":1,"publishedfileid":"2","title":"Another game"}]}}}]}
    """;
var workshopSsr = JsonSerializer.Serialize(new { renderContext = new { queryData = workshopJson } });
var workshopPage = WorkshopFiles.ParsePage($"""<script id="valve-ssr-data" type="application/json">{workshopSsr}</script>""");
Check(workshopPage.Index == 2 && workshopPage.Pages == 5 && workshopPage.Items.Single().Id == ulong.MaxValue &&
    workshopPage.Items[0].Title == "Fixture & tools" && workshopPage.Items[0].Thumbnail?.Host == "images.steamusercontent.com",
    "Workshop browsing parses and scopes Steam's published text and thumbnails");
const ulong accountId = 76561198000000000;
var subscriptionRequest = WorkshopFiles.SubscriptionRequest(accountId, 2);
Check(subscriptionRequest.steamid == accountId && subscriptionRequest.appid == Launcher.Core.Payload.AppId &&
    subscriptionRequest.type == "mysubscriptions" && subscriptionRequest.sortmethod == "subscriptiondate" && subscriptionRequest.page == 2,
    "Workshop subscriptions query the signed-in account and preserve subscription ordering");
var subscriptions = new SteamKit2.Internal.CPublishedFile_GetUserFiles_Response { total = 151 };
subscriptions.publishedfiledetails.Add(new() { publishedfileid = 123, consumer_appid = Launcher.Core.Payload.AppId, title = "Subscribed fixture" });
subscriptions.publishedfiledetails.Add(new() { publishedfileid = 456, consumer_appid = 1 });
subscriptions.publishedfiledetails.Add(new() { publishedfileid = 789, consumer_appid = Launcher.Core.Payload.AppId, banned = true });
subscriptions.publishedfiledetails.Add(new() { publishedfileid = 987 });
var subscriptionPage = WorkshopFiles.SubscriptionPage(2, subscriptions);
Check(subscriptionPage.Index == 2 && subscriptionPage.Pages == 4 && subscriptionPage.Items.Select(item => item.Id).SequenceEqual(new ulong[] { 123, 987 }),
    "Subscription pagination follows Steam's total even when entries are filtered");
Check(new SteamLogOnException(SteamKit2.EResult.Expired).RequiresSignIn &&
    !new SteamLogOnException(SteamKit2.EResult.ServiceUnavailable).RequiresSignIn,
    "Steam token expiry requires sign-in while temporary server failures retain the saved session");

var cloudDirectory = Directory.CreateTempSubdirectory("sts2-cloud-progress-").FullName;
try
{
    var chunks = new List<long>();
    using var stream = new MemoryStream(new byte[150_000]);
    var savePath = Path.Combine(cloudDirectory, "download");
    var write = typeof(SteamCloudSaves).GetMethod("WriteDownload", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
    await (Task)write.Invoke(null, [stream, savePath, (uint)stream.Length, CancellationToken.None, (Action<long>)chunks.Add])!;
    Check(chunks.SequenceEqual([65_536L, 131_072L, 150_000L]) && new FileInfo(savePath).Length == stream.Length,
        "Steam Cloud streams progress in cumulative bytes through file completion");
}
finally { Directory.Delete(cloudDirectory, recursive: true); }

var workshopGraph = new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2, 3], [2] = [4], [3] = [4], [4] = [] };
Task<IReadOnlyList<ulong>> Requirements(ulong id, CancellationToken _) => Task.FromResult(workshopGraph[id]);
var missing = await WorkshopDependencyOrder.MissingAsync(1, new HashSet<ulong> { 3 }, Requirements, CancellationToken.None);
Check(missing.SequenceEqual(new ulong[] { 4, 2 }), "Workshop installs shared prerequisites once, before dependent items");
workshopGraph[4] = [1];
var cycleRejected = false;
try { await WorkshopDependencyOrder.MissingAsync(1, new HashSet<ulong>(), Requirements, CancellationToken.None); }
catch (InvalidDataException) { cycleRejected = true; }
Check(cycleRejected, "Workshop rejects cyclic dependency graphs");

var compatibilityDirectory = Directory.CreateTempSubdirectory("sts2-compat-").FullName;
try
{
    var gamePath = Path.Combine(compatibilityDirectory, "fixture.dll");
    using var fixture = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("FixtureGame", new Version(1, 0)),
        "FixtureGame", ModuleKind.Dll);
    var module = fixture.MainModule;
    var main = new TypeDefinition("GodotPlugins.Game", "Main", TypeAttributes.Class, module.TypeSystem.Object);
    module.Types.Add(main);
    var initializeGame = new MethodDefinition("InitializeFromGameProject", MethodAttributes.Static | MethodAttributes.Private, module.TypeSystem.Byte);
    initializeGame.CustomAttributes.Add(new CustomAttribute(module.ImportReference(typeof(UnmanagedCallersOnlyAttribute).GetConstructor(Type.EmptyTypes)!)));
    foreach (var parameter in new[] { module.TypeSystem.IntPtr, module.TypeSystem.IntPtr, module.TypeSystem.IntPtr, module.TypeSystem.Int32 })
        initializeGame.Parameters.Add(new ParameterDefinition(parameter));
    initializeGame.Body.GetILProcessor().Emit(OpCodes.Ldc_I4_1);
    initializeGame.Body.GetILProcessor().Emit(OpCodes.Ret);
    main.Methods.Add(initializeGame);
    var node = new TypeDefinition("MegaCrit.Sts2.Core.Nodes", "NGame", TypeAttributes.Class, module.TypeSystem.Object);
    module.Types.Add(node);
    var platform = new MethodDefinition("InitializePlatform", MethodAttributes.Private, module.ImportReference(typeof(Task<bool>)));
    platform.Body.GetILProcessor().Emit(OpCodes.Ldnull);
    platform.Body.GetILProcessor().Emit(OpCodes.Ret);
    node.Methods.Add(platform);
    fixture.Write(gamePath);
    Check(GameCompatibilityAudit.FirstIssue(gamePath, false, false) is null, "The expected Godot and platform methods pass audit");
    Check(GameCompatibilityAudit.FirstIssue(gamePath, true, false)?.Contains("save hooks") == true,
        "An isolated profile detects a missing save hook before switching to Godot");
    Check(GameCompatibilityAudit.FirstIssue(gamePath, false, true)?.Contains("mod path") == true,
        "Isolated and paused mods require the scanner signature");
}
finally { Directory.Delete(compatibilityDirectory, recursive: true); }

var steamKitDirectory = Directory.CreateTempSubdirectory("sts2-steamkit-").FullName;
var steamKitContext = new AssemblyLoadContext("SteamKit iOS adapter test", isCollectible: true);
try
{
    var original = typeof(SteamKit2.SteamClient).Assembly;
    var patchedPath = Path.Combine(steamKitDirectory, "SteamKit2.dll");
    Rewriter.Prepare("steamkit", original.Location, "", patchedPath);
    using (var patched = AssemblyDefinition.ReadAssembly(patchedPath))
    {
        var constructor = patched.MainModule.GetType("SteamKit2.SteamClient").Methods
            .Single(method => method.IsConstructor && method.Parameters.Count == 2);
        Check(!constructor.Body.Instructions.Any(instruction => instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == "System.Diagnostics.Process"), "SteamKit can construct its client on iOS");
    }

    using var stream = File.OpenRead(patchedPath);
    var assembly = steamKitContext.LoadFromStream(stream);
    Check(assembly.GetName().FullName == original.GetName().FullName, "The iOS adapter preserves SteamKit assembly identity");
    var clientType = assembly.GetType("SteamKit2.SteamClient", throwOnError: true)!;
    var client = Activator.CreateInstance(clientType);
    var nextJob = clientType.GetMethod(nameof(SteamKit2.SteamClient.GetNextJobID))!;
    var first = nextJob.Invoke(client, null)!;
    var second = nextJob.Invoke(client, null)!;
    var jobType = first.GetType();
    var timestamp = jobType.GetProperty(nameof(SteamKit2.JobID.StartTime))!;
    var sequence = jobType.GetProperty(nameof(SteamKit2.JobID.SequentialCount))!;
    var firstTimestamp = (DateTime)timestamp.GetValue(first)!;
    Check((firstTimestamp - DateTime.UtcNow).Duration() < TimeSpan.FromMinutes(1) &&
          firstTimestamp == (DateTime)timestamp.GetValue(second)! &&
          Convert.ToInt64(sequence.GetValue(second)) == Convert.ToInt64(sequence.GetValue(first)) + 1,
          "The patched SteamKit generates distinct job IDs with a stable timestamp");
}
finally
{
    steamKitContext.Unload();
    Directory.Delete(steamKitDirectory, recursive: true);
}

Console.WriteLine("Native launcher managed entry points passed without Steam credentials or game files.");

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
