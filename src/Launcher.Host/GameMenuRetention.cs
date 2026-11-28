using System.Reflection;

namespace Launcher.Host;

// Device traces show the first frame of a rebuilt main menu stalling for ~2 s
// while the process briefly uses ~1 GiB outside Godot's counters. The game drops
// the menu scene from its asset cache during a run, so the return rebuilds every
// shader and resource while the run is still resident. Keeping the menu's scene
// and resources referenced lets the return reuse them from Godot's cache.
internal static class GameMenuRetention
{
    static readonly string[] ResourceProperties =
        ["Material", "ProcessMaterial", "MaterialOverride", "Texture", "Mesh", "SpriteFrames", "Shader", "Theme"];
    const int NodeLimit = 20000;

    static readonly List<object> retained = [];
    static readonly HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<Type, PropertyInfo[]> propertiesByType = [];

    internal static void Retain(object menu, Assembly godot)
    {
        var node = godot.GetType("Godot.Node", throwOnError: true)!;
        var resource = godot.GetType("Godot.Resource", throwOnError: true)!;
        var getChildren = node.GetMethod("GetChildren", [typeof(bool)])!;
        int before = retained.Count, nodes = 0;

        if (node.GetProperty("SceneFilePath")?.GetValue(menu) is string { Length: > 0 } path)
        {
            var loader = godot.GetType("Godot.ResourceLoader", throwOnError: true)!;
            var load = loader.GetMethods().Single(method => method.Name == "Load" && !method.IsGenericMethod &&
                method.GetParameters().Length == 3);
            var reuse = Enum.Parse(load.GetParameters()[2].ParameterType, "Reuse");
            Keep(load.Invoke(null, [path, "", reuse]));
        }

        var pending = new Stack<object>();
        pending.Push(menu);
        while (pending.Count > 0 && nodes < NodeLimit)
        {
            var current = pending.Pop();
            nodes++;
            foreach (var property in Properties(current.GetType(), resource))
            {
                object? value;
                try { value = property.GetValue(current); }
                catch (TargetInvocationException) { continue; }
                if (Keep(value) && value is not null)
                    foreach (var nested in Properties(value.GetType(), resource))
                    {
                        try { Keep(nested.GetValue(value)); }
                        catch (TargetInvocationException) { }
                    }
            }
            if (getChildren.Invoke(current, [true]) is System.Collections.IEnumerable children)
                foreach (var child in children)
                    if (child is not null) pending.Push(child);
        }
        GameMemory.Log($"kept {retained.Count - before} main menu resources from {nodes} nodes for the return from runs " +
            $"({retained.Count} total)");
        Describe(godot);
    }

    // Name the menu's resource types and its large textures, so device logs
    // show what the menu holds when its first frame stalls.
    static void Describe(Assembly godot)
    {
        var texture = godot.GetType("Godot.Texture2D");
        var width = texture?.GetMethod("GetWidth", Type.EmptyTypes);
        var height = texture?.GetMethod("GetHeight", Type.EmptyTypes);
        var path = godot.GetType("Godot.Resource")?.GetProperty("ResourcePath");
        var types = retained.GroupBy(value => value.GetType().Name).OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} {group.Count()}");
        HostDiagnostics.Stage($"main menu resource types: {string.Join(", ", types)}");
        if (texture is null || width is null || height is null) return;
        var large = new List<string>();
        foreach (var value in retained)
        {
            if (!texture.IsInstanceOfType(value)) continue;
            try
            {
                int w = (int)width.Invoke(value, null)!, h = (int)height.Invoke(value, null)!;
                if (Math.Max(w, h) < 1024) continue;
                var name = path?.GetValue(value) as string;
                large.Add($"{value.GetType().Name} {w}x{h} {(string.IsNullOrEmpty(name) ? "(generated)" : name)}");
            }
            catch (TargetInvocationException) { }
        }
        if (large.Count > 0) HostDiagnostics.Stage($"main menu large textures: {string.Join("; ", large.Take(40))}");
    }

    internal static void Release()
    {
        retained.Clear();
        seen.Clear();
        propertiesByType.Clear();
    }

    static bool Keep(object? value)
    {
        if (value is null || !seen.Add(value)) return false;
        retained.Add(value);
        return true;
    }

    static PropertyInfo[] Properties(Type type, Type resource)
    {
        if (propertiesByType.TryGetValue(type, out var cached)) return cached;
        var found = ResourceProperties
            .Select(name => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property is { CanRead: true } && property.GetIndexParameters().Length == 0 &&
                resource.IsAssignableFrom(property.PropertyType))
            .Select(property => property!)
            .ToArray();
        propertiesByType[type] = found;
        return found;
    }
}
