using System.Runtime.InteropServices;
using System.Text.Json;
using Launcher.Host;
using Launcher.Steam;

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
    delegate* unmanaged[Cdecl]<int> webDav = &Entry.BrowseWebDav;
    delegate* unmanaged[Cdecl]<int> steamCloud = &Entry.BrowseSteamCloud;
    delegate* unmanaged[Cdecl]<int> mods = &Entry.BrowseMods;
    delegate* unmanaged[Cdecl]<nint, int> importMod = &Entry.BeginModImport;
    delegate* unmanaged[Cdecl]<int> workshop = &Entry.BrowseWorkshop;
    delegate* unmanaged[Cdecl]<int> nexus = &Entry.BrowseNexus;
    delegate* unmanaged[Cdecl]<int> cancel = &Entry.Cancel;
    delegate* unmanaged[Cdecl]<int> prepare = &Entry.Prepare;
    delegate* unmanaged[Cdecl]<int> forget = &Entry.ForgetSession;
    delegate* unmanaged[Cdecl]<int> startHooks = &Entry.StartGameHooks;

    Check(prepare() == -1, "An uninitialized host rejects launch");
    Check(cancel() == 0, "Cancel is safe while idle");
    Check(reply(0, 1) == -1, "A reply needs a live Steam prompt");
    Check(import(0) == -2, "Import rejects an invalid archive path");
    Check(versions() == -1, "Version selection needs an initialized host");
    Check(profiles() == -1, "Profile selection needs an initialized host");
    Check(snapshots() == -1, "Save snapshots need an initialized host");
    Check(webDav() == -1, "WebDAV needs an initialized host");
    Check(steamCloud() == -1, "Steam Cloud needs an initialized host");
    Check(mods() == -1 && importMod(0) == -2, "Mod management validates its native entry arguments");
    Check(workshop() == -1, "Workshop needs an initialized host");
    Check(nexus() == -1, "Nexus Mods needs an initialized host");
    Check(startHooks() == -1, "Compatibility requires a prepared game");
    Check(forget != null, "Keychain delegate is callable through the native ABI");

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
const string workshopJson = """
    {"queries":[{"queryKey":["workshop_browse",{"appid":2868840}],"state":{"data":{"eresult":1,"current_page":2,
    "total_pages":5,"results":[{"consumer_appid":2868840,"publishedfileid":"18446744073709551615",
    "title":"Fixture & tools","short_description":"A Steam Workshop fixture"},
    {"consumer_appid":1,"publishedfileid":"2","title":"Another game"}]}}}]}
    """;
var workshopSsr = JsonSerializer.Serialize(new { renderContext = new { queryData = workshopJson } });
var workshopPage = WorkshopFiles.ParsePage($"""<script id="valve-ssr-data" type="application/json">{workshopSsr}</script>""");
Check(workshopPage.Index == 2 && workshopPage.Pages == 5 && workshopPage.Items.Single().Id == ulong.MaxValue &&
    workshopPage.Items[0].Title == "Fixture & tools", "Workshop browsing parses and scopes Steam's published page data");
var workshopGraph = new Dictionary<ulong, IReadOnlyList<ulong>> { [1] = [2, 3], [2] = [4], [3] = [4], [4] = [] };
Task<IReadOnlyList<ulong>> Requirements(ulong id, CancellationToken _) => Task.FromResult(workshopGraph[id]);
var missing = await WorkshopDependencyOrder.MissingAsync(1, new HashSet<ulong> { 3 }, Requirements, CancellationToken.None);
Check(missing.SequenceEqual(new ulong[] { 4, 2 }), "Workshop installs shared prerequisites once, before dependent items");
workshopGraph[4] = [1];
var cycleRejected = false;
try { await WorkshopDependencyOrder.MissingAsync(1, new HashSet<ulong>(), Requirements, CancellationToken.None); }
catch (InvalidDataException) { cycleRejected = true; }
Check(cycleRejected, "Workshop rejects cyclic dependency graphs");

Console.WriteLine("Native launcher managed entry points passed without Steam credentials or game files.");

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
