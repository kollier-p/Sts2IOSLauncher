using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core;

public static class PayloadIntegrity
{
    public const string InventoryName = ".payload_integrity.json";
    static IEnumerable<string> Files(string root)
    {
        yield return Payload.Required[0];
        yield return Payload.Required[1];
        var directories = new Stack<string>();
        directories.Push(Payload.AssemblyDirectory);
        while (directories.TryPop(out var relative))
        {
            var folder = Payload.SafePath(root, relative);
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var path = relative + "/" + Path.GetFileName(entry);
                var safe = Payload.SafePath(root, path);
                if (Directory.Exists(safe)) directories.Push(path);
                else if (File.Exists(safe)) yield return path;
                else throw new InvalidDataException("Unexpected payload entry.");
            }
        }
    }
    static string Hash(string path, CancellationToken ct)
    {
        using var input = File.OpenRead(path);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            digest.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }
    public static string Create(string root, CancellationToken ct)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Files(root))
        {
            ct.ThrowIfCancellationRequested();
            files.Add(path, Hash(Payload.SafePath(root, path), ct));
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(files);
        File.WriteAllBytes(Payload.SafePath(root, InventoryName), bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
    public static void Verify(string root, string expected, CancellationToken ct = default)
    {
        var bytes = File.ReadAllBytes(Payload.SafePath(root, InventoryName));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid integrity inventory checksum.");
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(bytes)
            ?? throw new InvalidDataException("Invalid integrity inventory.");
        var actual = Files(root).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(files.Keys)) throw new InvalidDataException("Installed file list differs from inventory.");
        foreach (var (path, hash) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(Hash(Payload.SafePath(root, path), ct), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installed file checksum mismatch: " + path);
        }
    }
}
