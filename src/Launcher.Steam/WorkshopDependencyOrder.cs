namespace Launcher.Steam;

public static class WorkshopDependencyOrder
{
    public static async Task<IReadOnlyList<ulong>> MissingAsync(ulong root, IReadOnlySet<ulong> installed,
        Func<ulong, CancellationToken, Task<IReadOnlyList<ulong>>> children, CancellationToken ct)
    {
        var graph = new HashSet<ulong>();
        var visiting = new HashSet<ulong>();
        var ordered = new List<ulong>();

        async Task Visit(ulong id, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 32 || !visiting.Add(id)) throw new InvalidDataException("Workshop dependencies contain a cycle or are too deeply nested.");
            if (graph.Add(id))
            {
                if (graph.Count > 64) throw new InvalidDataException("This Workshop item has too many dependencies for one installation.");
                foreach (var child in await children(id, ct))
                    if (child != 0) await Visit(child, depth + 1);
                if (id != root && !installed.Contains(id)) ordered.Add(id);
            }
            visiting.Remove(id);
        }

        await Visit(root, 0);
        return ordered;
    }
}
