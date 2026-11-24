using System.Buffers;
using System.Formats.Tar;
using System.IO.Compression;

namespace Launcher.Core;

public static class ArchiveImport
{
    public static Task<InstallReceipt> ImportAsync(PayloadStore store, string path,
        Action<long, long>? report, CancellationToken ct)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ImportZipAsync(store, path, report, ct);
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ImportTarAsync(store, path, ".tar.gz", report, ct);
        if (path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return ImportTarAsync(store, path, ".tgz", report, ct);
        if (path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return ImportTarAsync(store, path, ".tar", report, ct);
        throw new NotSupportedException("Choose a ZIP, TAR, or gzip-compressed TAR archive.");
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
            return store.InstallArchive(staging, ArchiveName(path, Path.GetExtension(path)), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    static async Task<InstallReceipt> ImportTarAsync(PayloadStore store, string path, string extension,
        Action<long, long>? report, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var file = File.OpenRead(path);
        using var gzip = extension == ".tar" ? null : new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        using var archive = new TarReader(gzip is null ? file : gzip);
        using var writer = store.AcquireWriteLock();
        var staging = store.NewStaging();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            string? root = null;
            var selected = new HashSet<string>(StringComparer.Ordinal);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long done = 0;
            while (await archive.GetNextEntryAsync(copyData: false, cancellationToken: ct) is { } entry)
            {
                if (entry.EntryType == TarEntryType.Directory) continue;
                var name = entry.Name;
                while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                var filePath = Payload.Normalize(name);
                var prefixLength = PayloadPrefixLength(filePath);
                if (prefixLength < 0) continue;
                var prefix = filePath[..prefixLength];
                root ??= prefix;
                if (prefix != root) throw new InvalidDataException("Select an archive with one Windows game installation.");
                var relative = filePath[prefixLength..];
                if (!unique.Add(relative) || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("Duplicate or linked payload entry in TAR.");
                selected.Add(relative);
                PayloadStorage.EnsureCapacity(store.Root, entry.Length);
                await CopyEntryAsync(staging, relative, entry.DataStream ?? Stream.Null, entry.Length, buffer,
                    count => report?.Invoke(done += count, 0), ct);
            }
            if (!Payload.Required.All(selected.Contains)) throw new InvalidDataException("The TAR is missing required game files.");
            return store.InstallArchive(staging, ArchiveName(path, extension), ct);
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

    static string ArchiveName(string path, string extension)
    {
        var filename = Path.GetFileName(path)[..^extension.Length];
        var name = new string(filename.Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
        return string.IsNullOrEmpty(name) ? "Imported archive" : name;
    }

    static async Task CopyEntryAsync(string staging, string relative, Stream source, long length,
        byte[] buffer, Action<int> bytesWritten, CancellationToken ct)
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
            bytesWritten(read);
        }
        if (written != length) throw new InvalidDataException("An archive entry is truncated.");
    }
}
