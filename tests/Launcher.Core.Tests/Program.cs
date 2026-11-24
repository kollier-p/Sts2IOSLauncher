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
    Console.WriteLine("All payload fixture assertions passed.");
}
finally { Directory.Delete(root, true); }
