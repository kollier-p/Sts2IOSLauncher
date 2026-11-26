using System.Reflection;
using System.Runtime.InteropServices;

namespace Launcher.Host;

internal static class GameSession
{
    static WeakReference<object>? mainMenu;
    static MethodInfo? isValid, isMenuVisible;
    static int menuReturnGeneration;

    internal static void ObserveMainMenu(object menu, Assembly godot)
    {
        var node = godot.GetType("Godot.Node", throwOnError: true)!;
        var canvas = godot.GetType("Godot.CanvasItem", throwOnError: true)!;
        var godotObject = godot.GetType("Godot.GodotObject", throwOnError: true)!;
        isValid = godotObject.GetMethod("IsInstanceValid", [godotObject])!;
        isMenuVisible = canvas.IsInstanceOfType(menu)
            ? canvas.GetMethod("IsVisibleInTree", Type.EmptyTypes)!
            : node.GetMethod("IsInsideTree", Type.EmptyTypes)!;
        mainMenu = new(menu);
        GameMemory.Log("game main menu ready");
    }

    static bool MainMenuVisible()
    {
        try
        {
            return mainMenu?.TryGetTarget(out var menu) == true &&
                isValid?.Invoke(null, [menu]) is true && isMenuVisible?.Invoke(menu, null) is true;
        }
        catch (Exception error) { HostDiagnostics.Error("check game main menu for quit", error); return false; }
    }

    public static bool SceneQuitPrefix(int __0) => __0 != 0 || !MainMenuVisible() || !TryReturn();

    public static void GameQuitPrefix(out bool __state)
    {
        __state = MainMenuVisible();
        HostDiagnostics.Stage(__state ? "game quit requested from main menu" : "game quit requested outside main menu");
    }

    public static void GameQuitPostfix(bool __state)
    {
        if (__state) TryReturn();
    }

    public static void AsyncGameQuitPostfix(Task __result, bool __state)
    {
        if (__state) _ = ReturnAfterQuitAsync(__result);
    }

    public static void ReturnToMainMenuPrefix()
    {
        GameMemory.Log("returning from the run to the game main menu");
        GameRenderWarmup.ReleaseRetainedAssets();
        if (OperatingSystem.IsIOS())
            _ = LogMenuReturnMemoryAsync(Interlocked.Increment(ref menuReturnGeneration));
    }

    public static void LoadMainMenuPrefix() => GameMemory.Log("game loading its main menu scene");

    public static void ReturnToMainMenuPostfix(Task __result) => _ = ObserveMenuReturnAsync(__result);

    internal static void End()
    {
        GameViewDiagnostics.End();
        Interlocked.Increment(ref menuReturnGeneration);
    }

    static async Task LogMenuReturnMemoryAsync(int generation)
    {
        int elapsed = 0;
        foreach (int seconds in new[] { 1, 1, 1, 1, 4 })
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            if (Volatile.Read(ref menuReturnGeneration) != generation) return;
            elapsed += seconds;
            GameMemory.Log($"game menu return after {elapsed}s");
        }
    }

    static async Task ObserveMenuReturnAsync(Task transition)
    {
        try
        {
            await transition;
            GameMemory.Log("game finished returning to its main menu");
        }
        catch (OperationCanceledException) { HostDiagnostics.Stage("game main menu return cancelled"); }
        catch (Exception error) { HostDiagnostics.Error("game main menu return failed", error); }
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
