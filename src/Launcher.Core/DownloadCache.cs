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
