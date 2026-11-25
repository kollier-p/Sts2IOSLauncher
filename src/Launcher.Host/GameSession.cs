using System.Reflection;
using System.Runtime.InteropServices;

namespace Launcher.Host;

internal static class GameSession
{
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    static object? tree;
    static PropertyInfo? paused;
    static bool? previousPause;
    static Assembly? godotAssembly, gameAssembly;
    static float? previousVolume;

    internal static void Attach(object menu, Assembly godot, Assembly? game)
    {
        tree = godot.GetType("Godot.Node", true)!.GetMethod("GetTree", Type.EmptyTypes)!.Invoke(menu, null);
        paused = godot.GetType("Godot.SceneTree", true)!.GetProperty("Paused")!;
        godotAssembly = godot;
        gameAssembly = game;
    }

    public static bool SceneQuitPrefix(int __0) => __0 != 0 || !TryReturn();

    public static void GameQuitPostfix() => TryReturn();

    public static void AsyncGameQuitPostfix(Task __result) => _ = ReturnAfterQuitAsync(__result);

    internal static void SetParked(bool parked)
    {
        try
        {
            if (parked && previousPause is null && paused?.GetValue(tree) is bool wasPaused)
            {
                previousPause = wasPaused;
                paused.SetValue(tree, true);
            }
            else if (!parked && previousPause is { } restore)
            {
                paused?.SetValue(tree, restore);
                previousPause = null;
            }
        }
        catch (Exception error) { HostDiagnostics.Error("pause game scene", error); }

        try
        {
            if (parked)
            {
                var saves = gameAssembly?.GetType("MegaCrit.Sts2.Core.Saves.SaveManager")?
                    .GetProperty("Instance", Members)?.GetValue(null);
                var settings = saves?.GetType().GetProperty("SettingsSave", Members)?.GetValue(saves);
                if (settings?.GetType().GetProperty("VolumeMaster", Members)?.GetValue(settings) is not float volume) return;
                previousVolume = volume;
                SetMasterVolume(0);
            }
            else if (previousVolume is { } volume)
            {
                previousVolume = null;
                SetMasterVolume(volume);
            }
        }
        catch (Exception error) { HostDiagnostics.Error("change game audio for launcher", error); }
    }

    static void SetMasterVolume(float volume)
    {
        var game = gameAssembly?.GetType("MegaCrit.Sts2.Core.Nodes.NGame")?
            .GetProperty("Instance", Members)?.GetValue(null);
        if (game is null || godotAssembly is not { } godot) return;
        var type = game.GetType();
        var audio = type.GetProperty("AudioManager", Members)?.GetValue(game);
        var debug = type.GetProperty("DebugAudio", Members)?.GetValue(game);
        audio?.GetType().GetMethod("SetMasterVol", Members, [typeof(float)])?.Invoke(audio, [volume]);
        debug?.GetType().GetMethod("SetMasterAudioVolume", Members, [typeof(float)])?.Invoke(debug, [volume]);

        // FMOD defers mixer changes until its next update. The render loop is parked.
        var name = godot.GetType("Godot.StringName", throwOnError: true)!;
        var engine = godot.GetType("Godot.Engine", throwOnError: true)!;
        using var fmod = (IDisposable)Activator.CreateInstance(name, "FmodServer")!;
        if (engine.GetMethod("HasSingleton", [name])!.Invoke(null, [fmod]) is not true) return;
        var server = engine.GetMethod("GetSingleton", [name])!.Invoke(null, [fmod]);
        if (server is null) return;
        using var update = (IDisposable)Activator.CreateInstance(name, "update")!;
        var godotObject = godot.GetType("Godot.GodotObject", throwOnError: true)!;
        if (godotObject.GetMethod("HasMethod", [name])!.Invoke(server, [update]) is not true) return;
        var variant = godot.GetType("Godot.Variant", throwOnError: true)!;
        using var result = (IDisposable?)godotObject.GetMethod("Call", [name, variant.MakeArrayType()])!
            .Invoke(server, [update, Array.CreateInstance(variant, 0)]);
    }

    static async Task ReturnAfterQuitAsync(Task quit)
    {
        try
        {
            await quit;
            TryReturn();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { HostDiagnostics.Error("complete game quit", error); }
    }

    static bool TryReturn()
    {
        try { return ReturnToLauncher() == 1; }
        catch (Exception error) { HostDiagnostics.Error("show launcher after game quit", error); return false; }
    }

    [DllImport("__Internal", EntryPoint = "sts2_return_to_launcher")]
    static extern int ReturnToLauncher();

    [DllImport("__Internal", EntryPoint = "sts2_game_keyboard_ready")]
    internal static extern void KeyboardReady();
}
