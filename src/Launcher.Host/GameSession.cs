using System.Reflection;
using System.Runtime.InteropServices;

namespace Launcher.Host;

internal static class GameSession
{
    static WeakReference<object>? mainMenu;
    static MethodInfo? isValid, isMenuVisible;
    static object? sceneTree;
    static EventInfo? processFrame;
    static Func<ulong?> availableMemory = GameMemory.AvailableBytes;
    static bool collectedBeforeMenu;
    static int returnsInProgress;
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
        sceneTree = node.GetMethod("GetTree", Type.EmptyTypes)?.Invoke(menu, null);
        processFrame = godot.GetType("Godot.SceneTree")?.GetEvent("ProcessFrame");
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
        if (Interlocked.Increment(ref returnsInProgress) == 1) collectedBeforeMenu = false;
        GameMemory.Log("returning from the run to the game main menu");
        GameRenderWarmup.ReleaseRetainedAssets();
        if (OperatingSystem.IsIOS())
            _ = LogMenuReturnMemoryAsync(Interlocked.Increment(ref menuReturnGeneration));
    }

    public static void LoadMainMenuPrefix()
    {
        if (!collectedBeforeMenu && availableMemory() is ulong bytes && bytes < GameMemory.MenuHeadroom)
        {
            // The run teardown and the game's cache eviction have run by this
            // point. Collect unused Godot resource wrappers without waiting
            // for finalizers on the game thread.
            GC.Collect();
            GameMemory.Log("collected unused managed objects before loading the game main menu");
        }
        GameMemory.Log("game loading its main menu scene");
    }

    public static void MainMenuPreloadPostfix(ref Task __result)
    {
        if (Volatile.Read(ref returnsInProgress) > 0 && sceneTree is { } tree && processFrame is { } frame)
            __result = AllowResourceRetirementAsync(__result, tree, frame);
    }

    static async Task AllowResourceRetirementAsync(Task preload, object tree, EventInfo frame)
    {
        await preload;
        if (availableMemory() is not ulong bytes || bytes >= GameMemory.MenuHeadroom) return;

        // Resource frees are queued by the old scene and the asset cache. Let
        // finalizers and the renderer process those frees before instantiating
        // the next scene on top of the old frame's allocations.
        GameMemory.Log("waiting for queued resource frees before loading the game main menu");
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int remainingFrames = 3;
        Action onFrame = () =>
        {
            remainingFrames--;
            if (remainingFrames == 2)
            {
                GC.Collect();
                collectedBeforeMenu = true;
            }
            if (remainingFrames == 0) rendered.TrySetResult();
        };
        Delegate? callback = null;
        try
        {
            var delegateHandler = Delegate.CreateDelegate(frame.EventHandlerType!, onFrame.Target, onFrame.Method);
            frame.AddEventHandler(tree, delegateHandler);
            callback = delegateHandler;
            var completed = await Task.WhenAny(rendered.Task, Task.Delay(750));
            GameMemory.Log(completed == rendered.Task
                ? "processed three frames before loading the game main menu"
                : "menu resource retirement reached its time limit");
        }
        catch (Exception error) { HostDiagnostics.Error("wait for game menu resource retirement", error); }
        finally
        {
            try { if (callback is not null) frame.RemoveEventHandler(tree, callback); }
            catch (Exception error) { HostDiagnostics.Error("detach game menu resource retirement", error); }
        }
    }

    public static void ReturnToMainMenuPostfix(Task __result) => _ = ObserveMenuReturnAsync(__result);

    internal static void End()
    {
        sceneTree = null;
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
        finally { Interlocked.Decrement(ref returnsInProgress); }
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
