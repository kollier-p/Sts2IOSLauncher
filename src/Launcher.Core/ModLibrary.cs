using System.IO.Compression;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Launcher.Core;

public sealed record WorkshopOrigin(ulong ItemId, string Branch, ulong? Manifest, uint Updated);
public sealed record ModModule(string Id, string Manifest, string Version, string Author, string[] Dependencies, string? MinimumGameVersion);
public sealed record ModModuleState(ModModule Module, bool? Enabled);
public sealed record InstalledMod(string Key, string Label, string[] ModuleIds, bool Enabled, WorkshopOrigin? Workshop = null,
    ModModule[]? Modules = null, string? Group = null, string? Note = null)
{
    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrEmpty(Note) ? Label : Note;
}
public sealed record ModPreset(string Id, string Name, int Packages);

public sealed class ModLibrary(string activeDirectory)
{
    const string ReceiptFile = ".sts2-mod-install";
    readonly string active = Path.GetFullPath(activeDirectory);
    string Parent => Path.GetDirectoryName(active)!;
    string Disabled => Payload.SafePath(Parent, ".disabled-mods");
    string PauseMarker => Payload.SafePath(Parent, ".mod-loading-paused");
    string PresetRoot => Payload.SafePath(Parent, ".mod-presets");
    string PendingPreset => Payload.SafePath(Parent, ".mod-preset-pending.json");

    sealed record PresetPackage(string Key, bool Enabled, Dictionary<string, bool> Modules);
    sealed record PresetManifest(ModPreset Preset, PresetPackage[] Packages);

    public sealed class PreparedImport : IDisposable
    {
        internal PreparedImport(string directory, string label, ModModule[] modules, WorkshopOrigin? workshop)
        {
            DirectoryPath = directory;
            Label = label;
            ModuleIds = Array.AsReadOnly(modules.Select(module => module.Id).ToArray());
            Modules = modules;
            Workshop = workshop;
        }

        internal string DirectoryPath { get; }
        internal ModModule[] Modules { get; }
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

    public IReadOnlyDictionary<ulong, string> WorkshopInstallations()
    {
        using var operation = Lock();
        Recover();
        var paused = LoadingPaused;
        var result = new Dictionary<ulong, string>();
        foreach (var package in ReadInstalled().Where(package => package.Workshop is not null))
        {
            var root = Location(package.Key, package.Enabled);
            var enabled = package.Enabled && (package.Modules is null || package.Modules.Any(module => ModuleEnabled(root, module) == true));
            if (enabled) result[package.Workshop!.ItemId] = paused ? "Installed · Paused" : "Installed";
            else result.TryAdd(package.Workshop!.ItemId, "Installed · Disabled");
        }
        if (!Directory.Exists(active)) return result;
        foreach (var directory in Directory.EnumerateDirectories(active, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (!ulong.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                id == 0 || (result.TryGetValue(id, out var status) && status != "Installed · Disabled")) continue;
            try
            {
                if (PrepareModules(directory, CancellationToken.None, normalize: false, allowEmpty: true).Length != 0)
                    result[id] = paused ? "Installed · Paused" : "Installed";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                JsonException or BadImageFormatException) { }
        }
        return result;
    }

    public bool LoadingPaused => File.Exists(PauseMarker);

    public void SetLoadingPaused(bool paused)
    {
        using var operation = Lock();
        if (!paused)
        {
            File.Delete(PauseMarker);
            return;
        }
        using var marker = new FileStream(PauseMarker, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        marker.Flush(flushToDisk: true);
    }

    public string RuntimeDirectory(bool skipInstalled = false)
    {
        using var operation = Lock();
        Recover();
        if (!skipInstalled && !LoadingPaused) return active;
        var empty = Payload.SafePath(Parent, ".empty-runtime-mods");
        Directory.CreateDirectory(empty);
        if (Directory.EnumerateFileSystemEntries(empty).Any())
            throw new InvalidDataException("The paused mod directory contains unexpected files.");
        return empty;
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
            var modules = PrepareModules(stage, ct, allowEmpty: true);
            if (modules.Length == 0)
            {
                var archives = files.Select(file => (File: file, Format: ArchiveFormats.Detect(file)))
                    .Where(candidate => candidate.Format.HasValue).Take(2).ToArray();
                if (archives is not [var archive])
                    throw new InvalidDataException(archives.Length == 0 ? "No mod manifest with a neighboring managed DLL or resource pack was found." :
                        "The download contains multiple archives. Import the intended archive separately.");
                var nested = Payload.SafePath(Parent, ".mod-stage-" + Guid.NewGuid().ToString("N") + ".archive");
                File.Move(archive.File, nested);
                try
                {
                    Directory.Delete(stage, recursive: true);
                    Directory.CreateDirectory(stage);
                    await ExtractAsync(nested, archive.Format!.Value, stage, ct);
                }
                finally { File.Delete(nested); }
                modules = PrepareModules(stage, ct);
            }
            var label = new string(displayName.Where(c => !char.IsControl(c)).Take(128).ToArray()).Trim();
            return new PreparedImport(stage, label.Length == 0 ? modules[0].Id : label, modules, workshop);
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
        var result = new InstalledMod(key, import.Label, import.ModuleIds.ToArray(), enabled, import.Workshop, import.Modules,
            previous?.Group, previous?.Note);
        var destination = Location(key, enabled);
        if (previous?.Modules is { } previousModules)
        {
            var disabled = previousModules.Where(module => ModuleEnabled(destination, module) == false)
                .Select(module => module.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var module in import.Modules.Where(module => disabled.Contains(module.Id)))
                SetModuleEnabled(import.DirectoryPath, module, false);
        }
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
        return MoveEnabled(current, enabled);
    }

    public InstalledMod SetPresentation(string key, string? group, string? note)
    {
        group = CleanLabel(group, 64);
        note = CleanLabel(note, 96);
        using var operation = Lock();
        Recover();
        var current = Get(key);
        var updated = current with { Group = group, Note = note };
        WriteReceipt(Location(key, current.Enabled), updated, update: true);
        return updated;
    }

    static string? CleanLabel(string? value, int maximum)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maximum || trimmed.Any(char.IsControl)) throw new ArgumentException("The mod label has invalid characters or length.");
        return trimmed;
    }

    InstalledMod MoveEnabled(InstalledMod current, bool enabled)
    {
        if (current.Enabled == enabled) return current;
        var destination = Location(current.Key, enabled);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(Location(current.Key, current.Enabled), destination);
        return current with { Enabled = enabled };
    }

    public IReadOnlyList<ModPreset> Presets()
    {
        using var operation = Lock();
        Recover();
        return ReadPresets().Select(item => item.Preset).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ModPreset CreatePreset(string name)
    {
        name = name.Trim();
        if (!ValidPresetName(name)) throw new ArgumentException("Enter a preset name of 1–64 characters.");
        using var operation = Lock();
        Recover();
        if (ReadPresets().Any(item => item.Preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A mod preset with this name already exists.");
        var packages = ReadInstalled().Select(package =>
        {
            var root = Location(package.Key, package.Enabled);
            var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in package.Modules ?? [])
                if (ModuleEnabled(root, module) is bool enabled) states.Add(module.Id, enabled);
            return new PresetPackage(package.Key, package.Enabled, states);
        }).ToArray();
        if (packages.Length == 0) throw new InvalidOperationException("Import a mod before creating a preset.");
        var preset = new ModPreset(Guid.NewGuid().ToString("N"), name, packages.Length);
        Directory.CreateDirectory(PresetRoot);
        WritePreset(PresetPath(preset.Id), new(preset, packages));
        return preset;
    }

    public void ApplyPreset(string id, CancellationToken ct)
    {
        using var operation = Lock();
        Recover();
        var preset = ReadPreset(PresetPath(id)) ?? throw new InvalidDataException("The mod preset is unavailable.");
        ct.ThrowIfCancellationRequested();
        WritePreset(PendingPreset, preset);
        CompletePendingPreset();
    }

    public void DeletePreset(string id)
    {
        using var operation = Lock();
        Recover();
        File.Delete(PresetPath(id));
    }

    string PresetPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid mod preset ID.");
        return Payload.SafePath(PresetRoot, id + ".json");
    }

    IEnumerable<PresetManifest> ReadPresets()
    {
        if (!Directory.Exists(PresetRoot)) yield break;
        foreach (var file in Directory.EnumerateFiles(PresetRoot, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (Guid.TryParseExact(id, "N", out _) && ReadPreset(PresetPath(id)) is { } manifest && manifest.Preset.Id == id)
                yield return manifest;
        }
    }

    static PresetManifest? ReadPreset(string path)
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        var preset = JsonSerializer.Deserialize<PresetManifest>(input);
        if (preset?.Preset is null || !Guid.TryParseExact(preset.Preset.Id, "N", out _) || !ValidPresetName(preset.Preset.Name) ||
            preset.Packages is null || preset.Packages.Length != preset.Preset.Packages || preset.Packages.Any(package =>
                package is null || !Guid.TryParseExact(package.Key, "N", out _) || package.Modules is null ||
                !package.Modules.Keys.All(ValidModuleName) ||
                package.Modules.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Modules.Count) ||
            preset.Packages.Select(package => package.Key).Distinct(StringComparer.Ordinal).Count() != preset.Packages.Length)
            throw new InvalidDataException("The mod preset is invalid.");
        return preset;
    }

    static bool ValidPresetName(string? name) => name is { Length: >= 1 and <= 64 } && !name.Any(char.IsControl);

    static void WritePreset(string path, PresetManifest preset)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(staging, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(output, preset);
                output.Flush(flushToDisk: true);
            }
            File.Move(staging, path);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    void CompletePendingPreset()
    {
        var pending = ReadPreset(PendingPreset);
        if (pending is null) return;
        var installed = ReadInstalled().ToDictionary(package => package.Key, StringComparer.Ordinal);
        foreach (var target in pending.Packages)
        {
            if (!installed.TryGetValue(target.Key, out var current)) continue;
            current = MoveEnabled(current, target.Enabled);
            var root = Location(current.Key, current.Enabled);
            var states = new Dictionary<string, bool>(target.Modules, StringComparer.OrdinalIgnoreCase);
            foreach (var module in current.Modules ?? [])
                if (states.TryGetValue(module.Id, out var enabled) && ModuleEnabled(root, module) is not null)
                    SetModuleEnabled(root, module, enabled);
        }
        File.Delete(PendingPreset);
    }

    public IReadOnlyList<ModModuleState> Modules(string key)
    {
        using var operation = Lock();
        Recover();
        var package = Get(key);
        var root = Location(key, package.Enabled);
        return package.Modules?.Select(module => new ModModuleState(module, ModuleEnabled(root, module))).ToArray() ?? [];
    }

    public IReadOnlySet<string> EnabledModuleIds()
    {
        using var operation = Lock();
        Recover();
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in ReadInstalled().Where(package => package.Enabled))
        {
            if (package.Modules is null) enabled.UnionWith(package.ModuleIds);
            else
            {
                var root = Location(package.Key, true);
                enabled.UnionWith(package.Modules.Where(module => ModuleEnabled(root, module) == true).Select(module => module.Id));
            }
        }
        return enabled;
    }

    public void SetModuleEnabled(string key, string id, bool enabled)
    {
        using var operation = Lock();
        Recover();
        var package = Get(key);
        var module = package.Modules?.SingleOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The installed package does not describe this module.");
        SetModuleEnabled(Location(key, package.Enabled), module, enabled);
    }

    static bool? ModuleEnabled(string root, ModModule module)
    {
        var manifest = Payload.SafePath(root, module.Manifest);
        var disabled = Payload.SafePath(root, module.Manifest + ".disabled");
        var enabled = File.Exists(manifest);
        return enabled == File.Exists(disabled) ? null : enabled;
    }

    static void SetModuleEnabled(string root, ModModule module, bool enabled)
    {
        var current = ModuleEnabled(root, module) ?? throw new InvalidDataException($"Module {module.Id} has a missing or duplicate manifest.");
        if (current == enabled) return;
        var active = Payload.SafePath(root, module.Manifest);
        var inactive = Payload.SafePath(root, module.Manifest + ".disabled");
        File.Move(enabled ? inactive : active, enabled ? active : inactive);
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
        if (workshop is null && import.Workshop is { } incomingOrigin && matches is [var overlap] &&
            overlap.Workshop?.ItemId == incomingOrigin.ItemId) workshop = overlap;
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
                    InstalledMod? mod;
                    using (var stream = File.OpenRead(receipt))
                        mod = JsonSerializer.Deserialize<InstalledMod>(stream);
                    if (mod?.Key == name && mod.ModuleIds is { Length: > 0 } && mod.ModuleIds.All(ValidModuleName) &&
                        (mod.Modules is null || ValidModules(mod)) &&
                        (mod.Workshop is null || (mod.Workshop.ItemId != 0 && !string.IsNullOrWhiteSpace(mod.Workshop.Branch))))
                    {
                        var current = mod with { Enabled = enabled };
                        result.Add(current.Modules is null ? TryUpgradeReceipt(entry, current) : current);
                    }
                }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
            }
        }
        return result.OrderBy(mod => mod.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.DisplayLabel, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static InstalledMod TryUpgradeReceipt(string path, InstalledMod original)
    {
        try
        {
            var modules = PrepareModules(path, CancellationToken.None, normalize: false);
            if (!modules.Select(module => module.Id).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(original.ModuleIds))
                return original;
            var updated = original with { Modules = modules, ModuleIds = modules.Select(module => module.Id).ToArray() };
            WriteReceipt(path, updated, update: true);
            return updated;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or BadImageFormatException)
        { return original; }
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
        CompletePendingPreset();
    }

    static void WriteReceipt(string path, InstalledMod mod, bool update = false)
    {
        var destination = Payload.SafePath(path, ReceiptFile);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, mod);
                stream.Flush(true);
            }
            File.Move(temporary, destination, overwrite: update);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static ModModule[] PrepareModules(string stage, CancellationToken ct, bool normalize = true, bool allowEmpty = false)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modules = new List<ModModule>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var manifest in Directory.EnumerateFiles(stage, "*.json", options))
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
            if (normalize && legacy && !normalized.Equals(manifest, StringComparison.OrdinalIgnoreCase))
                File.Move(manifest, normalized, overwrite: false);
            else
                normalized = manifest;
            var relative = Path.GetRelativePath(stage, normalized).Replace(Path.DirectorySeparatorChar, '/');
            if (File.Exists(Payload.SafePath(stage, relative + ".disabled")))
                throw new InvalidDataException("The mod package contains an existing disabled manifest.");
            modules.Add(new(id, relative, Metadata(json, "version"), Metadata(json, "author"),
                Dependencies(json), Metadata(json, "min_game_version", "minGameVersion")));
        }
        if (!allowEmpty && ids.Count == 0) throw new InvalidDataException("The package has no mod manifests with a managed DLL or resource pack.");
        return modules.OrderBy(module => module.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string[] Dependencies(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("dependencies", out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Select(entry => entry.ValueKind == JsonValueKind.String ? entry.GetString() :
            entry.ValueKind == JsonValueKind.Object ? ManifestString(entry, "id", "mod_id", "modId", "ID") : null)
            .OfType<string>().Where(ValidModuleName).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
    }

    static string Metadata(JsonElement manifest, params string[] keys)
        => new((ManifestString(manifest, keys) ?? "").Where(character => !char.IsControl(character)).Take(128).ToArray());

    static bool ValidModules(InstalledMod mod)
    {
        var modules = mod.Modules!;
        if (modules.Length != mod.ModuleIds.Length ||
            !modules.Select(module => module?.Id).SequenceEqual(mod.ModuleIds, StringComparer.OrdinalIgnoreCase)) return false;
        try
        {
            return modules.All(module => module.Manifest is not null && module.Dependencies is not null &&
                module.Manifest.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                Payload.Normalize(module.Manifest) == module.Manifest && module.Dependencies.All(ValidModuleName));
        }
        catch (InvalidDataException) { return false; }
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
