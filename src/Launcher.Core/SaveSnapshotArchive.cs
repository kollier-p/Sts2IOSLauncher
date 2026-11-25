using System.IO.Compression;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core;

internal static class SaveSnapshotArchive
{
    const string MetadataName = "sts2_local_save_snapshot.json";
    const string ArchiveType = "sts2_local_save_snapshot";
    const int MaximumFiles = 100_000;
    const int MaximumMetadataBytes = 4 * 1024 * 1024;

    public static void Write(string source, string destination, SaveSnapshot snapshot, IReadOnlyDictionary<string, string> hashes,
        CancellationToken ct)
    {
        var paths = SnapshotPaths(hashes.Keys);
        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1,
            type = ArchiveType,
            created_at_ms = snapshot.Created.ToUnixTimeMilliseconds(),
            reason = snapshot.Reason,
            profile_id = "ios-export",
            file_count = paths.Count,
            paths
        });
        if (metadataBytes.Length > MaximumMetadataBytes) throw new InvalidDataException("The snapshot metadata is too large.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                using (var metadata = zip.CreateEntry(MetadataName).Open())
                    metadata.Write(metadataBytes);
                var buffer = new byte[65_536];
                foreach (var relative in paths)
                {
                    var path = Payload.SafePath(source, relative);
                    using var input = File.OpenRead(path);
                    using var output = zip.CreateEntry(relative, CompressionLevel.Optimal).Open();
                    if (CopyFile(input, output, input.Length, null, buffer, ct) != hashes[relative])
                        throw new InvalidDataException("A save snapshot file failed verification.");
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static (SaveSnapshot Snapshot, Dictionary<string, string> Hashes) Read(string source, string stage, string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > MaximumFiles + 1) throw new InvalidDataException("The snapshot contains too many files.");
        var metadata = zip.GetEntry(MetadataName) ?? throw new InvalidDataException("The archive has no save snapshot metadata.");
        if (metadata.Length > MaximumMetadataBytes) throw new InvalidDataException("The snapshot metadata is too large.");
        using var definition = new MemoryStream();
        var buffer = new byte[65_536];
        using (var input = metadata.Open()) CopyFile(input, definition, metadata.Length, metadata.Crc32, buffer, ct);
        definition.Position = 0;
        using var document = JsonDocument.Parse(definition);
        var element = document.RootElement;
        if (element.GetProperty("schema").GetInt32() != 1 || element.GetProperty("type").GetString() != ArchiveType)
            throw new InvalidDataException("The ZIP is not a supported save snapshot.");
        var paths = SnapshotPaths(element.GetProperty("paths").EnumerateArray().Select(path => path.GetString() ?? ""));
        if (element.GetProperty("file_count").GetInt32() != paths.Count) throw new InvalidDataException("Invalid snapshot file count.");
        var created = DateTimeOffset.FromUnixTimeMilliseconds(element.GetProperty("created_at_ms").GetInt64());
        var label = element.GetProperty("reason").GetString() ?? "Imported";
        var reason = new string(label.Where(character => !char.IsControl(character)).Take(128).ToArray());
        var declared = paths.ToHashSet(StringComparer.Ordinal);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (ReferenceEquals(entry, metadata)) continue;
            var name = Payload.Normalize(entry.FullName);
            var mode = entry.ExternalAttributes >> 16 & 0xf000;
            if (mode is not (0 or 0x8000) || !declared.Contains(name) || !entries.TryAdd(name, entry))
                throw new InvalidDataException("The snapshot has a linked, duplicate, or unexpected entry.");
        }
        if (entries.Count != declared.Count) throw new InvalidDataException("The snapshot is missing declared files.");
        Directory.CreateDirectory(stage);
        var bytes = entries.Values.Sum(entry => entry.Length);
        if (bytes > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("The snapshot exceeds the supported size.");
        PayloadStorage.EnsureCapacity(stage, bytes);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (relative, entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = Payload.SafePath(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open();
            using var output = new FileStream(path, FileMode.CreateNew);
            hashes.Add(relative, CopyFile(input, output, entry.Length, entry.Crc32, buffer, ct));
        }
        return (new SaveSnapshot(id, created, reason, hashes.Count), hashes);
    }

    static List<string> SnapshotPaths(IEnumerable<string> paths)
    {
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (result.Count >= MaximumFiles || path != Payload.Normalize(path) || path.Equals(MetadataName, StringComparison.OrdinalIgnoreCase) ||
                !unique.Add(path)) throw new InvalidDataException("The save snapshot contains unsafe or duplicate paths.");
            result.Add(path);
        }
        if (result.Count == 0) throw new InvalidDataException("The save snapshot contains no files.");
        return result;
    }

    static string CopyFile(Stream source, Stream target, long size, uint? crc, byte[] buffer, CancellationToken ct)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var checksum = crc is null ? null : new Crc32();
        long written = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            written += read;
            if (written > size) throw new InvalidDataException("A snapshot file exceeds its declared size.");
            target.Write(buffer, 0, read);
            digest.AppendData(buffer, 0, read);
            checksum?.Append(buffer.AsSpan(0, read));
        }
        if (written != size || (checksum is not null && checksum.GetCurrentHashAsUInt32() != crc))
            throw new InvalidDataException("A snapshot file failed its size or CRC check.");
        return Convert.ToHexString(digest.GetHashAndReset());
    }
}
