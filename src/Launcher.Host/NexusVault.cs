using System.Runtime.InteropServices;

namespace Launcher.Host;

internal static class NexusVault
{
    [DllImport("__Internal", EntryPoint = "sts2_nexus_write")]
    static extern int Write([MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("__Internal", EntryPoint = "sts2_nexus_read")]
    static extern int Read([Out] byte[] buffer, int capacity);

    [DllImport("__Internal", EntryPoint = "sts2_nexus_clear")]
    public static extern int Clear();

    public static string? Load() => KeychainJson.Load<string>(Read);
    public static void Save(string apiKey) => KeychainJson.Save(apiKey, Write);
}
