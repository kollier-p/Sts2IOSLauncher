namespace Launcher.Core;

public static class SaveFileRules
{
    static readonly HashSet<string> SlotFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "progress.save", "prefs.save", "current_run.save", "current_run.save.backup",
        "current_run_mp.save", "current_run_mp.save.backup"
    };

    public static bool Include(string path, bool includeSettings)
    {
        if (path.Equals("settings.save", StringComparison.OrdinalIgnoreCase)) return includeSettings;
        var parts = path.Split('/');
        if (parts.Any(UnsafePart)) return false;
        var offset = parts[0].Equals("modded", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (parts.Length == offset + 1 && parts[offset].Equals("profile.save", StringComparison.OrdinalIgnoreCase)) return true;
        if (parts.Length < offset + 3 || !IsSlot(parts[offset]) ||
            !parts[offset + 1].Equals("saves", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length == offset + 3) return SlotFiles.Contains(parts[offset + 2]);
        return parts[offset + 2].Equals("history", StringComparison.OrdinalIgnoreCase) &&
               parts[^1].Length > 4 && parts[^1].EndsWith(".run", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Visit(string path)
    {
        var parts = path.Split('/');
        if (parts.Any(UnsafePart)) return false;
        var offset = parts[0].Equals("modded", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (offset == 1 && parts.Length == 1) return true;
        if (!IsSlot(parts[offset])) return false;
        return parts.Length == offset + 1 || (parts[offset + 1].Equals("saves", StringComparison.OrdinalIgnoreCase) &&
            (parts.Length == offset + 2 || parts[offset + 2].Equals("history", StringComparison.OrdinalIgnoreCase)));
    }

    public static IEnumerable<(string Relative, string File)> Enumerate(string root, bool includeSettings, CancellationToken ct)
    {
        if (!Directory.Exists(root)) yield break;
        var folders = new Stack<string>();
        folders.Push("");
        while (folders.TryPop(out var folder))
        {
            var directory = folder.Length == 0 ? root : Payload.SafePath(root, folder);
            foreach (var file in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var relative = folder.Length == 0 ? name : folder + "/" + name;
                if (Visit(relative) && Directory.Exists(file)) folders.Push(relative);
                else if (Include(relative, includeSettings) && File.Exists(file)) yield return (relative, Payload.SafePath(root, relative));
            }
        }
    }

    static bool IsSlot(string value) => value.Length == 8 && value.StartsWith("profile", StringComparison.OrdinalIgnoreCase) && value[^1] is >= '1' and <= '3';
    static bool UnsafePart(string part) => part is "" or "." or ".." || part.Contains('\\') || part.Contains(':') || part.Any(char.IsControl);
}
