using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Launcher.Core;

public static class PckTextureOptimizer
{
    const int MaxTextureBytes = 32 * 1024 * 1024;
    const int CheckpointBytes = 4 + 16 + 16;
    static readonly Regex DesktopPath = new(@"(?m)^path\.(?:s3tc|bptc)[ \t]*=[ \t]*""res://(\.godot/imported/[^""\r\n]+\.(?:s3tc|bptc)\.ctex)""[ \t]*(?=\r?$)");
    static readonly Regex PhonePath = new(@"(?m)^path(?:\.astc)?[ \t]*=");
    static readonly byte[] EmptyDigest = new byte[16];

    sealed record Entry(byte[] Name, long Offset, long Length, byte[] Digest, uint Flags);
    sealed record Texture(int Resource, int Import, byte[] Remap);
    sealed record Pack(long BaseOffset, Entry[] Entries, Dictionary<string, int> Names);

    public static string Prepare(string source, string cacheDirectory, string sourceSha256,
        Action<string, string> copy, Func<byte[], byte[]?> convert, Action<int, int, int> progress, CancellationToken ct)
    {
        if (sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The installed pack needs a SHA-256 digest.", nameof(sourceSha256));
        string output = Path.Combine(cacheDirectory, $"ipad-astc4x4-v1-{sourceSha256.ToLowerInvariant()}.pck");
        if (File.Exists(output)) return output;

        using var input = File.OpenRead(source);
        var pack = ReadPack(input, ct);
        var textures = Plan(input, pack, ct);
        if (textures.Count == 0) return source;
        var inputEntries = pack.Entries.ToArray();

        string partial = output + ".partial";
        string journal = output + ".journal";
        if (!File.Exists(partial) || !File.Exists(journal) || new FileInfo(partial).Length < input.Length)
        {
            File.Delete(partial);
            File.Delete(journal);
            copy(source, partial);
            using var created = File.Create(journal);
        }

        using (var target = new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var checkpoints = new FileStream(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            long end = input.Length;
            int done = Restore(target, checkpoints, pack, textures, ref end, out int converted, ct);
            target.SetLength(end);
            checkpoints.SetLength(done * (long)CheckpointBytes);
            checkpoints.Position = checkpoints.Length;
            using var checkpoint = new BinaryWriter(checkpoints, Encoding.UTF8, leaveOpen: true);
            progress(done, textures.Count, converted);
            for (; done < textures.Count; done++)
            {
                ct.ThrowIfCancellationRequested();
                var texture = textures[done];
                // Read the source directory entry. The target's table can be
                // replaced without affecting the verified input pack.
                var originalEntry = inputEntries[texture.Resource];
                var result = convert(ReadBytes(input, originalEntry));
                ct.ThrowIfCancellationRequested();

                if (result is null)
                {
                    checkpoint.Write(0);
                    checkpoint.Write(EmptyDigest);
                    checkpoint.Write(EmptyDigest);
                }
                else
                {
                    if (!IsAstcTexture(result))
                        throw new InvalidDataException("The texture encoder returned an invalid file.");

                    PayloadStorage.EnsureCapacity(cacheDirectory, result.Length + texture.Remap.Length + 32L);
                    long offset = Align(end);
                    long importOffset = Align(offset + result.Length);
                    target.Position = offset;
                    target.Write(result);
                    target.Position = importOffset;
                    target.Write(texture.Remap);
                    target.Flush(flushToDisk: true);

                    var textureDigest = MD5.HashData(result);
                    var importDigest = MD5.HashData(texture.Remap);
                    Replace(pack, texture, offset, result.Length, textureDigest, importOffset, importDigest);
                    end = target.Position;
                    checkpoint.Write(result.Length);
                    checkpoint.Write(textureDigest);
                    checkpoint.Write(importDigest);
                    converted++;
                }
                checkpoints.Flush(flushToDisk: true);
                progress(done + 1, textures.Count, converted);
            }
            ct.ThrowIfCancellationRequested();
            WriteDirectory(target, pack, Align(end), ct);
        }

        File.Move(partial, output, overwrite: true);
        File.Delete(journal);
        return output;
    }

    static int Restore(FileStream target, FileStream checkpoints, Pack pack, List<Texture> textures,
        ref long end, out int converted, CancellationToken ct)
    {
        converted = 0;
        using var reader = new BinaryReader(checkpoints, Encoding.UTF8, leaveOpen: true);
        int done = 0;
        while (done < textures.Count && checkpoints.Length - checkpoints.Position >= CheckpointBytes)
        {
            ct.ThrowIfCancellationRequested();
            int length = reader.ReadInt32();
            byte[] digest = reader.ReadBytes(16), importDigest = reader.ReadBytes(16);
            if (length == 0) { done++; continue; }
            if (length is < 52 or > MaxTextureBytes) break;
            var texture = textures[done];
            long offset = Align(end), importOffset = Align(offset + length);
            if (importOffset + texture.Remap.Length > target.Length) break;
            target.Position = offset;
            var data = new byte[length];
            target.ReadExactly(data);
            target.Position = importOffset;
            var remap = new byte[texture.Remap.Length];
            target.ReadExactly(remap);
            if (!IsAstcTexture(data) || !MD5.HashData(data).AsSpan().SequenceEqual(digest) ||
                !remap.AsSpan().SequenceEqual(texture.Remap) || !MD5.HashData(remap).AsSpan().SequenceEqual(importDigest))
                break;
            Replace(pack, texture, offset, length, digest, importOffset, importDigest);
            end = importOffset + texture.Remap.Length;
            converted++;
            done++;
        }
        return done;
    }

    static bool IsAstcTexture(byte[] data) => data.Length is >= 52 and <= MaxTextureBytes &&
        data.AsSpan(0, 4).SequenceEqual("GST2"u8) && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48)) == 35;

    static Pack ReadPack(FileStream stream, CancellationToken ct)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != 0x43504447 || reader.ReadUInt32() != 3)
            throw new InvalidDataException("Texture optimization needs a Godot version 3 pack.");
        stream.Position = 20;
        uint flags = reader.ReadUInt32();
        long fileBase = reader.ReadInt64(), directory = reader.ReadInt64();
        if ((flags & 1) != 0 || directory < 104 || directory >= stream.Length)
            throw new InvalidDataException("Unsupported Godot pack directory.");
        long baseOffset = (flags & 2) == 0 ? 0 : fileBase;
        if (baseOffset < 0 || baseOffset > directory) throw new InvalidDataException("Invalid pack file base.");
        stream.Position = directory;
        int count = reader.ReadInt32();
        if (count is <= 0 or > 1_000_000) throw new InvalidDataException("Invalid pack entry count.");
        var entries = new Entry[count];
        var names = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (stream.Position - directory > 128 * 1024 * 1024) throw new InvalidDataException("Pack directory exceeds its limit.");
            int pathLength = reader.ReadInt32();
            if (pathLength is <= 0 or > 65536) throw new InvalidDataException("Invalid pack path.");
            var name = reader.ReadBytes(pathLength);
            var offset = reader.ReadInt64();
            var length = reader.ReadInt64();
            var digest = reader.ReadBytes(16);
            var entryFlags = reader.ReadUInt32();
            if (offset < 0 || length < 0 || offset > directory - baseOffset || length > directory - baseOffset - offset)
                throw new InvalidDataException("Pack resource is out of bounds.");
            entries[index] = new(name, baseOffset + offset, length, digest, entryFlags);
            string path = Encoding.UTF8.GetString(name).TrimEnd('\0');
            if (path.StartsWith("res://", StringComparison.Ordinal)) path = path[6..];
            names.TryAdd(path, index);
        }
        return new(baseOffset, entries, names);
    }

    static List<Texture> Plan(FileStream input, Pack pack, CancellationToken ct)
    {
        var textures = new List<Texture>();
        var seen = new HashSet<int>();
        foreach (var (path, index) in pack.Names)
        {
            ct.ThrowIfCancellationRequested();
            var import = pack.Entries[index];
            if (!path.EndsWith(".import", StringComparison.Ordinal) || import.Flags != 0 || import.Length is <= 0 or > 65536)
                continue;
            string text = Encoding.UTF8.GetString(ReadBytes(input, import));
            if (PhonePath.IsMatch(text)) continue;
            var matches = DesktopPath.Matches(text);
            if (matches.Count != 1) continue;
            var match = matches[0];
            var name = match.Groups[1].Value;
            if (!pack.Names.TryGetValue(name, out int resource)) continue;
            var entry = pack.Entries[resource];
            if (entry.Flags != 0 || entry.Length is < 52 or > MaxTextureBytes || !seen.Add(resource)) continue;
            // Keep the original resource path and UID. The unconditional path
            // ensures the ASTC file is selected regardless of desktop feature tags.
            var remap = Encoding.UTF8.GetBytes($"{text[..match.Index]}path=\"res://{name}\"{text[(match.Index + match.Length)..]}");
            textures.Add(new(resource, index, remap));
        }
        return textures;
    }

    static byte[] ReadBytes(FileStream stream, Entry entry)
    {
        var bytes = new byte[checked((int)entry.Length)];
        stream.Position = entry.Offset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    static void Replace(Pack pack, Texture texture, long offset, int length, byte[] digest, long importOffset, byte[] importDigest)
    {
        pack.Entries[texture.Resource] = pack.Entries[texture.Resource] with { Offset = offset, Length = length, Digest = digest };
        pack.Entries[texture.Import] = pack.Entries[texture.Import] with
            { Offset = importOffset, Length = texture.Remap.Length, Digest = importDigest };
    }

    static void WriteDirectory(FileStream target, Pack pack, long directory, CancellationToken ct)
    {
        using var writer = new BinaryWriter(target, Encoding.UTF8, leaveOpen: true);
        target.Position = directory;
        writer.Write(pack.Entries.Length);
        foreach (var entry in pack.Entries)
        {
            ct.ThrowIfCancellationRequested();
            writer.Write(entry.Name.Length);
            writer.Write(entry.Name);
            writer.Write(entry.Offset - pack.BaseOffset);
            writer.Write(entry.Length);
            writer.Write(entry.Digest);
            writer.Write(entry.Flags);
        }
        target.SetLength(target.Position);
        target.Position = 32;
        writer.Write(directory);
        target.Flush(flushToDisk: true);
    }

    static long Align(long value) => (value + 15) & ~15L;
}
