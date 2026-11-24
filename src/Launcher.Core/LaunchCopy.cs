namespace Launcher.Core;

// The caller owns the disposable destination and removes it on failure.
public static class LaunchCopy
{
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
