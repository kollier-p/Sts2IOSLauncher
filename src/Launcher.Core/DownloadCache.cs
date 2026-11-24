using System.Security.Cryptography;

namespace Launcher.Core;

// A cached file is never trusted merely because its name or size matches.
public static class DownloadCache
{
    public static bool IsVerified(string path, long length, byte[] hash)
    {
        if (!File.Exists(path)) return false;
        using var stream = File.OpenRead(path);
        return stream.Length == length &&
            CryptographicOperations.FixedTimeEquals(SHA1.HashData(stream), hash);
    }
}

public sealed record TransferEstimate(double BytesPerSecond, TimeSpan? Remaining)
{
    public static TransferEstimate Calculate(long initial, long completed, long total, TimeSpan elapsed)
    {
        if (initial < 0 || completed < initial || total < completed || elapsed <= TimeSpan.Zero)
            return new(0, null);
        var speed = (completed - initial) / elapsed.TotalSeconds;
        var seconds = speed > 0 ? (total - completed) / speed : double.PositiveInfinity;
        return new(speed, completed == total ? TimeSpan.Zero :
            seconds < TimeSpan.MaxValue.TotalSeconds ? TimeSpan.FromSeconds(seconds) : null);
    }
}

public static class ChunkCache
{
    // Steam chunk IDs are SHA-1 hashes of the uncompressed bytes.
    public static bool Verify(Stream stream, long offset, int length, byte[] hash)
    {
        if (offset < 0 || length <= 0 || length > 64 * 1024 * 1024 ||
            offset > stream.Length || length > stream.Length - offset) return false;
        stream.Position = offset;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[Math.Min(length, 65536)];
        int remaining = length;
        while (remaining > 0)
        {
            int count = stream.Read(buffer, 0, Math.Min(remaining, buffer.Length));
            if (count == 0) return false;
            digest.AppendData(buffer, 0, count);
            remaining -= count;
        }
        return CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), hash);
    }
}

public sealed class TransferMeter(long total)
{
    long completed, network;
    public (long Completed, TransferEstimate Estimate) Add(long bytes, bool downloaded, TimeSpan elapsed)
    {
        if (bytes < 0 || bytes > total - completed) throw new ArgumentOutOfRangeException(nameof(bytes));
        completed += bytes;
        if (downloaded) network += bytes;
        return (completed, TransferEstimate.Calculate(completed - network, completed, total, elapsed));
    }
}

public readonly record struct ChunkExtent(ulong Offset, uint Length, byte[]? Hash);

public static class ChunkLayout
{
    // Validate the entire manifest before accepting even a fully cached file.
    public static void Validate(ulong size, byte[] fileHash, IEnumerable<ChunkExtent> chunks)
    {
        if (size > long.MaxValue || fileHash is not { Length: 20 })
            throw new InvalidDataException("Invalid manifest file size or hash.");
        ulong cursor = 0;
        foreach (var chunk in chunks.OrderBy(c => c.Offset))
        {
            if (chunk.Offset != cursor || chunk.Length == 0 || chunk.Length > 64 * 1024 * 1024 ||
                cursor > size || chunk.Length > size - cursor || chunk.Hash is not { Length: 20 })
                throw new InvalidDataException("Invalid manifest chunk layout.");
            cursor += chunk.Length;
        }
        if (cursor != size) throw new InvalidDataException("Manifest chunks do not cover the file.");
    }

    public static void VerifyDownloaded(byte[] data, int count, byte[] hash)
    {
        if (count != data.Length || hash is not { Length: 20 } ||
            !CryptographicOperations.FixedTimeEquals(SHA1.HashData(data), hash))
            throw new InvalidDataException("Downloaded chunk size or hash mismatch.");
    }
}
