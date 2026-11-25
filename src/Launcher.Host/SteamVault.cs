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
    static extern int Read([Out] byte[] buffer,int capacity);
    [DllImport("__Internal",EntryPoint="sts2_vault_clear")]
    static extern int ClearNative();
    public static SteamIdentity? Load()
    {
        const int itemNotFound = -25300;
        var identity = KeychainJson.Load<SteamIdentity>((buffer, capacity) =>
        {
            var count = Read(buffer, capacity);
            if (count < 0 && count != itemNotFound) throw new IOException($"Steam login storage read failed ({count}).");
            return count;
        });
        return identity is not null && !string.IsNullOrWhiteSpace(identity.AccountName) &&
            !string.IsNullOrWhiteSpace(identity.RefreshToken) ? identity : null;
    }
    public static void Save(SteamIdentity identity) => KeychainJson.Save(identity, Write);
    public static void Clear()
    {
        var result = ClearNative();
        if (result != 0) throw new IOException($"Steam login storage deletion failed ({result}).");
    }
}

internal static class KeychainJson
{
    public static T? Load<T>(Func<byte[], int, int> read) where T : class
    {
        var buffer=new byte[65536];
        try{var count=read(buffer,buffer.Length);return count>0&&count<buffer.Length?JsonSerializer.Deserialize<T>(buffer.AsSpan(0,count)):null;}
        finally{CryptographicOperations.ZeroMemory(buffer);}
    }

    public static void Save<T>(T value, Func<string, int> write)
    {
        if (write(JsonSerializer.Serialize(value)) != 0) throw new IOException("Keychain write failed.");
    }
}
