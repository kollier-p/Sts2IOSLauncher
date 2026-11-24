namespace Launcher.Core;

public sealed class InsufficientStorageException(long requiredBytes, long availableBytes) : IOException("Insufficient storage for the game files.")
{
    public long RequiredBytes { get; } = requiredBytes;
    public long AvailableBytes { get; } = availableBytes;
}

public static class PayloadStorage
{
    public static long RemainingBytes(string root, IEnumerable<(string Path, long Length)> files)
    {
        long remaining = 0;
        foreach (var (relative, length) in files)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(files));
            var file = new FileInfo(Payload.SafePath(root, relative));
            var existing = file.Exists ? Math.Min(file.Length, length) : 0;
            remaining = checked(remaining + (length - existing));
        }
        return remaining;
    }

    internal static long? FreeBytes(string directory)
    {
        try { return new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public static void EnsureCapacity(string directory, long requiredBytes)
        => EnsureCapacity(requiredBytes, FreeBytes(directory));

    public static void EnsureCapacity(long requiredBytes, long? availableBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBytes);
        if (requiredBytes > 0 && availableBytes is >= 0 && availableBytes.Value < requiredBytes)
            throw new InsufficientStorageException(requiredBytes, availableBytes.Value);
    }
}
