using System.Runtime.InteropServices;
using System.Text.Json;
using System.Security.Cryptography;
using Launcher.Steam;
namespace Launcher.Host;
internal static class SteamVault
{
    [DllImport("__Internal",EntryPoint="sts2_vault_write")]
    static extern int Write([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("__Internal",EntryPoint="sts2_vault_read")]
    static extern int Read(byte[] buffer,int capacity);
    [DllImport("__Internal",EntryPoint="sts2_vault_clear")]
    static extern int ClearNative();
    public static SteamIdentity? Load(){
        var buffer=new byte[65536];
        try{var count=Read(buffer,buffer.Length);return count>0&&count<buffer.Length?JsonSerializer.Deserialize<SteamIdentity>(buffer.AsSpan(0,count)):null;}
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }
    public static void Save(SteamIdentity identity){if(Write(JsonSerializer.Serialize(identity))!=0)throw new IOException("Keychain write failed.");}
    public static int Clear()=>ClearNative();
}
