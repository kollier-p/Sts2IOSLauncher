using System.Diagnostics;
using System.Reflection;

namespace Launcher.Host;

// Device logs show the process losing ~1 GiB within seconds of the main menu
// appearing, with nothing in the game log. Sample Godot's own counters and
// time resource loads so the next log names what allocates the memory.
internal static class GameMemoryTrace
{
    const double SampleSeconds = 0.25;
    const double WindowSeconds = 12;
    const ulong LoadDeltaBytes = 24 * GameMemory.MiB;
    const double LoadMilliseconds = 200;
    const double StallMilliseconds = 400;

    static readonly (string Label, string Name)[] Monitors =
    [
        ("static", "MemoryStatic"),
        ("video", "RenderVideoMemUsed"),
        ("textures", "RenderTextureMemUsed"),
        ("buffers", "RenderBufferMemUsed"),
        ("objects", "ObjectCount"),
        ("resources", "ObjectResourceCount"),
        ("nodes", "ObjectNodeCount"),
    ];

    static MethodInfo? getMonitor;
    static readonly List<(string Label, object Value)> monitorIds = [];
    static object? tree;
    static EventInfo? processFrame;
    static Delegate? callback;
    static long windowStart, lastSample, lastFrame;
    static string windowReason = "";
    static int threadedRequests;
    static bool inventoried;
    static Assembly? godotAssembly;
    static string? lastThreadedPath;

    public readonly record struct LoadState(long Timestamp, ulong Headroom);

    internal static void Install(object node, Assembly godot, Action<MethodInfo, MethodInfo, string> patch)
    {
        if (callback is not null) return;
        var performance = godot.GetType("Godot.Performance");
        var monitorType = godot.GetType("Godot.Performance+Monitor");
        if (performance is not null && monitorType is not null)
        {
            getMonitor = performance.GetMethod("GetMonitor", [monitorType]);
            foreach (var (label, name) in Monitors)
                if (Enum.TryParse(monitorType, name, out var id)) monitorIds.Add((label, id!));
        }

        godotAssembly = godot;
        var nodeType = godot.GetType("Godot.Node", throwOnError: true)!;
        tree = nodeType.GetMethod("GetTree", Type.EmptyTypes)!.Invoke(node, null);
        processFrame = godot.GetType("Godot.SceneTree", throwOnError: true)!.GetEvent("ProcessFrame");
        if (tree is null || processFrame is null) return;
        callback = Delegate.CreateDelegate(processFrame.EventHandlerType!,
            typeof(GameMemoryTrace).GetMethod(nameof(Frame), BindingFlags.Static | BindingFlags.NonPublic)!);
        processFrame.AddEventHandler(tree, callback);

        var loader = godot.GetType("Godot.ResourceLoader");
        var load = loader?.GetMethods().SingleOrDefault(method => method.Name == "Load" && !method.IsGenericMethod &&
            method.GetParameters() is [{ ParameterType: var path }, _, _] && path == typeof(string));
        if (load is not null)
        {
            patch(load, typeof(GameMemoryTrace).GetMethod(nameof(LoadPrefix))!, "prefix");
            patch(load, typeof(GameMemoryTrace).GetMethod(nameof(LoadPostfix))!, "postfix");
        }
        var request = loader?.GetMethods().SingleOrDefault(method => method.Name == "LoadThreadedRequest" &&
            method.GetParameters() is [{ ParameterType: var path }, ..] && path == typeof(string));
        if (request is not null) patch(request, typeof(GameMemoryTrace).GetMethod(nameof(ThreadedRequestPrefix))!, "prefix");

        // Images decoded or uploaded from code bypass ResourceLoader.Load.
        var image = godot.GetType("Godot.Image");
        var imageTexture = godot.GetType("Godot.ImageTexture");
        var imageCalls = new[]
        {
            image?.GetMethod("LoadFromFile", BindingFlags.Public | BindingFlags.Static, [typeof(string)]),
            image?.GetMethod("Load", BindingFlags.Public | BindingFlags.Instance, [typeof(string)]),
            image is null ? null : imageTexture?.GetMethod("CreateFromImage", BindingFlags.Public | BindingFlags.Static, [image]),
            image?.GetMethod("GenerateMipmaps", BindingFlags.Public | BindingFlags.Instance, [typeof(bool)]),
        };
        foreach (var call in imageCalls)
        {
            if (call is null) continue;
            try
            {
                patch(call, typeof(GameMemoryTrace).GetMethod(nameof(LoadPrefix))!, "prefix");
                patch(call, typeof(GameMemoryTrace).GetMethod(nameof(ImagePostfix))!, "postfix");
            }
            catch (Exception error) { HostDiagnostics.Error($"trace game {call.DeclaringType?.Name}.{call.Name}", error); }
        }
        HostDiagnostics.Stage($"game memory trace installed ({monitorIds.Count} Godot counters)");
        LogShaderCache(godot);
    }

    // Godot keeps compiled shaders in user://shader_cache between launches, and
    // iPadOS caches the Metal pipelines built from them. Report whether the game
    // has the cache enabled and how much it holds.
    static void LogShaderCache(Assembly godot)
    {
        try
        {
            var settings = godot.GetType("Godot.ProjectSettings", throwOnError: true)!;
            var getSetting = settings.GetMethods().First(method => method.Name == "GetSetting" &&
                method.GetParameters() is [{ ParameterType: var name }, ..] && name == typeof(string));
            string Setting(string key)
            {
                var arguments = getSetting.GetParameters().Length == 1 ? new object?[] { key } :
                    new object?[] { key, Activator.CreateInstance(getSetting.GetParameters()[1].ParameterType) };
                return getSetting.Invoke(null, arguments)?.ToString() ?? "unset";
            }
            var globalize = settings.GetMethod("GlobalizePath", [typeof(string)])!;
            var directory = globalize.Invoke(null, ["user://shader_cache"]) as string;
            long bytes = 0;
            int files = 0;
            if (directory is not null && Directory.Exists(directory))
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += new FileInfo(file).Length;
                }
            HostDiagnostics.Stage($"game shader cache {Setting("rendering/shader_compiler/shader_cache/enabled")}, " +
                $"pipeline cache {Setting("rendering/rendering_device/pipeline_cache/enable")}, " +
                $"{files} cached shader files ({bytes / (long)GameMemory.MiB} MiB)");
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("read game shader cache settings",
                error is TargetInvocationException { InnerException: { } cause } ? cause : error);
        }
    }

    internal static void Begin(string reason)
    {
        if (callback is null) return;
        windowReason = reason;
        Volatile.Write(ref windowStart, Stopwatch.GetTimestamp());
        lastSample = 0;
        inventoried = false;
        Interlocked.Exchange(ref threadedRequests, 0);
        Sample("start");
    }

    internal static void End()
    {
        try { if (tree is not null && callback is not null) processFrame?.RemoveEventHandler(tree, callback); }
        catch (Exception error) { HostDiagnostics.Error("remove game memory trace", error); }
        tree = null;
        callback = null;
        windowStart = 0;
    }

    static void Frame()
    {
        long now = Stopwatch.GetTimestamp();
        if (lastFrame != 0)
        {
            var frame = Stopwatch.GetElapsedTime(lastFrame, now).TotalMilliseconds;
            if (frame >= StallMilliseconds) GameMemory.Log($"game frame stalled {frame:F0} ms");
            if (frame >= 1000 && !inventoried && Volatile.Read(ref windowStart) != 0)
            {
                inventoried = true;
                Inventory();
            }
        }
        lastFrame = now;
        long start = Volatile.Read(ref windowStart);
        if (start == 0) return;
        var elapsed = Stopwatch.GetElapsedTime(start, now).TotalSeconds;
        if (elapsed > WindowSeconds)
        {
            Sample("end");
            windowStart = 0;
            return;
        }
        if (lastSample != 0 && Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds < SampleSeconds) return;
        Sample($"{elapsed:F2}s");
    }

    // Count the node classes in the whole scene tree and describe the node kinds
    // that allocate render targets or GPU buffers.
    static void Inventory()
    {
        try
        {
            var godot = godotAssembly!;
            var node = godot.GetType("Godot.Node", throwOnError: true)!;
            var getClass = godot.GetType("Godot.GodotObject", throwOnError: true)!.GetMethod("GetClass", Type.EmptyTypes)!;
            var getChildren = node.GetMethod("GetChildren", [typeof(bool)])!;
            var root = godot.GetType("Godot.SceneTree", throwOnError: true)!.GetProperty("Root")!.GetValue(tree);
            if (root is null) return;
            var counts = new Dictionary<string, int>();
            var details = new List<string>();
            var pending = new Stack<object>();
            pending.Push(root);
            int total = 0;
            while (pending.Count > 0 && total < 50000)
            {
                var current = pending.Pop();
                total++;
                var name = getClass.Invoke(current, null) as string ?? current.GetType().Name;
                counts[name] = counts.GetValueOrDefault(name) + 1;
                // Render targets and Spine skeletons first; idle particles last.
                if (name is "SubViewport" or "Window" or "SpineSprite" or "SubViewportContainer" or "Camera3D" or
                    "WorldEnvironment" or "VideoStreamPlayer" or "CanvasGroup" or "BackBufferCopy" or "MeshInstance3D")
                    details.Insert(0, $"{name} {Describe(current)}");
                else if (name is "GPUParticles2D" or "GPUParticles3D" && details.Count < 60)
                    details.Add($"{name} {Describe(current)}");
                if (getChildren.Invoke(current, [true]) is System.Collections.IEnumerable children)
                    foreach (var child in children)
                        if (child is not null) pending.Push(child);
            }
            var top = counts.OrderByDescending(pair => pair.Value).Take(40).Select(pair => $"{pair.Key} {pair.Value}");
            HostDiagnostics.Stage($"scene tree during stall ({total} nodes): {string.Join(", ", top)}");
            if (details.Count > 0) HostDiagnostics.Stage($"scene tree render nodes: {string.Join("; ", details.Take(60))}");
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("inventory game scene tree", error is TargetInvocationException { InnerException: { } cause } ? cause : error);
        }
    }

    static string Describe(object value)
    {
        var parts = new List<string>();
        foreach (var property in new[] { "Name", "Size", "Size2DOverride", "Msaa2D", "Msaa3D", "UseHdr2D",
                     "RenderTargetUpdateMode", "Stretch", "StretchShrink", "Amount", "Emitting", "Visible", "FitMargin",
                     "Stream", "SceneFilePath" })
        {
            try
            {
                if (value.GetType().GetProperty(property)?.GetValue(value) is { } setting) parts.Add($"{property}={setting}");
            }
            catch (TargetInvocationException) { }
        }
        // GDExtension nodes such as SpineSprite expose their settings only through Get.
        foreach (var setting in new[] { "skeleton_data_res", "size" })
        {
            try
            {
                var get = value.GetType().GetMethod("Get", [godotAssembly!.GetType("Godot.StringName")!]);
                var stringName = Activator.CreateInstance(godotAssembly.GetType("Godot.StringName")!, setting);
                var variant = get?.Invoke(value, [stringName]);
                var obj = variant?.GetType().GetProperty("Obj")?.GetValue(variant);
                if (obj is null) continue;
                var path = obj.GetType().GetProperty("ResourcePath")?.GetValue(obj) as string;
                parts.Add($"{setting}={(string.IsNullOrEmpty(path) ? obj : path)}");
            }
            catch (Exception error) when (error is TargetInvocationException or MissingMethodException or ArgumentException) { }
        }
        return $"({string.Join(", ", parts)})";
    }

    static void Sample(string at)
    {
        lastSample = Stopwatch.GetTimestamp();
        try
        {
            var parts = new List<string>();
            if (GameMemory.AvailableBytes() is ulong headroom) parts.Add($"headroom {headroom / GameMemory.MiB} MiB");
            parts.Add($"managed {GC.GetTotalMemory(false) / (long)GameMemory.MiB} MiB");
            foreach (var (label, id) in monitorIds)
            {
                var value = Convert.ToDouble(getMonitor!.Invoke(null, [id]));
                parts.Add(label is "objects" or "resources" or "nodes"
                    ? $"{label} {value:F0}"
                    : $"{label} {value / GameMemory.MiB:F0} MiB");
            }
            int requests = Interlocked.Exchange(ref threadedRequests, 0);
            if (requests > 0) parts.Add($"{requests} threaded loads (last {lastThreadedPath})");
            HostDiagnostics.Stage($"memory trace {windowReason} {at}: {string.Join(", ", parts)}");
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("sample game memory", error is TargetInvocationException { InnerException: { } cause } ? cause : error);
            monitorIds.Clear();
        }
    }

    public static void LoadPrefix(out LoadState __state) =>
        __state = new(Stopwatch.GetTimestamp(), GameMemory.AvailableBytes() ?? 0);

    public static void LoadPostfix(string __0, LoadState __state)
    {
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(__state.Timestamp).TotalMilliseconds;
            ulong after = GameMemory.AvailableBytes() ?? 0;
            ulong used = __state.Headroom > after ? __state.Headroom - after : 0;
            if (used >= LoadDeltaBytes || elapsed >= LoadMilliseconds)
                HostDiagnostics.Stage($"game loaded {__0} in {elapsed:F0} ms using {used / GameMemory.MiB} MiB " +
                    $"(iPad process headroom {after / GameMemory.MiB} MiB)");
        }
        catch (Exception error) { HostDiagnostics.Error("trace game resource load", error); }
    }

    public static void ImagePostfix(MethodBase __originalMethod, object[] __args, LoadState __state)
    {
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(__state.Timestamp).TotalMilliseconds;
            ulong after = GameMemory.AvailableBytes() ?? 0;
            ulong used = __state.Headroom > after ? __state.Headroom - after : 0;
            if (used < LoadDeltaBytes && elapsed < 100) return;
            var subject = __args.FirstOrDefault() is string path ? $" {path}" : "";
            HostDiagnostics.Stage($"game {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}{subject} took " +
                $"{elapsed:F0} ms using {used / GameMemory.MiB} MiB (iPad process headroom {after / GameMemory.MiB} MiB)");
        }
        catch (Exception error) { HostDiagnostics.Error("trace game image call", error); }
    }

    public static void ThreadedRequestPrefix(string __0)
    {
        lastThreadedPath = __0;
        Interlocked.Increment(ref threadedRequests);
    }
}
