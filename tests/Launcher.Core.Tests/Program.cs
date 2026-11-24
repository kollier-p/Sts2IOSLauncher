using Launcher.Core;
using System.Text;
using System.Security.Cryptography;

var root = Path.Combine(Path.GetTempPath(), "sts2-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    void Assert(bool test, string message) { if (!test) throw new Exception(message); }
    void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OperationCanceledException) { return; } throw new Exception("Expected rejection"); }
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
    using var partial = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
    Assert(ChunkCache.Verify(partial, 0, 3, SHA1.HashData(new byte[] { 1, 2, 3 })), "Resume verified first chunk");
    Assert(ChunkCache.Verify(partial, 3, 2, SHA1.HashData(new byte[] { 4, 5 })), "Resume verified later chunk");
    Assert(!ChunkCache.Verify(partial, 3, 3, expected), "Reject incomplete chunk");
    Assert(!ChunkCache.Verify(partial, 0, 3, SHA1.HashData(new byte[] { 1, 2, 4 })), "Reject corrupted chunk");
    Assert(!ChunkCache.Verify(partial, -1, 3, expected), "Reject negative offset");
    var meter = new TransferMeter(1000);
    var cachedSample = meter.Add(400, false, TimeSpan.FromSeconds(1));
    Assert(cachedSample.Completed == 400 && cachedSample.Estimate.BytesPerSecond == 0, "Cached data excluded from network speed");
    var liveSample = meter.Add(200, true, TimeSpan.FromSeconds(2));
    Assert(liveSample.Completed == 600 && liveSample.Estimate.BytesPerSecond == 100 && liveSample.Estimate.Remaining == TimeSpan.FromSeconds(4), "Mixed cached and live transfer ETA");
    Reject(() => meter.Add(401, true, TimeSpan.FromSeconds(3)));
    Assert(store.VerifyForLaunch().Receipt == receipt, "Verified launch payload");
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
    Console.WriteLine("All payload, download, chunk, meter, integrity and rollback fixture assertions passed.");
}
finally { Directory.Delete(root, true); }
