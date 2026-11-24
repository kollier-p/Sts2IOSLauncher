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

Console.WriteLine("Native launcher managed entry points passed without Steam credentials or game files.");

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
