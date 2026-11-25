namespace Launcher.Host;

internal static class GameWarmupRecovery
{
    const string MarkerName = "extended-warmup-in-progress";

    internal static bool WasInterrupted(string? directory) =>
        directory is not null && File.Exists(Path.Combine(directory, MarkerName));

    internal static bool TryBegin(string? directory)
    {
        if (directory is null) return false;
        try
        {
            using var marker = File.Open(Path.Combine(directory, MarkerName), FileMode.CreateNew, FileAccess.Write);
            return true;
        }
        catch (IOException) when (WasInterrupted(directory))
        {
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HostDiagnostics.Error("record extended preload attempt", error);
            return false;
        }
    }

    internal static void Clear(string? directory)
    {
        if (directory is null || !WasInterrupted(directory)) return;
        try { File.Delete(Path.Combine(directory, MarkerName)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HostDiagnostics.Error("clear extended preload attempt", error);
        }
    }
}
