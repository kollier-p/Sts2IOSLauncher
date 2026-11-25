namespace Launcher.Core;

// The caller owns the disposable destination and removes it on failure.
public static class LaunchCopy
{
    public static void RemoveInterruptedCopies(string directory)
    {
        if (!Directory.Exists(directory)) return;
        const string prefix = "StS2.pck.";
        foreach (var temporary in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
        {
            var filename = Path.GetFileName(temporary);
            if (filename.Length == prefix.Length + 32 + 4 &&
                Guid.TryParseExact(filename.AsSpan(prefix.Length, 32), "N", out _))
                File.Delete(Payload.SafePath(directory, filename));
        }
    }

    public static void Copy(string source, string destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = input.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            output.Write(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        output.Flush(true);
    }
}
