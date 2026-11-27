using System.Runtime.InteropServices;

namespace Launcher.Host;

internal static class GameMemory
{
    internal const ulong MiB = 1024 * 1024;
    internal const ulong MenuHeadroom = 1024 * MiB;

    // xnu memorystatus_control commands and memory limit attributes.
    const uint GetMemlimitProperties = 8;
    const uint SetMemlimitProperties = 7;
    const uint SetJetsamTaskLimit = 6;
    const uint MemlimitAttrFatal = 1;

    // Leave this much physical memory to iPadOS, SpringBoard and the GPU driver.
    const ulong SystemReserve = 768 * MiB;

    internal static ulong? AvailableBytes() => OperatingSystem.IsIOS() ? AvailableProcessMemory() : null;

    internal static void Log(string stage)
    {
        var available = AvailableBytes();
        HostDiagnostics.Stage(available is ulong bytes ? $"{stage} (iPad process headroom {bytes / MiB} MiB)" : stage);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemlimitProperties
    {
        public int Active;
        public uint ActiveAttributes;
        public int Inactive;
        public uint InactiveAttributes;
        public uint Increase;
        public uint IncreaseBytes;
    }

    internal static uint TargetLimitMiB(ulong physicalBytes)
    {
        if (physicalBytes <= SystemReserve * 2) return 0;
        ulong byReserve = physicalBytes - SystemReserve;
        ulong byShare = physicalBytes / 20 * 17;
        return (uint)(Math.Min(byReserve, byShare) / MiB);
    }

    // The desktop game keeps far more textures resident than the default iPad
    // jetsam limit allows. TrollStore builds carry the memorystatus and
    // increased-memory-limit entitlements; other installs keep the default.
    internal static void RaiseProcessLimit()
    {
        if (!OperatingSystem.IsIOS()) return;
        try
        {
            ulong before = AvailableProcessMemory();
            int pid = Environment.ProcessId;
            var current = new MemlimitProperties();
            int read = MemorystatusControl(GetMemlimitProperties, pid, 0, ref current, (nuint)Marshal.SizeOf<MemlimitProperties>());
            int readError = read == 0 ? 0 : Marshal.GetLastPInvokeError();
            uint target = TargetLimitMiB(PhysicalMemory());
            string currentText = read == 0 ? $"{current.Active} MiB" : $"unknown (errno {readError})";
            if (target == 0 || (read == 0 && current.Active > 0 && current.Active >= target))
            {
                HostDiagnostics.Stage($"iPad memory limit {currentText}, headroom {before / MiB} MiB (no change needed)");
                return;
            }

            var wanted = new MemlimitProperties
            {
                Active = (int)target,
                ActiveAttributes = MemlimitAttrFatal,
                Inactive = (int)target,
                InactiveAttributes = MemlimitAttrFatal
            };
            int result = MemorystatusControl(SetMemlimitProperties, pid, 0, ref wanted, 16);
            int error = result == 0 ? 0 : Marshal.GetLastPInvokeError();
            if (result != 0)
            {
                var none = new MemlimitProperties();
                result = MemorystatusControl(SetJetsamTaskLimit, pid, target, ref none, 0);
                if (result != 0) error = Marshal.GetLastPInvokeError();
            }
            ulong after = AvailableProcessMemory();
            HostDiagnostics.Stage(result == 0
                ? $"raised iPad memory limit from {currentText} to {target} MiB (headroom {before / MiB} → {after / MiB} MiB)"
                : $"iPad memory limit stays {currentText}, headroom {after / MiB} MiB " +
                  $"(raise to {target} MiB refused, errno {error}; install the TrollStore build for a higher limit)");
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
        {
            HostDiagnostics.Error("raise iPad memory limit", error);
        }
    }

    static ulong PhysicalMemory()
    {
        ulong value = 0;
        nuint size = (nuint)sizeof(ulong);
        return SysctlByName("hw.memsize", ref value, ref size, 0, 0) == 0 ? value : 0;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "os_proc_available_memory")]
    static extern nuint AvailableProcessMemory();

    [DllImport("libSystem.B.dylib", EntryPoint = "memorystatus_control", SetLastError = true)]
    static extern int MemorystatusControl(uint command, int pid, uint flags, ref MemlimitProperties buffer, nuint size);

    [DllImport("libSystem.B.dylib", EntryPoint = "sysctlbyname", SetLastError = true)]
    static extern int SysctlByName(string name, ref ulong value, ref nuint size, nint newValue, nuint newSize);
}
