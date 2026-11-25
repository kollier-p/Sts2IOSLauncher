using Launcher.Core;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;

var root = Path.Combine(Path.GetTempPath(), "sts2-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Assert(bool test, string message) { if (!test) throw new Exception(message); }
    void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OperationCanceledException) { return; } throw new Exception("Expected rejection"); }
    var copySource = Path.Combine(root, "copy-source");
    var copyTarget = Path.Combine(root, "copy-target");
    var copyBytes = new byte[2 * 1024 * 1024 + 17];
    new Random(42).NextBytes(copyBytes);
    File.WriteAllBytes(copySource, copyBytes);
    Reject(() => LaunchCopy.Copy(copySource, copyTarget, new CancellationToken(true)));
    Assert(!File.Exists(copyTarget), "Cancelled launch copy does not create a file");
    LaunchCopy.Copy(copySource, copyTarget);
    Assert(File.ReadAllBytes(copyTarget).SequenceEqual(copyBytes), "Launch copy preserves all bytes across buffer boundaries");
    Reject(() => LaunchCopy.Copy(copySource, copyTarget));
    Assert(File.ReadAllBytes(copyTarget).SequenceEqual(copyBytes), "Launch copy never overwrites an existing staging file");
    File.Delete(copySource);
    File.Delete(copyTarget);
    foreach (var path in new[] { "../x", "/x", "C:\\x", "a/../x", "a//x" }) Reject(() => Payload.Normalize(path));
    Assert(Payload.Include("data_sts2_windows_x86_64/sts2.dll"), "Include assembly");
    Assert(!Payload.Include("SlayTheSpire2.exe"), "Exclude native PC executable");
    var store = new PayloadStore(root); var stage = store.NewStaging();
    void Fixture(string folder)
    {
        var pck = Path.Combine(folder, Payload.Required[0]);
        var data = Encoding.UTF8.GetBytes("autoload/SentryInit\0autoload/SentryBootstrap");
        using (var f = File.Create(pck)) using (var w = new BinaryWriter(f))
        {
            w.Write(0x43504447u); w.Write(3u); w.Write(4u); w.Write(5u); w.Write(0u); w.Write(0u); w.Write(0ul); w.Write(256ul);
            f.Position = 104; w.Write(data); f.Position = 256; w.Write(1u); var name = Encoding.UTF8.GetBytes("project.binary"); w.Write(name.Length); w.Write(name); w.Write(104ul); w.Write((ulong)data.Length); w.Write(MD5.HashData(data)); w.Write(0u);
        }
        Directory.CreateDirectory(Path.Combine(folder, Payload.AssemblyDirectory));
        foreach (var item in Payload.Required.Where(p => p.EndsWith(".json"))) File.WriteAllText(Path.Combine(folder, item), "{}");
        File.Copy(typeof(Payload).Assembly.Location, Path.Combine(folder, Payload.Required[2]));
    }
    Fixture(stage);
    var pckPath = Path.Combine(stage, Payload.Required[0]);
    Assert(PckPatcher.Patch(pckPath) == 1, "PCK patch count");
    Assert(PckPatcher.Patch(pckPath) == 0, "PCK idempotent");
    var bytes = File.ReadAllBytes(pckPath);
    Assert(Encoding.UTF8.GetString(bytes).Contains("disabled/SentryBootstrap"), "Bootstrap replacement");
    var md5At = 256 + 4 + 4 + "project.binary".Length + 8 + 8;
    var body = Encoding.UTF8.GetBytes("disabled/SentryInit\0disabled/SentryBootstrap");
    Assert(bytes.AsSpan(md5At, 16).SequenceEqual(MD5.HashData(body)), "PCK directory checksum");
    var scriptCopy=Path.Combine(root,"script-test.pck");
    File.Copy(pckPath,scriptCopy);
    Assert(PckScripts.AddPaths(scriptCopy,new[]{"res://Scripts/Test.cs","res://Scripts/Test.cs","res://../bad.cs"})==1,"Script placeholders deduplicated");
    Assert(PckScripts.AddPaths(scriptCopy,new[]{"res://Scripts/Test.cs"})==0,"Script patch idempotent");
    using(var f=File.OpenRead(scriptCopy))using(var r=new BinaryReader(f)){
        f.Position=32;var directory=r.ReadUInt64();f.Position=(long)directory;
        Assert(r.ReadUInt32()==2,"Rebuilt directory count");
        var first=r.ReadUInt32();f.Position+=first+36;
        var length=r.ReadUInt32();var name=Encoding.UTF8.GetString(r.ReadBytes((int)length)).TrimEnd('\0');
        var offset=r.ReadUInt64();var size=r.ReadUInt64();var hash=r.ReadBytes(16);
        Assert(name=="res://Scripts/Test.cs"&&size==1&&hash.SequenceEqual(MD5.HashData(new byte[]{10})),"Script metadata");
        f.Position=(long)offset;Assert(r.ReadByte()==10,"Placeholder data");
    }
    Assert(File.ReadAllBytes(pckPath).SequenceEqual(bytes),"Original pack unchanged");
    var extensionConfig="[configuration]\nentry_symbol = \"fmod_library_init\"\n[libraries]\nwindows.x86_64 = \"res://fmod.dll\"\nios.release = \"old.framework\"\n[dependencies]\nios = { \"old.a\": \"\" }\n";
    var rewritten=PckExtensions.RewriteDescriptor(extensionConfig,"fmod_library_init");
    Assert(rewritten.Contains("ios.arm64 = \"res://native/Sts2Fmod.framework\"")&&!rewritten.Contains("old.framework")&&!rewritten.Contains("old.a")&&rewritten.Contains("windows.x86_64"),"iOS FMOD framework descriptor");
    var spine = PckExtensions.RewriteDescriptor(extensionConfig.Replace("fmod_library_init", "spine_godot_library_init"), "spine_godot_library_init");
    Assert(spine.Contains("ios.arm64 = \"res://native/Sts2Spine.framework\""), "iOS Spine framework descriptor");
    Reject(()=>PckExtensions.RewriteDescriptor(extensionConfig,"wrong_symbol"));
    Assert(PckExtensions.RewriteDescriptor(rewritten,"fmod_library_init").Contains("ios.arm64"),"Extension patch repeatable");
    var extensionPack = Path.Combine(root, "extension-test.pck");
    using (var file = File.Create(extensionPack))
    using (var writer = new BinaryWriter(file))
    {
        var descriptors = new[]
        {
            ("addons/custom/fmod.gdextension", Encoding.UTF8.GetBytes(extensionConfig)),
            ("plugins/spine_runtime.gdextension", Encoding.UTF8.GetBytes(extensionConfig.Replace("fmod_library_init", "spine_godot_library_init"))),
            ("plugins/unrelated.gdextension", Encoding.UTF8.GetBytes("[configuration]\nentry_symbol = \"unrelated\"\n"))
        };
        writer.Write(0x43504447u); writer.Write(3u); writer.Write(4u); writer.Write(5u); writer.Write(0u);
        writer.Write(0u); writer.Write(0ul); writer.Write(1024ul);
        file.Position = 104;
        var offsets = new List<long>();
        foreach (var descriptor in descriptors)
        {
            offsets.Add(file.Position);
            writer.Write(descriptor.Item2);
        }
        file.Position = 1024;
        writer.Write((uint)descriptors.Length);
        for (var i = 0; i < descriptors.Length; i++)
        {
            var name = Encoding.UTF8.GetBytes(descriptors[i].Item1);
            var data = descriptors[i].Item2;
            writer.Write((uint)name.Length); writer.Write(name); writer.Write((ulong)offsets[i]);
            writer.Write((ulong)data.Length); writer.Write(MD5.HashData(data)); writer.Write(0u);
        }
    }
    Assert(PckExtensions.Patch(extensionPack) == 2, "Locate the two supported descriptors by entry symbol");
    Assert(PckExtensions.Patch(extensionPack) == 2, "Rewriting extension paths is repeatable");
    var receipt = store.Install(stage, 123, "public");
    Assert(store.Current() == receipt, "Persistent receipt");
    var broken = store.NewStaging(); Reject(() => store.Install(broken, 456, "public"));
    Assert(store.Current() == receipt, "Failed install preserves active version");
    var cancelled = store.NewStaging(); Fixture(cancelled); Reject(() => store.Install(cancelled, 456, "public", new CancellationToken(true)));
    Assert(store.Current() == receipt, "Cancellation preserves active version");
    var malformed = store.NewStaging(); Fixture(malformed); var malformedPck = Path.Combine(malformed, Payload.Required[0]);
    using (var f = File.OpenWrite(malformedPck)) { f.Position = 32; f.Write(BitConverter.GetBytes(ulong.MaxValue)); }
    Reject(() => PckPatcher.Patch(malformedPck));
    var cached = Path.Combine(root, "cache-test");
    File.WriteAllBytes(cached, new byte[] { 1, 2, 3 });
    var expected = SHA1.HashData(new byte[] { 1, 2, 3 });
    Assert(DownloadCache.IsVerified(cached, 3, expected), "Valid cached file");
    Assert(!DownloadCache.IsVerified(cached, 4, expected), "Wrong cached length");
    File.WriteAllBytes(cached, new byte[] { 1, 2, 4 });
    Assert(!DownloadCache.IsVerified(cached, 3, expected), "Corrupted cached file");
    Assert(!DownloadCache.IsVerified(cached + ".missing", 3, expected), "Missing cached file");
    var estimate = TransferEstimate.Calculate(100, 300, 700, TimeSpan.FromSeconds(2));
    Assert(estimate.BytesPerSecond == 100 && estimate.Remaining == TimeSpan.FromSeconds(4), "Transfer estimate excludes cached bytes");
    Assert(TransferEstimate.Calculate(100, 100, 700, TimeSpan.FromSeconds(2)).Remaining is null, "No speed no ETA");
    Assert(TransferEstimate.Calculate(100, 700, 700, TimeSpan.FromSeconds(2)).Remaining == TimeSpan.Zero, "Finished ETA");
    var second = store.NewStaging(); Fixture(second);
    var newer = store.Install(second, 789, "public");
    Assert(store.InstalledVersions().Count == 2, "List installed versions");
    Assert(store.Current() == newer, "New version active");
    Assert(store.ActivateVersion(receipt.PayloadId) == receipt && store.Current() == receipt, "Rollback to previous version");
    Reject(() => store.ActivateVersion("../missing"));
    Assert(store.Current() == receipt, "Invalid rollback preserves active version");
    File.AppendAllText(Path.Combine(root, "payload-" + newer.PayloadId, Payload.Required[0]), "corruption");
    Reject(() => store.ActivateVersion(newer.PayloadId));
    Assert(store.Current() == receipt, "Corrupt rollback preserves active version");
    File.Delete(Path.Combine(root, "payload-" + newer.PayloadId, Payload.Required[2]));
    Assert(store.InstalledVersions().Contains(newer), "Damaged inactive version remains visible for cleanup");
    Reject(() => store.ActivateVersion(newer.PayloadId));
    store.RemoveVersion(newer.PayloadId);
    Assert(!Directory.Exists(Path.Combine(root, "payload-" + newer.PayloadId)) && store.Current() == receipt,
        "Damaged inactive version can be removed without affecting active installation");
    var archiveInput = Path.Combine(root, "archive-input"); Directory.CreateDirectory(archiveInput); Fixture(archiveInput);
    var zipPath = Path.Combine(root, "Windows backup.zip");
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
    {
        foreach (var item in Payload.Required)
            zip.CreateEntryFromFile(Path.Combine(archiveInput, item), "Steam library/Game/" + item);
        zip.CreateEntry("Steam library/Game/SlayTheSpire2.exe");
    }
    var imported = ArchiveImport.ImportZipAsync(store, zipPath, null, CancellationToken.None).GetAwaiter().GetResult();
    Assert(imported.DepotId == 0 && imported.ManifestId == 0 && imported.DisplayName() == "Windows backup",
        "Imported installs carry their own source identity");
    Assert(store.VerifyForLaunch().Receipt == imported && store.InstalledVersions().Contains(imported), "Verified ZIP install is selectable");
    Assert(!File.Exists(Path.Combine(root, "payload-" + imported.PayloadId, "SlayTheSpire2.exe")), "Import skips PC executables");
    var badZip = Path.Combine(root, "unsafe.zip");
    using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create)) { zip.CreateEntry("../escape.dll"); }
    Reject(() => ArchiveImport.ImportZipAsync(store, badZip, null, CancellationToken.None).GetAwaiter().GetResult());
    using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
        zip.CreateEntry("Steam library/Game/" + Payload.AssemblyDirectory + "/STS2.dll");
    Reject(() => ArchiveImport.ImportZipAsync(store, zipPath, null, CancellationToken.None).GetAwaiter().GetResult());
    Assert(store.Current() == imported, "Failed imports preserve the active installation");
    var tarPath = Path.Combine(root, "Compressed backup.tar.gz");
    using (var target = File.Create(tarPath))
    using (var gzip = new GZipStream(target, CompressionLevel.Fastest))
        TarFile.CreateFromDirectory(archiveInput, gzip, includeBaseDirectory: true);
    var tar = ArchiveImport.ImportAsync(store, tarPath, null, CancellationToken.None).GetAwaiter().GetResult();
    Assert(tar.DisplayName() == "Compressed backup" && store.VerifyForLaunch().Receipt == tar, "Import nested TAR.GZ through the same install store");
    var linkedTar = Path.Combine(root, "linked.tar");
    using (var target = File.Create(linkedTar))
    using (var writer = new TarWriter(target))
        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, Payload.Required[0]) { LinkName = "/tmp/foreign.pck" });
    Reject(() => ArchiveImport.ImportAsync(store, linkedTar, null, CancellationToken.None).GetAwaiter().GetResult());
    Assert(store.Current() == tar, "A linked TAR entry leaves the current installation intact");
    var bzipPath = Path.Combine(root, "Bzip backup.tar.bz2");
    using (var target = File.Create(bzipPath))
    using (var bzip = BZip2Stream.Create(target, SharpCompress.Compressors.CompressionMode.Compress, decompressConcatenated: true))
        TarFile.CreateFromDirectory(archiveInput, bzip, includeBaseDirectory: true);
    var bzipImport = ArchiveImport.ImportAsync(store, bzipPath, null, CancellationToken.None).GetAwaiter().GetResult();
    Assert(bzipImport.DisplayName() == "Bzip backup" && store.VerifyForLaunch().Receipt == bzipImport, "Import a bzip2-compressed TAR");
    var sevenZipPath = Path.Combine(root, "Game archive.7z");
    using (var target = File.Create(sevenZipPath))
    using (var writer = WriterFactory.OpenWriter(target, ArchiveType.SevenZip, new SevenZipWriterOptions(CompressionType.LZMA2)))
    {
        foreach (var item in Payload.Required)
        {
            using var source = File.OpenRead(Path.Combine(archiveInput, item));
            writer.Write("SteamLibrary/Game/" + item, source, DateTime.UtcNow);
        }
    }
    var sevenZip = ArchiveImport.ImportAsync(store, sevenZipPath, null, CancellationToken.None).GetAwaiter().GetResult();
    Assert(sevenZip.DisplayName() == "Game archive" && store.VerifyForLaunch().Receipt == sevenZip, "Import the game from a nested 7z");
    Assert(store.ActivateVersion(receipt.PayloadId) == receipt, "Steam version can replace a ZIP selection");
    store.RemoveVersion(imported.PayloadId);
    Assert(!store.InstalledVersions().Contains(imported) && store.Current() == receipt, "Removing an inactive version preserves the active installation");
    var refusedActiveRemoval = false;
    try { store.RemoveVersion(receipt.PayloadId); }
    catch (InvalidOperationException) { refusedActiveRemoval = true; }
    Assert(refusedActiveRemoval && store.Current() == receipt, "Active installations cannot be removed");
    var interruptedRemoval = Directory.CreateDirectory(Path.Combine(root, "remove-" + Guid.NewGuid().ToString("N")));
    var pendingRemoval = Directory.CreateDirectory(Path.Combine(root, "remove-9-" + Guid.NewGuid().ToString("N")));
    store.CompleteVersionRemovals();
    Assert(interruptedRemoval.Exists && !Directory.Exists(pendingRemoval.FullName), "Retry removes only directories with a valid staged removal name");
    Assert(PayloadStorage.RemainingBytes(root, [("cache-test", 10), ("missing", 5), ("cache-test", 2)]) == 12,
        "Storage preflight credits partial data and caps reuse at the target size");
    PayloadStorage.EnsureCapacity(11, 11);
    PayloadStorage.EnsureCapacity(11, null);
    Reject(() => PayloadStorage.EnsureCapacity(11, 10));
    var profileSettings = Path.Combine(root, "profiles.json");
    var profileData = Directory.CreateDirectory(Path.Combine(root, "documents")).FullName;
    var profiles = new LaunchProfiles(profileSettings, profileData);
    Assert(profiles.Current() == LaunchProfiles.Default, "Upgrades keep the default launch profile");
    var sharedPaths = profiles.EnsureDirectories(LaunchProfiles.Default);
    Assert(sharedPaths.SaveDirectory is null && sharedPaths.ModsDirectory == Path.Combine(profileData, "mods"), "Default profile keeps the shared game directories");
    var isolated = profiles.Create("Beta testing", isolateSaves: true, isolateMods: true);
    var isolatedPaths = profiles.EnsureDirectories(isolated);
    Assert(isolatedPaths.ModsDirectory != sharedPaths.ModsDirectory && Directory.Exists(isolatedPaths.SaveDirectory), "Separate directories for isolated data");
    Assert(isolatedPaths.GodotSavePath("profile2", "saves/settings.save") == $"user://LaunchProfiles/{isolated.Id}/default/1/profile2/saves/settings.save",
        "Save routes retain slot and save-type paths beneath the selected profile");
    Reject(() => isolatedPaths.GodotSavePath("../escape"));
    var savesOnly = profiles.Create("Isolated saves", isolateSaves: true, isolateMods: false);
    Assert(profiles.EnsureDirectories(savesOnly).ModsDirectory == sharedPaths.ModsDirectory, "Save isolation can share mods");
    Assert(profiles.Select(isolated.Id) == isolated && new LaunchProfiles(profileSettings, profileData).Current() == isolated,
        "Selected launch profile survives a restart");
    var bound = profiles.BindVersion(isolated.Id, tar.PayloadId);
    Assert(new LaunchProfiles(profileSettings, profileData).Current() == bound && bound.PayloadId == tar.PayloadId,
        "Profile version survives a restart");
    Assert(store.VerifyForLaunch(payloadId: bound.PayloadId).Receipt == tar && store.Current() == receipt,
        "A profile can launch a verified version without changing the global version");
    Reject(() => store.VerifyForLaunch(payloadId: "missing"));
    Assert(profiles.BindVersion(isolated.Id, null) == isolated, "A profile can follow the global version again");
    var saves = isolatedPaths.SaveDirectory!;
    var backups = Path.Combine(root, "snapshots");
    var snapshotStore = new SaveSnapshots(saves, backups);
    Assert(snapshotStore.Create("Empty") is null, "Do not list empty snapshots");
    var slot = Directory.CreateDirectory(Path.Combine(saves, "profile1", "saves")).FullName;
    var saveFile = Path.Combine(slot, "current_run.save");
    File.WriteAllText(saveFile, "first run");
    var snapshot = snapshotStore.Create("Manual")!;
    File.WriteAllText(saveFile, "second run");
    Assert(snapshotStore.Restore(snapshot.Id) == snapshot && File.ReadAllText(saveFile) == "first run",
        "Restore the exact selected snapshot");
    var beforeRestore = snapshotStore.List().Single(backup => backup.Reason == "Before restore");
    snapshotStore.Restore(beforeRestore.Id);
    Assert(File.ReadAllText(saveFile) == "second run", "The pre-restore backup can recover the original save");
    File.AppendAllText(Path.Combine(backups, snapshot.Id, "files", "profile1", "saves", "current_run.save"), "corrupt");
    Reject(() => snapshotStore.Restore(snapshot.Id));
    Assert(File.ReadAllText(saveFile) == "second run", "A failed snapshot verification preserves live saves");
    Directory.Move(saves, saves + ".before-restore");
    SaveSnapshots.RecoverInterruptedRestore(saves);
    Assert(File.ReadAllText(saveFile) == "second run", "Interrupted restore recovers the previous save directory");
    var webDavSettings = new WebDavSettings("https://dav.example/dav/", "user", "fixture", "global", false);
    Reject(() => (webDavSettings with { Slot = "../outside" }).BaseUri());
    Assert(SaveFileRules.Include("modded/profile2/saves/history/test.run", false) &&
        !SaveFileRules.Include("profile2/saves/history/../bad.run", true), "Cloud sync includes only recognized save paths");
    using var dav = new WebDavFixture();
    using var davHttp = new HttpClient(dav);
    var webDav = new WebDavSaves(davHttp, webDavSettings);
    var syncRoot = Path.Combine(root, "webdav-saves");
    var syncBackups = Path.Combine(root, "webdav-backups");
    var syncBaseline = Path.Combine(root, "webdav-baselines");
    const string syncSlot = "/dav/SlayTheSpire2/saves/global/";
    const string syncSave = "profile1/saves/current_run.save";
    var syncFile = Path.Combine(syncRoot, syncSave);
    dav.Files[syncSlot + syncSave] = "server run";
    dav.Files[syncSlot + "modded/" + syncSave] = "server modded run";
    dav.Files[syncSlot + "settings.save"] = "server settings";
    webDav.TestConnectionAsync(CancellationToken.None).GetAwaiter().GetResult();
    SaveSyncResult SyncDav(bool push, bool force = false) => webDav.SyncAsync(syncRoot, syncBackups, syncBaseline,
        "shared", push, force, CancellationToken.None).GetAwaiter().GetResult();
    Assert(SyncDav(false).Transferred == 2 && File.ReadAllText(syncFile) == "server run" &&
        File.ReadAllText(Path.Combine(syncRoot, "modded", syncSave)) == "server modded run" &&
        !File.Exists(Path.Combine(syncRoot, "settings.save")), "WebDAV uses the Android slot layout and preserves normal and modded saves separately");
    File.WriteAllText(syncFile, "local next run");
    Assert(SyncDav(true).Transferred == 1 && dav.Files[syncSlot + syncSave] == "local next run" && dav.LastIfMatch?.Tag == "\"save-version\"",
        "WebDAV uploads the active changes with a conditional request");
    File.WriteAllText(syncFile, "unsynced local run");
    dav.Files[syncSlot + syncSave] = "other device run";
    Reject(() => SyncDav(false));
    Assert(File.ReadAllText(syncFile) == "unsynced local run", "WebDAV conflicts leave live files intact");
    Assert(SyncDav(false, true).Transferred == 1 && File.ReadAllText(syncFile) == "other device run" &&
        new SaveSnapshots(syncRoot, syncBackups).List().Any(backup => backup.Reason == "Before cloud pull"), "Explicit conflict resolution backs up local data first");
    var modsRoot = Path.Combine(root, "mod-profile", "mods");
    var mods = new ModLibrary(modsRoot);
    string CreateModZip(string name, string id, string description)
    {
        var path = Path.Combine(root, name + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var manifest = new StreamWriter(zip.CreateEntry("module/mod_manifest.json").Open()))
            manifest.Write(System.Text.Json.JsonSerializer.Serialize(new { id }));
        using (var pack = zip.CreateEntry("module/" + (id == "../invalid" ? "TestMod" : id) + ".pck").Open()) pack.Write("GDPC"u8);
        using (var extra = new StreamWriter(zip.CreateEntry("module/description.txt").Open())) extra.Write(description);
        return path;
    }
    using var firstMod = mods.PrepareArchiveAsync(CreateModZip("First mod", "TestMod", "old"), CancellationToken.None).GetAwaiter().GetResult();
    var installedMod = mods.Install(firstMod, replace: false, CancellationToken.None);
    Assert(File.Exists(Path.Combine(modsRoot, installedMod.Key, "module", "TestMod.json")), "Legacy mod manifests get their expected filename");
    Assert(!mods.SetEnabled(installedMod.Key, false).Enabled && !Directory.Exists(Path.Combine(modsRoot, installedMod.Key)),
        "Disabled mods are outside the runtime scan directory");
    using var secondMod = mods.PrepareArchiveAsync(CreateModZip("Second mod", "TestMod", "new"), CancellationToken.None).GetAwaiter().GetResult();
    Assert(mods.Replacement(secondMod)?.Key == installedMod.Key, "A matching package is detected before replacement");
    var replacementMod = mods.Install(secondMod, replace: true, CancellationToken.None);
    Assert(!replacementMod.Enabled && replacementMod.Key == installedMod.Key, "Updates preserve enabled state and package identity");
    mods.SetEnabled(replacementMod.Key, true);
    Assert(File.ReadAllText(Path.Combine(modsRoot, installedMod.Key, "module", "description.txt")) == "new", "The replacement is made active");
    mods.Remove(installedMod.Key);
    Assert(mods.List().Count == 0, "Managed mod removal clears its install directory");
    Reject(() => mods.PrepareArchiveAsync(CreateModZip("Bad mod", "../invalid", "bad"), CancellationToken.None).GetAwaiter().GetResult());
    var workshopZip = CreateModZip("Workshop asset", "WorkshopMod", "workshop");
    using (var preparedWorkshop = mods.PrepareDownloadAsync("Workshop listing", (stage, _) =>
    {
        File.Copy(workshopZip, Path.Combine(stage, "downloaded.zip"));
        return Task.CompletedTask;
    }, CancellationToken.None).GetAwaiter().GetResult())
    {
        Assert(preparedWorkshop.ModuleIds.SequenceEqual(new[] { "WorkshopMod" }) && preparedWorkshop.Label == "Workshop listing",
            "A SteamPipe download containing a single ZIP uses the same mod pipeline");
    }
    using var nexusHandler = new NexusFixture();
    using var nexusHttp = new HttpClient(nexusHandler);
    var nexus = new NexusMods(nexusHttp, "fixture");
    nexus.ValidateAsync(CancellationToken.None).GetAwaiter().GetResult();
    Assert(NexusMods.ParseModId("https://nexusmods.example/slaythespire2/mods/42") is null &&
        NexusMods.ParseNxm("nxm://other/mods/42/files/37?key=public%2Bkey&expires=1234") is null,
        "Nexus lookup and download links are scoped to the game and service");
    var nexusMod = nexus.SearchAsync("https://www.nexusmods.com/slaythespire2/mods/42", CancellationToken.None).GetAwaiter().GetResult().Single();
    var nexusFile = nexus.FilesAsync(nexusMod.Id, CancellationToken.None).GetAwaiter().GetResult().Single();
    var nexusLink = NexusMods.ParseNxm("nxm://slaythespire2/mods/42/files/37?key=public%2Bkey&expires=1234")!;
    var nexusOutput = Path.Combine(root, "nexus-fixture.zip");
    Reject(() => nexus.DownloadAsync(42, 38, nexusLink, nexusOutput, CancellationToken.None).GetAwaiter().GetResult());
    nexus.DownloadAsync(nexusMod.Id, nexusFile.Id, nexusLink, nexusOutput, CancellationToken.None).GetAwaiter().GetResult();
    Assert(nexusFile.Primary && nexusHandler.Downloaded && new FileInfo(nexusOutput).Length == 3 &&
        !nexusLink.ToString().Contains(nexusLink.Key, StringComparison.Ordinal),
        "Nexus imports only the authorized file and keeps the API key and signed download token separate");
    using var partial = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
    Assert(ChunkCache.Verify(partial, 0, 3, SHA1.HashData(new byte[] { 1, 2, 3 })), "Resume verified first chunk");
    Assert(ChunkCache.Verify(partial, 3, 2, SHA1.HashData(new byte[] { 4, 5 })), "Resume verified later chunk");
    Assert(!ChunkCache.Verify(partial, 3, 3, expected), "Reject incomplete chunk");
    Assert(!ChunkCache.Verify(partial, 0, 3, SHA1.HashData(new byte[] { 1, 2, 4 })), "Reject corrupted chunk");
    Assert(!ChunkCache.Verify(partial, -1, 3, expected), "Reject negative offset");
    var firstChunk = new ChunkExtent(0, 3, expected);
    var lastChunk = new ChunkExtent(3, 2, SHA1.HashData(new byte[] { 4, 5 }));
    ChunkLayout.Validate(5, expected, new[] { lastChunk, firstChunk });
    ChunkLayout.Validate(0, SHA1.HashData(Array.Empty<byte>()), Array.Empty<ChunkExtent>());
    Reject(() => ChunkLayout.Validate(5, expected, new[] { firstChunk }));
    Reject(() => ChunkLayout.Validate(5, expected, new[] { firstChunk, lastChunk with { Offset = 2 } }));
    Reject(() => ChunkLayout.Validate(5, expected, new[] { firstChunk, lastChunk with { Offset = 4 } }));
    Reject(() => ChunkLayout.Validate(5, expected, new[] { firstChunk, lastChunk with { Length = 3 } }));
    Reject(() => ChunkLayout.Validate(3, expected, new[] { firstChunk with { Hash = new byte[19] } }));
    Reject(() => ChunkLayout.Validate(0, expected, new[] { firstChunk with { Length = 0 } }));
    Reject(() => ChunkLayout.Validate(ulong.MaxValue, expected, Array.Empty<ChunkExtent>()));
    ChunkLayout.VerifyDownloaded(new byte[] { 1, 2, 3 }, 3, expected);
    Reject(() => ChunkLayout.VerifyDownloaded(new byte[] { 1, 2, 3 }, 2, expected));
    Reject(() => ChunkLayout.VerifyDownloaded(new byte[] { 1, 2, 4 }, 3, expected));
    var meter = new TransferMeter(1000);
    var cachedSample = meter.Add(400, false, TimeSpan.FromSeconds(1));
    Assert(cachedSample.Completed == 400 && cachedSample.Estimate.BytesPerSecond == 0, "Cached data excluded from network speed");
    var liveSample = meter.Add(200, true, TimeSpan.FromSeconds(2));
    Assert(liveSample.Completed == 600 && liveSample.Estimate.BytesPerSecond == 100 && liveSample.Estimate.Remaining == TimeSpan.FromSeconds(4), "Mixed cached and live transfer ETA");
    Reject(() => meter.Add(401, true, TimeSpan.FromSeconds(3)));
    Assert(store.VerifyForLaunch().Receipt == receipt, "Verified launch payload");
    Reject(() => store.ActivateVersion(receipt.PayloadId, new CancellationToken(true)));
    Reject(() => store.VerifyForLaunch(new CancellationToken(true)));
    Assert(store.Current() == receipt, "Cancelled verification preserves active installation");
    var launchData = root + "-saves";
    try
    {
        var plan = LaunchPlan.Prepare(store, launchData);
        Assert(plan.Receipt == receipt && File.Exists(plan.Pack) && File.Exists(plan.GameAssembly), "Launch plan points to verified payload");
        Assert(plan.EngineArguments().Contains(plan.Pack), "Engine arguments select verified pack");
        Assert(Directory.Exists(launchData), "Persistent save directory");
        Reject(() => LaunchPlan.Prepare(store, Path.Combine(root, "saves")));
        Reject(() => LaunchPlan.Prepare(store, launchData, new CancellationToken(true)));
    }
    finally { if (Directory.Exists(launchData)) Directory.Delete(launchData, true); }
    var installedAssembly = Path.Combine(root, "payload-" + receipt.PayloadId, Payload.Required[2]);
    File.AppendAllText(installedAssembly, "corrupt assembly");
    Reject(() => store.VerifyForLaunch());
    Reject(() => store.ActivateVersion(receipt.PayloadId));
    Assert(store.Current() == receipt, "Integrity failure never changes active pointer");
    Console.WriteLine("Payload, download, archive, save, WebDAV and launcher fixtures passed.");
}
finally { Directory.Delete(root, true); }
