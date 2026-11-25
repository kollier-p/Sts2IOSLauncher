using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;

namespace Launcher.Core;

internal enum ArchiveFormat { Zip, Tar, TarGzip, TarBzip2, TarXz, TarZstd, Rar, SevenZip }

internal static class ArchiveFormats
{
    const int EntryLimit = 200_000;

    public static ArchiveFormat? Detect(string path)
    {
        using var input = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[263];
        var length = input.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        var header = bytes[..length];
        if (header.StartsWith("PK\x03\x04"u8) || header.StartsWith("PK\x05\x06"u8)) return ArchiveFormat.Zip;
        if (header.StartsWith(new byte[] { 0x1f, 0x8b })) return ArchiveFormat.TarGzip;
        if (header.StartsWith("BZh"u8)) return ArchiveFormat.TarBzip2;
        if (header.StartsWith(new byte[] { 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0 })) return ArchiveFormat.TarXz;
        if (header.StartsWith(new byte[] { 0x28, 0xb5, 0x2f, 0xfd })) return ArchiveFormat.TarZstd;
        if (header.StartsWith("Rar!\x1a\x07"u8)) return ArchiveFormat.Rar;
        if (header.StartsWith(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c })) return ArchiveFormat.SevenZip;
        if ((header.Length >= 262 && header[257..262].SequenceEqual("ustar"u8)) ||
            path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) return ArchiveFormat.Tar;
        return null;
    }

    public static string DisplayName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name;
    }

    public static async Task ReadAsync(string path, ArchiveFormat format,
        Func<string, long, bool, Func<Stream>, Task> visit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var file = File.OpenRead(path);
        if (format is ArchiveFormat.Rar or ArchiveFormat.SevenZip)
        {
            await ReadIndexedAsync(file, format, visit, ct);
            return;
        }
        using var compressed = format switch
        {
            ArchiveFormat.Tar => (Stream?)null,
            ArchiveFormat.TarGzip => new GZipStream(file, CompressionMode.Decompress, leaveOpen: true),
            ArchiveFormat.TarBzip2 => BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress,
                decompressConcatenated: true, leaveOpen: true),
            ArchiveFormat.TarXz => new XZStream(file),
            ArchiveFormat.TarZstd => new DecompressionStream(file),
            _ => throw new NotSupportedException("Unsupported archive format.")
        };
        using var reader = new TarReader(compressed ?? file);
        var count = 0;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct) is { } entry)
        {
            if (++count > EntryLimit) throw new InvalidDataException("The archive contains too many entries.");
            if (entry.EntryType == TarEntryType.Directory) continue;
            var name = entry.Name;
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            var regular = entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
            await visit(Payload.Normalize(name), entry.Length, regular, () => entry.DataStream ?? Stream.Null);
        }
    }

    static async Task ReadIndexedAsync(Stream file, ArchiveFormat format,
        Func<string, long, bool, Func<Stream>, Task> visit, CancellationToken ct)
    {
        using var archive = ArchiveFactory.OpenArchive(file);
        var expected = format == ArchiveFormat.Rar ? ArchiveType.Rar : ArchiveType.SevenZip;
        if (archive.Type != expected || archive.IsEncrypted || !archive.IsComplete)
            throw new InvalidDataException("The archive is incomplete, encrypted, or has an unexpected format.");
        using var reader = archive.ExtractAllEntries();
        var count = 0;
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            if (++count > EntryLimit) throw new InvalidDataException("The archive contains too many entries.");
            var entry = reader.Entry;
            if (entry.IsDirectory) continue;
            var attributes = unchecked((uint)(entry.Attrib ?? 0));
            var mode = format == ArchiveFormat.Rar ? attributes & 0xf000 : (attributes >> 16) & 0xf000;
            var regular = entry.LinkTarget is null or "" && !entry.IsEncrypted && !entry.IsSplitAfter &&
                (attributes & (uint)FileAttributes.ReparsePoint) == 0 && mode is 0 or 0x8000 &&
                entry is not SevenZipArchiveEntry { IsAnti: true };
            Stream? opened = null;
            try
            {
                await visit(Payload.Normalize(entry.Key ?? ""), entry.Size, regular, () => opened = reader.OpenEntryStream());
            }
            finally { opened?.Dispose(); }
        }
    }
}
