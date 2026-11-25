using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Launcher.Core;

internal static class TexturePackTests
{
    const string First = ".godot/imported/first.bptc.ctex";
    const string Second = ".godot/imported/second.s3tc.ctex";
    const string Existing = ".godot/imported/existing.astc.ctex";

    public static void Run(string root, Action<bool, string> check)
    {
        var cache = Directory.CreateDirectory(Path.Combine(root, "texture-cache")).FullName;
        var source = Path.Combine(root, "texture-source.pck");
        var desktop = Container(22);
        var originalSecond = Container(19);
        var mobile = Container(35);
        var originals = new Dictionary<string, byte[]>
        {
            ["ui/first.png.import"] = Remap("bptc", First),
            [First] = desktop,
            ["ui/second.png.import"] = Remap("s3tc", Second),
            [Second] = originalSecond,
            ["ui/existing.png.import"] = Remap("astc", Existing),
            [Existing] = mobile,
            ["other.resource"] = [1, 2, 3]
        };
        BuildPack(source, originals);
        var sourceBytes = File.ReadAllBytes(source);
        string sha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));

        int copies = 0, encodes = 0;
        void Copy(string from, string to)
        {
            copies++;
            File.Copy(from, to);
        }
        byte[]? ConvertTexture(byte[] input)
        {
            encodes++;
            return input.SequenceEqual(desktop) ? mobile : null;
        }

        using var cancellation = new CancellationTokenSource();
        try
        {
            PckTextureOptimizer.Prepare(source, cache, sha256, Copy, ConvertTexture, (done, _, _) =>
            {
                if (done == 1) cancellation.Cancel();
            }, cancellation.Token);
            throw new InvalidOperationException("Expected texture optimization to cancel.");
        }
        catch (OperationCanceledException) { }
        check(encodes == 1 && Directory.GetFiles(cache, "*.pck").Length == 0,
            "Cancellation checkpoints a texture and publishes no incomplete pack");
        using (var journal = File.Open(Directory.GetFiles(cache, "*.journal").Single(), FileMode.Append))
            journal.WriteByte(0xAB);

        var reports = new List<(int Done, int Total, int Converted)>();
        var optimized = PckTextureOptimizer.Prepare(source, cache, sha256, Copy, ConvertTexture,
            (done, total, converted) => reports.Add((done, total, converted)), default);
        check(copies == 1 && encodes == 2 && reports.SequenceEqual([(1, 2, 1), (2, 2, 1)]),
            "A restart resumes validated textures after a torn journal record");
        check(Directory.GetFiles(cache).SequenceEqual([optimized]), "The completed cache replaces its journal and partial file");
        check(optimized == PckTextureOptimizer.Prepare(source, cache, sha256, Copy, ConvertTexture, (_, _, _) => { }, default) &&
            encodes == 2 && copies == 1, "Later launches reuse the per-version optimized pack");
        check(File.ReadAllBytes(source).SequenceEqual(sourceBytes), "The verified installation stays byte-for-byte intact");

        var entries = ReadPack(optimized, check);
        check(entries[First].SequenceEqual(mobile) &&
            Encoding.UTF8.GetString(entries["ui/first.png.import"]).Contains($"path=\"res://{First}\"\r\n") &&
            Encoding.UTF8.GetString(entries["ui/first.png.import"]).Contains("uid=\"uid://fixture\""),
            "Only successful conversions change their texture entries and remaps");
        check(entries[Second].SequenceEqual(originalSecond) &&
            entries["ui/second.png.import"].SequenceEqual(originals["ui/second.png.import"]) &&
            entries["ui/existing.png.import"].SequenceEqual(originals["ui/existing.png.import"]) &&
            entries["other.resource"].SequenceEqual(originals["other.resource"]),
            "The rebuilt pack preserves rejected textures, native variants and other assets");
        check(PckScripts.AddPaths(optimized, ["res://scripts/Fixture.cs"]) == 1,
            "The optimized pack supports the launcher's existing script patch");
    }

    static byte[] Container(uint format)
    {
        var bytes = new byte[68];
        "GST2"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), format);
        return bytes;
    }

    static byte[] Remap(string variant, string path) => Encoding.UTF8.GetBytes(
        $"[remap]\r\nimporter=\"texture\"\r\ntype=\"CompressedTexture2D\"\r\nuid=\"uid://fixture\"\r\npath.{variant}=\"res://{path}\"\r\n");

    static void BuildPack(string path, Dictionary<string, byte[]> contents)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        foreach (uint word in new uint[] { 0x43504447, 3, 4, 5, 1, 2 }) writer.Write(word);
        const long fileBase = 112;
        writer.Write(fileBase);
        var locations = new List<long>();
        stream.Position = fileBase;
        foreach (var data in contents.Values)
        {
            locations.Add(stream.Position - fileBase);
            writer.Write(data);
        }
        long directory = stream.Position;
        writer.Write(contents.Count);
        int index = 0;
        foreach (var (name, data) in contents)
        {
            var encoded = Encoding.UTF8.GetBytes(name);
            writer.Write(encoded.Length);
            writer.Write(encoded);
            writer.Write(locations[index++]);
            writer.Write((long)data.Length);
            writer.Write(MD5.HashData(data));
            writer.Write(0u);
        }
        stream.Position = 32;
        writer.Write(directory);
    }

    static Dictionary<string, byte[]> ReadPack(string path, Action<bool, string> check)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        stream.Position = 24;
        long fileBase = reader.ReadInt64();
        stream.Position = reader.ReadInt64();
        var result = new Dictionary<string, byte[]>();
        int count = reader.ReadInt32();
        for (int index = 0; index < count; index++)
        {
            var name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            long offset = reader.ReadInt64();
            int length = checked((int)reader.ReadInt64());
            var digest = reader.ReadBytes(16);
            check(reader.ReadUInt32() == 0, "The pack preserves entry flags");
            long next = stream.Position;
            stream.Position = fileBase + offset;
            byte[] bytes = reader.ReadBytes(length);
            check(MD5.HashData(bytes).SequenceEqual(digest), "Output directory checksums match each resource");
            result.Add(name, bytes);
            stream.Position = next;
        }
        return result;
    }
}
