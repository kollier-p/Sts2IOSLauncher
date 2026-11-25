using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core;

public sealed record SaveSnapshot(string Id, DateTimeOffset Created, string Reason, int Files);

public sealed class SaveSnapshots(string saves, string backups)
{
    const string Metadata = "snapshot.json";
    readonly string live = Path.GetFullPath(saves);
    readonly string root = Path.GetFullPath(backups);
    sealed record SnapshotManifest(SaveSnapshot Snapshot, Dictionary<string, string> Hashes);

    public IReadOnlyList<SaveSnapshot> List()
    {
        if (!Directory.Exists(root)) return [];
        var snapshots = new List<SaveSnapshot>();
        foreach (var directory in Directory.GetDirectories(root))
        {
            var id = Path.GetFileName(directory);
            if (!ValidId(id)) continue;
            try { snapshots.Add(Read(id).Snapshot); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        return snapshots.OrderByDescending(snapshot => snapshot.Created).ToArray();
    }

    public SaveSnapshot? Create(string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(live) && !Directory.Exists(Previous(live))) return null;
        using var operation = Lock(live);
        Recover(live);
        return CreateBackup(reason, ct);
    }

    public SaveSnapshot Restore(string id, CancellationToken ct = default)
    {
        using var operation = Lock(live);
        Recover(live);
        ct.ThrowIfCancellationRequested();
        var manifest = Read(id);
        var source = Payload.SafePath(root, id + "/files");
        var stage = live + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            var verified = Copy(source, stage, ct);
            if (verified.Count != manifest.Hashes.Count ||
                verified.Any(pair => !manifest.Hashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
                throw new InvalidDataException("The save snapshot failed verification.");
            SwapIn(stage, "Before restore", ct);
            return manifest.Snapshot;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    public string ExportArchive(string id, string destinationDirectory, CancellationToken ct = default)
    {
        using var operation = Lock(live);
        var manifest = Read(id);
        Directory.CreateDirectory(destinationDirectory);
        var destination = Payload.SafePath(destinationDirectory, $"STS2-saves-{manifest.Snapshot.Created:yyyyMMdd-HHmmss}-{id[..8]}.zip");
        SaveSnapshotArchive.Write(Payload.SafePath(root, id + "/files"), destination, manifest.Snapshot, manifest.Hashes, ct);
        return destination;
    }

    public SaveSnapshot ImportArchive(string path, CancellationToken ct = default)
    {
        using var operation = Lock(live);
        var id = Guid.NewGuid().ToString("N");
        var stage = Payload.SafePath(root, ".stage-" + id);
        try
        {
            var (snapshot, hashes) = SaveSnapshotArchive.Read(path, Path.Combine(stage, "files"), id, ct);
            CommitBackup(stage, snapshot, hashes, ct);
            return snapshot;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    public void ApplyDownloadedFiles(IReadOnlyDictionary<string, string> downloads, CancellationToken ct = default)
    {
        if (downloads.Count == 0) return;
        using var operation = Lock(live);
        Recover(live);
        StageFiles(downloads, "Before cloud pull", null, ct);
    }

    public int TransferSlot(int profile, bool toModded, CancellationToken ct = default)
    {
        if (profile is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(profile));
        using var operation = Lock(live);
        Recover(live);
        var slot = $"profile{profile}/saves";
        var modded = "modded/" + slot;
        var source = toModded ? slot : modded;
        var destination = ExistingFolderCase(live, toModded ? modded : slot);
        var existing = Directory.Exists(live) ? SaveFileRules.Enumerate(live, includeSettings: false, ct).ToArray() : [];
        var files = existing.Where(item => item.Relative.StartsWith(source + "/", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(item => destination + item.Relative[source.Length..], item => item.File, StringComparer.OrdinalIgnoreCase);
        if (files.Count == 0) return 0;
        StageFiles(files, "Before save slot transfer", destination, ct);
        return files.Count;
    }

    static string ExistingFolderCase(string root, string relative)
    {
        var path = "";
        foreach (var segment in relative.Split('/'))
        {
            var parent = path.Length == 0 ? root : Payload.SafePath(root, path);
            var name = Directory.Exists(parent) ? Directory.EnumerateDirectories(parent).Select(Path.GetFileName)
                .SingleOrDefault(name => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase)) : null;
            path += (path.Length == 0 ? "" : "/") + (name ?? segment);
        }
        return path;
    }

    void StageFiles(IReadOnlyDictionary<string, string> files, string reason, string? replaceFolder, CancellationToken ct)
    {
        var stage = live + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(stage);
            if (Directory.Exists(live)) Copy(live, stage, ct);
            if (replaceFolder is not null)
            {
                var folder = Payload.SafePath(stage, replaceFolder);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            foreach (var (relative, source) in files)
            {
                ct.ThrowIfCancellationRequested();
                var output = Payload.SafePath(stage, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(source, output, overwrite: true);
            }
            SwapIn(stage, reason, ct);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    void SwapIn(string stage, string backupReason, CancellationToken ct)
    {
        CreateBackup(backupReason, ct);
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(live)) Directory.Move(live, Previous(live));
        try { Directory.Move(stage, live); }
        catch
        {
            Recover(live);
            throw;
        }
        Recover(live);
    }

    public static void RecoverInterruptedRestore(string saves)
    {
        var path = Path.GetFullPath(saves);
        if (!Directory.Exists(Previous(path))) return;
        using var operation = Lock(path);
        Recover(path);
    }

    SaveSnapshot? CreateBackup(string reason, CancellationToken ct)
    {
        if (!Directory.Exists(live)) return null;
        var id = Guid.NewGuid().ToString("N");
        var stage = Path.Combine(root, ".stage-" + id);
        try
        {
            var hashes = Copy(live, Path.Combine(stage, "files"), ct);
            if (hashes.Count == 0) return null;
            var snapshot = new SaveSnapshot(id, DateTimeOffset.UtcNow, reason, hashes.Count);
            CommitBackup(stage, snapshot, hashes, ct);
            return snapshot;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }

    void CommitBackup(string stage, SaveSnapshot snapshot, Dictionary<string, string> hashes, CancellationToken ct)
    {
        using (var stream = new FileStream(Path.Combine(stage, Metadata), FileMode.CreateNew))
        {
            JsonSerializer.Serialize(stream, new SnapshotManifest(snapshot, hashes));
            stream.Flush(true);
        }
        ct.ThrowIfCancellationRequested();
        Directory.Move(stage, Payload.SafePath(root, snapshot.Id));
    }

    SnapshotManifest Read(string id)
    {
        if (!ValidId(id)) throw new InvalidDataException("Invalid save snapshot identifier.");
        using var stream = File.OpenRead(Payload.SafePath(root, id + "/" + Metadata));
        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(stream);
        if (manifest is null || manifest.Snapshot?.Id != id || manifest.Hashes is null || manifest.Hashes.Count == 0 ||
            manifest.Hashes.Count != manifest.Snapshot.Files)
            throw new InvalidDataException("Invalid save snapshot metadata.");
        return manifest;
    }

    static Dictionary<string, string> Copy(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var folders = new Stack<string>();
        folders.Push("");
        while (folders.TryPop(out var folder))
        {
            var directory = folder.Length == 0 ? source : Payload.SafePath(source, folder);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                var relative = folder.Length == 0 ? name : folder + "/" + name;
                var input = Payload.SafePath(source, relative);
                if (Directory.Exists(input)) { folders.Push(relative); continue; }
                var output = Payload.SafePath(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(input, output);
                using var file = File.OpenRead(output);
                hashes.Add(relative, Convert.ToHexString(SHA256.HashData(file)));
            }
        }
        return hashes;
    }

    static FileStream Lock(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        Payload.SafePath(parent, Path.GetFileName(path));
        return new FileStream(Payload.SafePath(parent, ".sts2-save.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    static string Previous(string path) => path + ".before-restore";

    static void Recover(string path)
    {
        var previous = Previous(path);
        if (!Directory.Exists(previous)) return;
        var parent = Path.GetDirectoryName(previous)!;
        Payload.SafePath(parent, Path.GetFileName(previous));
        if (!Directory.Exists(path)) Directory.Move(previous, path);
        else
        {
            // An interrupted cleanup can be retried on the next access.
            try { Directory.Delete(previous, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    static bool ValidId(string id) => Guid.TryParseExact(id, "N", out var guid) && id == guid.ToString("N");
}
