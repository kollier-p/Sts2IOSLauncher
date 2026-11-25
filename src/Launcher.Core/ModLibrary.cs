using System.IO.Compression;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Launcher.Core;

public sealed record WorkshopOrigin(ulong ItemId, string Branch, ulong? Manifest, uint Updated);
public sealed record InstalledMod(string Key, string Label, string[] ModuleIds, bool Enabled, WorkshopOrigin? Workshop = null);

public sealed class ModLibrary(string activeDirectory)
{
    const string ReceiptFile = ".sts2-mod-install";
    readonly string active = Path.GetFullPath(activeDirectory);
    string Parent => Path.GetDirectoryName(active)!;
    string Disabled => Payload.SafePath(Parent, ".disabled-mods");

    public sealed class PreparedImport : IDisposable
    {
        internal PreparedImport(string directory, string label, string[] modules, WorkshopOrigin? workshop)
        {
            DirectoryPath = directory;
            Label = label;
            ModuleIds = Array.AsReadOnly(modules);
            Workshop = workshop;
        }

        internal string DirectoryPath { get; }
        public string Label { get; }
        public IReadOnlyList<string> ModuleIds { get; }
        public WorkshopOrigin? Workshop { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    public IReadOnlyList<InstalledMod> List()
    {
        using var operation = Lock();
        Recover();
        return ReadInstalled();
    }

    public Task<PreparedImport> PrepareArchiveAsync(string source, CancellationToken ct, string? displayName = null,
        WorkshopOrigin? workshop = null)
    {
        ct.ThrowIfCancellationRequested();
        var format = ArchiveFormats.Detect(source) ?? throw new NotSupportedException("Unsupported mod archive format.");
        return PrepareAsync(displayName ?? ArchiveFormats.DisplayName(source), ct,
            (stage, token) => ExtractAsync(source, format, stage, token), workshop);
    }

    public Task<PreparedImport> PrepareDownloadAsync(string displayName, Func<string, CancellationToken, Task> download,
        CancellationToken ct, WorkshopOrigin? workshop = null) => PrepareAsync(displayName, ct, download, workshop);

    async Task<PreparedImport> PrepareAsync(string displayName, CancellationToken ct, Func<string, CancellationToken, Task> populate,
        WorkshopOrigin? workshop)
    {
        var stage = Payload.SafePath(Parent, ".mod-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stage);
            await populate(stage, ct);
            var files = Directory.GetFiles(stage, "*", SearchOption.AllDirectories);
            if (files.Any(file => Path.GetFileName(file).Equals(ReceiptFile, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Reserved mod package file.");
            if (files.Length == 1 && ArchiveFormats.Detect(files[0]) is { } format)
            {
                var nested = Payload.SafePath(Parent, ".mod-stage-" + Guid.NewGuid().ToString("N") + ".archive");
                File.Move(files[0], nested);
                try
                {
                    Directory.Delete(stage, recursive: true);
                    Directory.CreateDirectory(stage);
                    await ExtractAsync(nested, format, stage, ct);
                }
                finally { File.Delete(nested); }
            }
            var modules = PrepareModules(stage, ct);
            var label = new string(displayName.Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
            return new PreparedImport(stage, label.Length == 0 ? modules[0] : label, modules, workshop);
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
    }

    static async Task ExtractAsync(string source, ArchiveFormat format, string stage, CancellationToken ct)
    {
        const long MaximumSize = 16L * 1024 * 1024 * 1024;
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[65536];
        long bytes = 0;
        async Task WriteEntry(string name, long length, bool regular, Func<Stream> open)
        {
            if (!regular || !entries.Add(name) || Path.GetFileName(name).Equals(ReceiptFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Duplicate, linked, or reserved mod archive entry.");
            if (length < 0 || length > MaximumSize - bytes) throw new InvalidDataException("The mod archive is too large.");
            bytes += length;
            PayloadStorage.EnsureCapacity(stage, length);
            await ArchiveImport.CopyEntryAsync(stage, name, open(), length, buffer, null, ct);
        }
        if (format != ArchiveFormat.Zip)
        {
            await ArchiveFormats.ReadAsync(source, format, WriteEntry, ct);
            return;
        }
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > 200_000) throw new InvalidDataException("The mod archive contains too many entries.");
        var size = zip.Entries.Sum(entry => entry.Length);
        if (size > MaximumSize) throw new InvalidDataException("The mod archive is too large.");
        PayloadStorage.EnsureCapacity(stage, size);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name.Length == 0) continue;
            var name = Payload.Normalize(entry.FullName);
            var mode = (entry.ExternalAttributes >> 16) & 0xf000;
            Stream? opened = null;
            try { await WriteEntry(name, entry.Length, mode is 0 or 0x8000, () => opened = entry.Open()); }
            finally { opened?.Dispose(); }
        }
    }

    public InstalledMod? Replacement(PreparedImport import)
    {
        using var operation = Lock();
        Recover();
        return Match(import, ReadInstalled());
    }

    public InstalledMod Install(PreparedImport import, bool replace, CancellationToken ct)
    {
        using var operation = Lock();
        Recover();
        ct.ThrowIfCancellationRequested();
        if (Path.GetDirectoryName(import.DirectoryPath) != Parent ||
            !Path.GetFileName(import.DirectoryPath).StartsWith(".mod-stage-", StringComparison.Ordinal))
            throw new InvalidDataException("The staging directory belongs to another profile.");
        var previous = Match(import, ReadInstalled());
        if (previous is not null && !replace) throw new InvalidOperationException("Confirm replacement of the installed mod.");
        var key = previous?.Key ?? Guid.NewGuid().ToString("N");
        var enabled = previous?.Enabled ?? true;
        var result = new InstalledMod(key, import.Label, import.ModuleIds.ToArray(), enabled, import.Workshop);
        var destination = Location(key, enabled);
        WriteReceipt(import.DirectoryPath, result);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ct.ThrowIfCancellationRequested();
        var backup = BackupLocation(key, enabled);
        if (previous is not null) Directory.Move(destination, backup);
        try { Directory.Move(import.DirectoryPath, destination); }
        catch
        {
            if (Directory.Exists(backup) && !Directory.Exists(destination)) Directory.Move(backup, destination);
            throw;
        }
        Recover();
        return result;
    }

    public InstalledMod SetEnabled(string key, bool enabled)
    {
        using var operation = Lock();
        Recover();
        var current = Get(key);
        if (current.Enabled == enabled) return current;
        var destination = Location(key, enabled);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(Location(key, current.Enabled), destination);
        return current with { Enabled = enabled };
    }

    public void Remove(string key)
    {
        using var operation = Lock();
        Recover();
        var current = Get(key);
        var removed = Payload.SafePath(Parent, ".removed-mod-" + key);
        Directory.Move(Location(key, current.Enabled), removed);
        Directory.Delete(removed, recursive: true);
    }

    FileStream Lock()
    {
        Directory.CreateDirectory(Parent);
        return new FileStream(Payload.SafePath(Parent, ".sts2-mods.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    InstalledMod Get(string key) => ReadInstalled().SingleOrDefault(mod => mod.Key == key)
        ?? throw new InvalidDataException("The installed mod package was not found.");

    static InstalledMod? Match(PreparedImport import, IReadOnlyList<InstalledMod> installed)
    {
        var incoming = new HashSet<string>(import.ModuleIds, StringComparer.OrdinalIgnoreCase);
        var workshop = import.Workshop is { } origin ? installed.SingleOrDefault(mod => mod.Workshop is { } old &&
            old.ItemId == origin.ItemId && old.Branch.Equals(origin.Branch, StringComparison.OrdinalIgnoreCase)) : null;
        var matches = installed.Where(mod => mod.ModuleIds.Any(incoming.Contains)).ToArray();
        if (workshop is not null)
        {
            if (matches.Any(mod => mod.Key != workshop.Key))
                throw new InvalidDataException("The Workshop update overlaps another installed package.");
            return workshop;
        }
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || !incoming.SetEquals(matches[0].ModuleIds))
            throw new InvalidDataException("The archive overlaps multiple installed mod packages or changes their membership.");
        return matches[0];
    }

    IReadOnlyList<InstalledMod> ReadInstalled()
    {
        var result = new List<InstalledMod>();
        foreach (var enabled in new[] { true, false })
        {
            var directory = enabled ? active : Disabled;
            if (!Directory.Exists(directory)) continue;
            foreach (var entry in Directory.GetDirectories(directory))
            {
                var name = Path.GetFileName(entry);
                if (!Guid.TryParseExact(name, "N", out _)) continue;
                var receipt = Payload.SafePath(directory, name + "/" + ReceiptFile);
                if (!File.Exists(receipt)) continue;
                try
                {
                    using var stream = File.OpenRead(receipt);
                    var mod = JsonSerializer.Deserialize<InstalledMod>(stream);
                    if (mod?.Key == name && mod.ModuleIds is { Length: > 0 } && mod.ModuleIds.All(ValidModuleName) &&
                        (mod.Workshop is null || (mod.Workshop.ItemId != 0 && !string.IsNullOrWhiteSpace(mod.Workshop.Branch))))
                        result.Add(mod with { Enabled = enabled });
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
        return result.OrderBy(mod => mod.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    string Location(string key, bool enabled)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new InvalidDataException("Invalid mod package identifier.");
        return Payload.SafePath(enabled ? active : Disabled, key);
    }

    string BackupLocation(string key, bool enabled) => Payload.SafePath(Parent, $".replacing-mod-{(enabled ? 'e' : 'd')}-{key}");

    void Recover()
    {
        foreach (var path in Directory.GetDirectories(Parent, ".replacing-mod-*"))
        {
            var name = Path.GetFileName(path);
            var parts = name.Split('-');
            if (parts.Length != 4 || parts[2] is not ("e" or "d") || !Guid.TryParseExact(parts[3], "N", out _)) continue;
            var backup = Payload.SafePath(Parent, name);
            var destination = Location(parts[3], parts[2] == "e");
            if (Directory.Exists(destination)) Directory.Delete(backup, recursive: true);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(backup, destination);
            }
        }
        foreach (var path in Directory.GetDirectories(Parent, ".removed-mod-*"))
        {
            var name = Path.GetFileName(path);
            if (Guid.TryParseExact(name.AsSpan(".removed-mod-".Length), "N", out _))
                Directory.Delete(Payload.SafePath(Parent, name), recursive: true);
        }
    }

    static void WriteReceipt(string path, InstalledMod mod)
    {
        using var stream = new FileStream(Path.Combine(path, ReceiptFile), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, mod);
        stream.Flush(true);
    }

    static string[] PrepareModules(string stage, CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in Directory.GetFiles(stage, "*.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var legacy = Path.GetFileName(manifest).Equals("mod_manifest.json", StringComparison.OrdinalIgnoreCase);
            if (new FileInfo(manifest).Length > 1024 * 1024)
            {
                if (legacy) throw new InvalidDataException("The mod manifest is too large.");
                continue;
            }
            using var doc = ReadManifest(manifest, legacy);
            if (doc?.RootElement.ValueKind != JsonValueKind.Object) continue;
            var json = doc.RootElement;
            var id = ManifestString(json, "id", "mod_id", "modId", "ID");
            if (id is null) { if (legacy) throw new InvalidDataException("The mod manifest has no identifier."); continue; }
            var pack = ManifestString(json, "pck_name", "pckName") ?? id;
            var parent = Path.GetDirectoryName(manifest)!;
            if (!ValidModuleName(id) || !ValidModuleName(pack)) throw new InvalidDataException("Invalid mod identifier or pack name.");
            var library = Payload.SafePath(parent, id + ".dll");
            var pck = Payload.SafePath(parent, pack.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) ? pack : pack + ".pck");
            if (!File.Exists(library) && !File.Exists(pck))
            {
                if (legacy) throw new InvalidDataException("The mod archive is missing its DLL or PCK.");
                continue;
            }
            if (!ids.Add(id)) throw new InvalidDataException("The archive contains duplicate mod identifiers.");
            if (File.Exists(library))
            {
                using var stream = File.OpenRead(library);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) throw new InvalidDataException("A mod DLL has no managed metadata.");
            }
            if (File.Exists(pck))
            {
                using var stream = File.OpenRead(pck);
                var bytes = new byte[4];
                stream.ReadExactly(bytes);
                if (!bytes.AsSpan().SequenceEqual("GDPC"u8)) throw new InvalidDataException("A mod resource pack is invalid.");
            }
            var normalized = Payload.SafePath(parent, id + ".json");
            if (legacy && !normalized.Equals(manifest, StringComparison.OrdinalIgnoreCase))
                File.Move(manifest, normalized, overwrite: false);
        }
        if (ids.Count == 0) throw new InvalidDataException("The package has no mod manifests with a managed DLL or resource pack.");
        return ids.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static JsonDocument? ReadManifest(string path, bool required)
    {
        try
        {
            using var input = File.OpenRead(path);
            return JsonDocument.Parse(input);
        }
        catch (JsonException) when (!required) { return null; }
    }

    static string? ManifestString(JsonElement manifest, params string[] keys)
    {
        foreach (var key in keys)
            if (manifest.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    static bool ValidModuleName(string? name)
        => name is { Length: >= 1 and <= 128 } && name == name.Trim() && name is not ("." or "..") &&
           !name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c));
}
