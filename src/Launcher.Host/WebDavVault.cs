using System.Runtime.InteropServices;
using Launcher.Core;

namespace Launcher.Host;

internal static class WebDavVault
{
    [DllImport("__Internal", EntryPoint = "sts2_webdav_write")]
    static extern int Write([MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport("__Internal", EntryPoint = "sts2_webdav_read")]
    static extern int Read([Out] byte[] buffer, int capacity);

    [DllImport("__Internal", EntryPoint = "sts2_webdav_clear")]
    public static extern int Clear();

    public static WebDavSettings? Load() => KeychainJson.Load<WebDavSettings>(Read);
    public static void Save(WebDavSettings settings) => KeychainJson.Save(settings, Write);
}
