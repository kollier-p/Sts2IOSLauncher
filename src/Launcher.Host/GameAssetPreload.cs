using System.Reflection;

namespace Launcher.Host;

internal static class GameAssetPreload
{
    // Desktop BC textures may expand to RGBA8 before upload on iPad. Keep
    // memory available for the actual menu scene, its dependencies and mods.
    const ulong MenuReserve = 1024 * GameMemory.MiB;
    const BindingFlags StaticMethods = BindingFlags.Public | BindingFlags.NonPublic |
        BindingFlags.Static | BindingFlags.DeclaredOnly;
    static MethodInfo? emptySession;
    static Func<ulong?>? queryHeadroom;
    static int nameIndex;

    internal static void Install(Assembly game, Action<MethodInfo, MethodInfo> patch, Func<ulong?> availableBytes)
    {
        var manager = game.GetType("MegaCrit.Sts2.Core.Assets.PreloadManager");
        var session = game.GetType("MegaCrit.Sts2.Core.Assets.AssetLoadingSession");
        var empty = session?.GetMethod("Empty", StaticMethods, binder: null, types: Type.EmptyTypes, modifiers: null);
        var candidates = manager?.GetMethods(StaticMethods).Where(method =>
            method.Name == "LoadAssets" && method.ReturnType == session &&
            method.GetParameters().Any(parameter => parameter is { Name: "name", ParameterType: var type } &&
                type == typeof(string))).ToArray();
        if (empty is null || empty.ReturnType != session || candidates is not [var load])
        {
            HostDiagnostics.Stage("game menu preload memory guard unavailable for this version");
            return;
        }

        emptySession = empty;
        queryHeadroom = availableBytes;
        nameIndex = Array.FindIndex(load.GetParameters(), parameter => parameter.Name == "name");
        var prefix = typeof(GameAssetPreload).GetMethod(nameof(LoadAssetsPrefix), StaticMethods)!;
        patch(load, prefix.MakeGenericMethod(empty.ReturnType));
    }

    internal static bool IsLowMemoryMenuPreload(string? name, ulong? available) =>
        (name is "Common" or "MainMenu") && available is ulong bytes && bytes < MenuReserve;

    static bool LoadAssetsPrefix<TSession>(object[] __args, ref TSession __result)
    {
        var name = __args[nameIndex] as string;
        if (name is not ("Common" or "MainMenu")) return true;
        var available = queryHeadroom!();
        if (!IsLowMemoryMenuPreload(name, available))
        {
            if (available is ulong bytes)
                HostDiagnostics.Stage($"running game {name} preload (iPad process headroom {bytes / GameMemory.MiB} MiB)");
            return true;
        }

        // Keep LoadAssetSets' eviction and completion flow. The original
        // renderer still loads every dependency needed for scenes on demand.
        try
        {
            if (emptySession!.Invoke(null, null) is not TSession empty) return true;
            __result = empty;
            HostDiagnostics.Stage($"deferred game {name} preload (iPad process headroom {available / GameMemory.MiB} MiB)");
            return false;
        }
        catch (Exception error)
        {
            HostDiagnostics.Error("defer game menu preload", error);
            return true;
        }
    }
}
