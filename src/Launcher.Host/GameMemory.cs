using System.Runtime.InteropServices;

namespace Launcher.Host;

internal static class GameMemory
{
    internal const ulong MiB = 1024 * 1024;
    internal const ulong MenuHeadroom = 1024 * MiB;

    internal static ulong? AvailableBytes() => OperatingSystem.IsIOS() ? AvailableProcessMemory() : null;

    internal static void Log(string stage)
    {
        var available = AvailableBytes();
        HostDiagnostics.Stage(available is ulong bytes ? $"{stage} (iPad process headroom {bytes / MiB} MiB)" : stage);
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "os_proc_available_memory")]
    static extern nuint AvailableProcessMemory();
}
