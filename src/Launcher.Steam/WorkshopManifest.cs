namespace Launcher.Steam;

public sealed record WorkshopManifest(ulong Id, string Branch, string MinimumBranch, string MaximumBranch, uint Timestamp, string Source)
{
    public static WorkshopManifest Snapshot(ulong id, string minimum, string maximum, uint timestamp, string source)
    {
        minimum = minimum.Trim();
        maximum = maximum.Trim();
        return new(id, minimum.Length != 0 ? minimum : maximum.Length != 0 ? maximum : "public",
            minimum, maximum, timestamp, source);
    }

    public static WorkshopManifest? ResolveBranch(IReadOnlyList<WorkshopManifest> choices, string branch)
    {
        var tagged = choices.Where(item => item.MinimumBranch.Equals(branch, StringComparison.OrdinalIgnoreCase) ||
            item.MaximumBranch.Equals(branch, StringComparison.OrdinalIgnoreCase)).OrderByDescending(item => item.Timestamp).FirstOrDefault();
        return tagged is null ? null : tagged with { Branch = branch };
    }
}
