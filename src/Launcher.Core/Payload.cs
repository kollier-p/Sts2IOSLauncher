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

public sealed record InstallReceipt(uint AppId, uint DepotId, ulong ManifestId, string Branch, string PckSha256, int PatchSchema, int PatchedEntries, string PayloadId, string? InventorySha256 = null)
{
    public string DisplayName() => DepotId == Payload.WindowsDepot ? $"{Branch} / {ManifestId}" : Branch;
}
public sealed class PayloadStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string NewStaging() { Directory.CreateDirectory(Root); return Directory.CreateDirectory(Path.Combine(Root, "stage-" + Guid.NewGuid().ToString("N"))).FullName; }
    public FileStream AcquireWriteLock()
    {
        Directory.CreateDirectory(Root);
        return new FileStream(Path.Combine(Root, ".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public InstallReceipt Install(string staging, ulong manifestId, string branch, CancellationToken ct = default)
    {
        if (manifestId == 0 || string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("Invalid source identity.");
        return Install(staging, Payload.WindowsDepot, manifestId, branch, ct);
    }

    internal InstallReceipt InstallArchive(string staging, string name, CancellationToken ct) => Install(staging, 0, 0, name, ct);

    InstallReceipt Install(string staging, uint depotId, ulong manifestId, string source, CancellationToken ct)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(staging)) != Root || !Path.GetFileName(staging).StartsWith("stage-", StringComparison.Ordinal)) throw new ArgumentException("Staging must be owned by this store.");
        ct.ThrowIfCancellationRequested(); Payload.Validate(staging);
        var patched = PckPatcher.Patch(Payload.SafePath(staging, Payload.Required[0]));
        Payload.Validate(staging);
        var hash = PayloadIntegrity.Hash(Payload.SafePath(staging, Payload.Required[0]), ct).ToLowerInvariant();
        var id = $"{manifestId}-{Guid.NewGuid():N}";
        var receipt = new InstallReceipt(Payload.AppId, depotId, manifestId, source, hash, 2, patched, id, PayloadIntegrity.Create(staging, ct));
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
                if (receipt is null || !ValidIdentity(receipt) || name != "payload-" + receipt.PayloadId) continue;
                // Keep damaged installations visible so users can reclaim their storage.
                versions.Add(receipt);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        return versions.OrderBy(x => x.ManifestId).ThenBy(x => x.PayloadId, StringComparer.Ordinal).ToArray();
    }
    public InstallReceipt ActivateVersion(string payloadId, CancellationToken ct = default)
    {
        using var installLock = AcquireWriteLock();
        ct.ThrowIfCancellationRequested();
        var receipt = InstalledVersions().SingleOrDefault(x => x.PayloadId == payloadId)
            ?? throw new InvalidDataException("Installed version not found.");
        var directory = Payload.SafePath(Root, "payload-" + receipt.PayloadId);
        Payload.Validate(directory);
        if (!string.Equals(PayloadIntegrity.Hash(Payload.SafePath(directory, Payload.Required[0]), ct), receipt.PckSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Installed PCK checksum mismatch.");
        if (receipt.InventorySha256 is not null) PayloadIntegrity.Verify(directory, receipt.InventorySha256, ct);
        var temp = Path.Combine(Root, "current-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew)) { JsonSerializer.Serialize(stream, receipt); stream.Flush(true); }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, Path.Combine(Root, "current.json"), true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return receipt;
    }
    public InstallReceipt? FindVersion(string payloadId)
        => IsPayloadId(payloadId) ? InstalledVersions().SingleOrDefault(receipt => receipt.PayloadId == payloadId) : null;

    public (InstallReceipt Receipt, string Directory) VerifyForLaunch(CancellationToken ct = default, string? payloadId = null)
    {
        var receipt = payloadId is null ? Current() : FindVersion(payloadId);
        if (receipt is null) throw new InvalidDataException("The selected game version is unavailable.");
        var directory = Payload.SafePath(Root, "payload-" + receipt.PayloadId);
        if (receipt.InventorySha256 is null) throw new InvalidDataException("Legacy installation requires reinstallation for full verification.");
        PayloadIntegrity.Verify(directory, receipt.InventorySha256, ct);
        return (receipt, directory);
    }
    public InstallReceipt? Current()
    {
        var receipt = ReadCurrentReceipt();
        if (receipt is null) return null;
        Payload.Validate(Payload.SafePath(Root, "payload-" + receipt.PayloadId));
        return receipt;
    }

    public void RemoveVersion(string payloadId, CancellationToken ct = default)
    {
        using var installLock = AcquireWriteLock();
        ct.ThrowIfCancellationRequested();
        if (ReadCurrentReceipt()?.PayloadId == payloadId)
            throw new InvalidOperationException("Select another version before removing the current installation.");
        var receipt = InstalledVersions().SingleOrDefault(x => x.PayloadId == payloadId)
            ?? throw new InvalidDataException("Installed version not found.");
        var directory = Payload.SafePath(Root, "payload-" + receipt.PayloadId);
        var removal = Payload.SafePath(Root, "remove-" + receipt.PayloadId);
        ct.ThrowIfCancellationRequested();
        // Remove it from the version list before deleting any files.
        Directory.Move(directory, removal);
        Directory.Delete(removal, recursive: true);
    }

    public void CompleteVersionRemovals(CancellationToken ct = default)
    {
        if (!Directory.Exists(Root)) return;
        using var installLock = AcquireWriteLock();
        foreach (var path in Directory.GetDirectories(Root, "remove-*"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            var id = name["remove-".Length..];
            var separator = id.IndexOf('-');
            if (separator <= 0 || !ulong.TryParse(id.AsSpan(0, separator), out _) ||
                !Guid.TryParseExact(id.AsSpan(separator + 1), "N", out _)) continue;
            Directory.Delete(Payload.SafePath(Root, name), recursive: true);
        }
    }

    InstallReceipt? ReadCurrentReceipt()
    {
        var pointer = Path.Combine(Root, "current.json");
        if (!File.Exists(pointer)) return null;
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(File.ReadAllText(pointer)) ?? throw new InvalidDataException("Invalid install receipt.");
        if (!ValidIdentity(receipt)) throw new InvalidDataException("Invalid payload identity.");
        return receipt;
    }

    static bool ValidIdentity(InstallReceipt receipt)
    {
        if (receipt.AppId != Payload.AppId || string.IsNullOrWhiteSpace(receipt.Branch)) return false;
        var validSource = receipt.DepotId == Payload.WindowsDepot && receipt.ManifestId != 0 ||
                          receipt.DepotId == 0 && receipt.ManifestId == 0;
        var prefix = $"{receipt.ManifestId}-";
        return validSource && receipt.PayloadId?.StartsWith(prefix, StringComparison.Ordinal) == true &&
               Guid.TryParseExact(receipt.PayloadId[prefix.Length..], "N", out _);
    }

    internal static bool IsPayloadId(string value)
    {
        var separator = value.IndexOf('-');
        return separator > 0 && ulong.TryParse(value.AsSpan(0, separator), out var manifest) &&
               manifest.ToString() == value[..separator] && Guid.TryParseExact(value.AsSpan(separator + 1), "N", out _);
    }
}
