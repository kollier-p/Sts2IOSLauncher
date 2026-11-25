using System.Buffers;
using System.IO.Compression;

namespace Launcher.Core;

public static class ArchiveImport
{
    public static Task<InstallReceipt> ImportAsync(PayloadStore store, string path,
        Action<long, long>? report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var format = ArchiveFormats.Detect(path) ?? throw new NotSupportedException("Unsupported archive format.");
        return format == ArchiveFormat.Zip ? ImportZipAsync(store, path, report, ct) : ImportEntriesAsync(store, path, format, report, ct);
    }

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
        var total = selected.Values.Sum(entry => entry.Length);
        PayloadStorage.EnsureCapacity(store.Root, total);
        var staging = store.NewStaging();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long done = 0;
            report?.Invoke(done, total);
            foreach (var (relative, entry) in selected)
            {
                ct.ThrowIfCancellationRequested();
                using var source = entry.Open();
                await CopyEntryAsync(staging, relative, source, entry.Length, buffer,
                    count => report?.Invoke(done += count, total), ct);
            }
            return store.InstallArchive(staging, ArchiveName(path), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    static async Task<InstallReceipt> ImportEntriesAsync(PayloadStore store, string path, ArchiveFormat format,
        Action<long, long>? report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var writer = store.AcquireWriteLock();
        var staging = store.NewStaging();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            string? root = null;
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long done = 0;
            await ArchiveFormats.ReadAsync(path, format, async (filePath, size, regular, open) =>
            {
                var prefixLength = PayloadPrefixLength(filePath);
                if (prefixLength < 0) return;
                var prefix = filePath[..prefixLength];
                root ??= prefix;
                if (prefix != root) throw new InvalidDataException("Select an archive with one Windows game installation.");
                var relative = filePath[prefixLength..];
                if (!unique.Add(relative) || !regular || size < 0)
                    throw new InvalidDataException("Duplicate, linked, or invalid game file in archive.");
                selected.Add(relative);
                PayloadStorage.EnsureCapacity(store.Root, size);
                await CopyEntryAsync(staging, relative, open(), size, buffer,
                    count => report?.Invoke(done += count, 0), ct);
            }, ct);
            if (!Payload.Required.All(selected.Contains)) throw new InvalidDataException("The archive is missing required game files.");
            return store.InstallArchive(staging, ArchiveName(path), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    static int PayloadPrefixLength(string path)
    {
        if (Payload.Include(path)) return 0;
        foreach (var rootFile in Payload.Required.Take(2))
            if (path.EndsWith("/" + rootFile, StringComparison.Ordinal)) return path.Length - rootFile.Length;
        var assemblyRoot = path.LastIndexOf("/" + Payload.AssemblyDirectory + "/", StringComparison.Ordinal);
        return assemblyRoot < 0 ? -1 : assemblyRoot + 1;
    }

    static string ArchiveName(string path)
    {
        var filename = ArchiveFormats.DisplayName(path);
        var name = new string(filename.Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
        return string.IsNullOrEmpty(name) ? "Imported archive" : name;
    }

    internal static async Task CopyEntryAsync(string staging, string relative, Stream source, long length,
        byte[] buffer, Action<int>? bytesWritten, CancellationToken ct)
    {
        var output = Payload.SafePath(staging, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            written += read;
            if (written > length) throw new InvalidDataException("An archive entry exceeds its declared size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesWritten?.Invoke(read);
        }
        if (written != length) throw new InvalidDataException("An archive entry is truncated.");
    }
}
