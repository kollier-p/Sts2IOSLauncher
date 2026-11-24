using System.Text.Json;

namespace Launcher.Core;

public sealed record LaunchProfile(string Id, string Name, bool IsolateSaves, bool IsolateMods, string? PayloadId = null);

public sealed record LaunchProfilePaths(string ModsDirectory, string? SaveDirectory, string? RelativeSavePath)
{
    public string GodotSavePath(params string?[] parts)
    {
        var path = RelativeSavePath ?? throw new InvalidOperationException("This profile shares the game's save directory.");
        foreach (var part in parts)
        {
            var relative = part?.Trim().Replace('\\', '/').Trim('/');
            if (!string.IsNullOrEmpty(relative)) path += "/" + Payload.Normalize(relative);
        }
        return "user://" + path;
    }
}

public sealed class LaunchProfiles(string settingsFile, string documents)
{
    public static LaunchProfile Default { get; } = new("default", "Default", false, false);
    sealed record Configuration(string Active, LaunchProfile[] Profiles);

    readonly string settings = Path.GetFullPath(settingsFile);
    readonly string data = Path.GetFullPath(documents);
    readonly object sync = new();

    public LaunchProfile Current()
    {
        lock (sync)
        {
            var config = Read();
            return config.Active == Default.Id ? Default : config.Profiles.Single(profile => profile.Id == config.Active);
        }
    }

    public IReadOnlyList<LaunchProfile> List()
    {
        lock (sync) return [Default, .. Read().Profiles.OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public LaunchProfile Create(string name, bool isolateSaves, bool isolateMods)
    {
        name = name.Trim();
        if (!ValidName(name)) throw new ArgumentException("Enter a profile name between 1 and 64 characters.");
        lock (sync)
        {
            var config = Read();
            if (string.Equals(Default.Name, name, StringComparison.OrdinalIgnoreCase) ||
                config.Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A profile with that name already exists.");
            var profile = new LaunchProfile(Guid.NewGuid().ToString("N"), name, isolateSaves, isolateMods);
            EnsureDirectories(profile);
            Write(new(profile.Id, [.. config.Profiles, profile]));
            return profile;
        }
    }

    public LaunchProfile Select(string id)
    {
        lock (sync)
        {
            var config = Read();
            var profile = id == Default.Id ? Default : config.Profiles.SingleOrDefault(profile => profile.Id == id)
                ?? throw new InvalidDataException("Launch profile not found.");
            EnsureDirectories(profile);
            if (config.Active != id) Write(config with { Active = id });
            return profile;
        }
    }

    public LaunchProfile BindVersion(string id, string? payloadId)
    {
        if (!ValidId(id) || (payloadId is not null && !PayloadStore.IsPayloadId(payloadId)))
            throw new ArgumentException("Invalid launch profile or game version binding.");
        lock (sync)
        {
            var config = Read();
            var index = Array.FindIndex(config.Profiles, profile => profile.Id == id);
            if (index < 0) throw new InvalidDataException("Launch profile not found.");
            var updated = config.Profiles[index] with { PayloadId = payloadId };
            config.Profiles[index] = updated;
            Write(config);
            return updated;
        }
    }

    public LaunchProfilePaths EnsureDirectories(LaunchProfile profile)
    {
        if (profile.Id != Default.Id && !ValidId(profile.Id))
            throw new InvalidDataException("Invalid launch profile identity.");
        var prefix = $"LaunchProfiles/{profile.Id}";
        var mods = Payload.SafePath(data, profile.IsolateMods ? prefix + "/mods" : "mods");
        var relative = profile.IsolateSaves ? prefix + "/default/1" : null;
        var saves = relative is null ? null : Payload.SafePath(data, relative);
        Directory.CreateDirectory(mods);
        SaveSnapshots.RecoverInterruptedRestore(saves ?? SnapshotSaveDirectory(profile));
        if (saves is not null) Directory.CreateDirectory(saves);
        return new(mods, saves, relative);
    }

    public string SnapshotSaveDirectory(LaunchProfile profile)
    {
        if (profile.Id != Default.Id && !ValidId(profile.Id))
            throw new InvalidDataException("Invalid launch profile identity.");
        return Payload.SafePath(data, profile.IsolateSaves ? $"LaunchProfiles/{profile.Id}/default/1" : "default/1");
    }

    Configuration Read()
    {
        if (!File.Exists(settings)) return new(Default.Id, []);
        using var stream = File.OpenRead(settings);
        var config = JsonSerializer.Deserialize<Configuration>(stream) ?? throw new InvalidDataException("Invalid launch profiles.");
        if (config.Profiles is null || config.Profiles.Any(profile => profile is null ||
                !ValidId(profile.Id) || !ValidName(profile.Name) ||
                (profile.PayloadId is not null && !PayloadStore.IsPayloadId(profile.PayloadId))) ||
            config.Profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != config.Profiles.Length ||
            config.Profiles.Select(profile => profile.Name).Append(Default.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Profiles.Length + 1 ||
            (config.Active != Default.Id && !config.Profiles.Any(profile => profile.Id == config.Active)))
            throw new InvalidDataException("Invalid launch profile configuration.");
        return config;
    }

    void Write(Configuration config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        var temporary = settings + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, config);
                stream.Flush(true);
            }
            File.Move(temporary, settings, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    static bool ValidName(string? name) => name is { Length: >= 1 and <= 64 } && name == name.Trim() && !name.Any(char.IsControl);
    static bool ValidId(string? id) => Guid.TryParseExact(id, "N", out var guid) && id == guid.ToString("N");
}
