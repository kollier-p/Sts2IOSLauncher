using System.Diagnostics;
using System.Reflection;

namespace Launcher.Host;

internal static class GameViewDiagnostics
{
    const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    static Func<ulong>? renderedFrames;

    public readonly record struct Observation(string Operation, long Timestamp, ulong? Frame);

    internal static void Install(Assembly game, Assembly godot, Action<MethodInfo, MethodInfo, string> patch)
    {
        var counter = godot.GetType("Godot.Engine")?.GetMethod("GetFramesDrawn", Type.EmptyTypes);
        if (counter?.ReturnType == typeof(ulong)) renderedFrames = counter.CreateDelegate<Func<ulong>>();

        const string nodes = "MegaCrit.Sts2.Core.Nodes.";
        foreach (var (type, name, async) in new[]
        {
            ("NTransition", "FadeOut", true),
            ("NTransition", "FadeIn", true),
            ("NSceneContainer", "SetCurrentScene", false),
            ("Screens.NDeckViewScreen", "ShowScreen", false),
            ("Screens.NDeckViewScreen", "_Ready", false)
        })
        {
            try
            {
                var targets = game.GetType(nodes + type)?.GetMethods(Methods).Where(method => method.Name == name &&
                    !method.ContainsGenericParameters && (!async || method.ReturnType == typeof(Task))).ToArray();
                if (targets is not [var target]) continue;
                patch(target, typeof(GameViewDiagnostics).GetMethod(nameof(Began))!, "prefix");
                patch(target, typeof(GameViewDiagnostics).GetMethod(async ? nameof(ObserveTask) : nameof(Finished))!,
                    async ? "postfix" : "finalizer");
            }
            catch (Exception error) { HostDiagnostics.Error($"trace game {type}.{name}", error); }
        }
    }

    public static void Began(MethodBase __originalMethod, object[] __args, out Observation __state)
    {
        var operation = $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}";
        if (__originalMethod.Name == "SetCurrentScene" && __args.FirstOrDefault() is { } scene)
            operation += $" → {scene.GetType().Name}";
        __state = new(operation, Stopwatch.GetTimestamp(), CurrentFrame());
        GameMemory.Log($"{operation} started");
    }

    public static void Finished(Observation __state, Exception? __exception)
    {
        if (__state.Timestamp == 0) return;
        var elapsed = Stopwatch.GetElapsedTime(__state.Timestamp).TotalMilliseconds;
        var frameCount = __state.Frame is ulong before && CurrentFrame() is ulong after && after >= before
            ? $", {after - before} drawn frames"
            : "";
        GameMemory.Log($"{__state.Operation} {(__exception is null ? "finished" : "failed")} in {elapsed:F0} ms{frameCount}");
        if (__exception is not null) HostDiagnostics.Error(__state.Operation, __exception);
    }

    public static void ObserveTask(Task __result, Observation __state) => _ = TrackTaskAsync(__result, __state);

    static async Task TrackTaskAsync(Task task, Observation observation)
    {
        try { await task; }
        catch (Exception error) { Finished(observation, error); return; }
        Finished(observation, null);
    }

    static ulong? CurrentFrame()
    {
        try { return renderedFrames?.Invoke(); }
        catch (Exception error)
        {
            renderedFrames = null;
            HostDiagnostics.Error("read Godot draw frame counter", error);
            return null;
        }
    }

    internal static void End() => renderedFrames = null;
}
