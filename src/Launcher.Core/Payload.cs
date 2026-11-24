using System.Security.Cryptography;
using System.Text.Json;

namespace Launcher.Core;

public static class Payload
{
    public const uint AppId = 2868840, WindowsDepot = 2868841;
    public const string AssemblyDirectory = "data_sts2_windows_x86_64";
    public static readonly string[] Required = ["SlayTheSpire2.pck", "release_info.json", $"{AssemblyDirectory}/sts2.dll", $"{AssemblyDirectory}/sts2.deps.json", $"{AssemblyDirectory}/sts2.runtimeconfig.json"];
    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.Contains(':') || path.Contains('\0') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Unsafe depot path.");
        return path;
    }
    public static bool Include(string path) => path is "SlayTheSpire2.pck" or "release_info.json" || path.StartsWith(AssemblyDirectory + "/", StringComparison.Ordinal);
    public static string SafePath(string root, string path)
    {
        path = Normalize(path);
        var current = Path.GetFullPath(root);
        foreach (var part in path.Split('/'))
        {
            current = Path.Combine(current, part);
            var info = new FileInfo(current);
            if (info.LinkTarget != null || (File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Symlink in payload path.");
        }
        return current;
    }
    public static void Validate(string root)
    {
        foreach (var path in Required)
        {
            var file = SafePath(root, path);
            if (!File.Exists(file) || new FileInfo(file).Length == 0) throw new InvalidDataException($"Missing or empty {path}");
            if (path.EndsWith(".json")) { using var data = JsonDocument.Parse(File.ReadAllBytes(file)); if (data.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Invalid JSON object: {path}"); }
        }
        using var stream = File.OpenRead(SafePath(root, Required[0]));
        Span<byte> magic = stackalloc byte[4]; stream.ReadExactly(magic);
        if (!magic.SequenceEqual("GDPC"u8)) throw new InvalidDataException("Invalid PCK magic.");
        using var dll = File.OpenRead(SafePath(root, Required[2]));
        using var pe = new System.Reflection.PortableExecutable.PEReader(dll);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) throw new InvalidDataException("sts2.dll is not a managed assembly.");
    }
}

public sealed record InstallReceipt(uint AppId, uint DepotId, ulong ManifestId, string Branch, string PckSha256, int PatchSchema, int PatchedEntries, string PayloadId);
public sealed class PayloadStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string NewStaging() { Directory.CreateDirectory(Root); return Directory.CreateDirectory(Path.Combine(Root, "stage-" + Guid.NewGuid().ToString("N"))).FullName; }
    public InstallReceipt Install(string staging, ulong manifestId, string branch, CancellationToken ct = default)
    {
        if (manifestId == 0 || string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Invalid source identity.");
        if (Path.GetDirectoryName(Path.GetFullPath(staging)) != Root || !Path.GetFileName(staging).StartsWith("stage-", StringComparison.Ordinal)) throw new ArgumentException("Staging must be owned by this store.");
        ct.ThrowIfCancellationRequested(); Payload.Validate(staging);
        var patched = PckPatcher.Patch(Payload.SafePath(staging, Payload.Required[0]));
        Payload.Validate(staging);
        using var file = File.OpenRead(Payload.SafePath(staging, Payload.Required[0]));
        var hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); file.Dispose();
        var id = $"{manifestId}-{Guid.NewGuid():N}";
        var receipt = new InstallReceipt(Payload.AppId, Payload.WindowsDepot, manifestId, branch, hash, 2, patched, id);
        File.WriteAllText(Path.Combine(staging, ".payload_manifest.json"), JsonSerializer.Serialize(receipt));
        ct.ThrowIfCancellationRequested();
        var destination = Path.Combine(Root, "payload-" + id);
        Directory.Move(staging, destination);
        // The old payload stays active until the new pointer is atomically renamed into place.
        var temp = Path.Combine(Root, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (var stream = new FileStream(temp, FileMode.CreateNew)) { JsonSerializer.Serialize(stream, receipt); stream.Flush(true); } File.Move(temp, Path.Combine(Root, "current.json"), true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return receipt;
    }
    public IReadOnlyList<InstallReceipt> InstalledVersions()
    {
        if (!Directory.Exists(Root)) return [];
        var versions = new List<InstallReceipt>();
        foreach (var directory in Directory.EnumerateDirectories(Root, "payload-*"))
        {
            try
            {
                var name = Path.GetFileName(directory);
                var safe = Payload.SafePath(Root, name);
                var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(Payload.SafePath(safe, ".payload_manifest.json")));
                if (receipt is null || receipt.AppId != Payload.AppId || receipt.DepotId != Payload.WindowsDepot ||
                    receipt.ManifestId == 0 || name != "payload-" + receipt.PayloadId ||
                    !receipt.PayloadId.StartsWith(receipt.ManifestId + "-", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(receipt.PayloadId[(receipt.ManifestId.ToString().Length + 1)..], "N", out _)) continue;
                Payload.Validate(safe);
                versions.Add(receipt);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        return versions.OrderBy(x => x.ManifestId).ThenBy(x => x.PayloadId, StringComparer.Ordinal).ToArray();
    }
    public InstallReceipt ActivateVersion(string payloadId)
    {
        Directory.CreateDirectory(Root);
        using var installLock = new FileStream(Path.Combine(Root, ".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var receipt = InstalledVersions().SingleOrDefault(x => x.PayloadId == payloadId)
            ?? throw new InvalidDataException("Installed version not found.");
        var directory = Payload.SafePath(Root, "payload-" + receipt.PayloadId);
        using (var stream = File.OpenRead(Payload.SafePath(directory, Payload.Required[0])))
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), receipt.PckSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Installed PCK checksum mismatch.");
        var temp = Path.Combine(Root, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew)) { JsonSerializer.Serialize(stream, receipt); stream.Flush(true); }
            File.Move(temp, Path.Combine(Root, "current.json"), true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return receipt;
    }
    public InstallReceipt? Current()
    {
        var pointer = Path.Combine(Root, "current.json");
        if (!File.Exists(pointer)) return null;
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(pointer)) ?? throw new InvalidDataException("Invalid install receipt.");
        if (receipt.AppId != Payload.AppId || receipt.DepotId != Payload.WindowsDepot || receipt.ManifestId == 0 || !receipt.PayloadId.StartsWith(receipt.ManifestId + "-", StringComparison.Ordinal) || !Guid.TryParseExact(receipt.PayloadId[(receipt.ManifestId.ToString().Length + 1)..], "N", out _)) throw new InvalidDataException("Invalid payload identity.");
        var directory = Payload.SafePath(Root, "payload-" + receipt.PayloadId); Payload.Validate(directory);
        return receipt;
    }
}
