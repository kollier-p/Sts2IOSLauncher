using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Launcher.Core;

public sealed class SaveSyncConflictException(int count) : IOException("The local and remote saves both changed.")
{
    public int Count { get; } = count;
}

public sealed record SaveSyncResult(int Transferred, int Compared);

public sealed class SaveSyncHistory
{
    readonly string path;
    readonly Dictionary<string, string> agreed;

    public SaveSyncHistory(string directory, string identity)
    {
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        path = Payload.SafePath(directory, key + ".json");
        agreed = new(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            if (JsonSerializer.Deserialize<Dictionary<string, string>>(stream) is { } previous)
                agreed = new(previous, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> Changes(IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> destination, bool overwriteConflicts)
    {
        var files = new List<string>();
        var conflicts = 0;
        foreach (var (relative, hash) in source)
        {
            if (!destination.TryGetValue(relative, out var other)) { files.Add(relative); continue; }
            if (hash == other) { Remember(relative, hash); continue; }
            agreed.TryGetValue(relative, out var previous);
            if (overwriteConflicts || other == previous) files.Add(relative);
            else if (hash != previous) conflicts++;
        }
        if (conflicts != 0) throw new SaveSyncConflictException(conflicts);
        return files;
    }

    public void Remember(string name, string hash) => agreed[name] = hash;

    public void Save()
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, agreed);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
