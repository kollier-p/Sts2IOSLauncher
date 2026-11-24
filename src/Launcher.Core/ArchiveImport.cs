using System.Buffers;
using System.IO.Compression;

namespace Launcher.Core;

public static class ArchiveImport
{
    public static async Task<InstallReceipt> ImportZipAsync(PayloadStore store, string path,
        Action<long, long>? report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(path);
        var entries = new List<(string Path, ZipArchiveEntry Entry)>();
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0) continue;
            entries.Add((Payload.Normalize(entry.FullName), entry));
        }

        var packs = entries.Where(e => e.Path == Payload.Required[0] ||
            e.Path.EndsWith("/" + Payload.Required[0], StringComparison.Ordinal)).ToArray();
        if (packs.Length != 1) throw new InvalidDataException("Select a ZIP with one Windows game installation.");
        var prefix = packs[0].Path[..^Payload.Required[0].Length];
        var selected = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = entry.Path[prefix.Length..];
            if (!Payload.Include(relative)) continue;
            if (!unique.Add(relative) || (entry.Entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                throw new InvalidDataException("Duplicate or linked payload entry in ZIP.");
            selected.Add(relative, entry.Entry);
        }
        if (!Payload.Required.All(selected.ContainsKey)) throw new InvalidDataException("The ZIP is missing required game files.");

        using var writer = store.AcquireWriteLock();
        var staging = store.NewStaging();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var total = selected.Values.Sum(entry => entry.Length);
            long done = 0;
            report?.Invoke(done, total);
            foreach (var (relative, entry) in selected)
            {
                ct.ThrowIfCancellationRequested();
                var output = Payload.SafePath(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var source = entry.Open();
                using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                long written = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
                {
                    written += read;
                    if (written > entry.Length) throw new InvalidDataException("An archive entry exceeds its declared size.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    report?.Invoke(done, total);
                }
                if (written != entry.Length) throw new InvalidDataException("An archive entry is truncated.");
            }

            var name = new string(Path.GetFileNameWithoutExtension(path).Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
            return store.InstallArchive(staging, string.IsNullOrEmpty(name) ? "Imported archive" : name, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
