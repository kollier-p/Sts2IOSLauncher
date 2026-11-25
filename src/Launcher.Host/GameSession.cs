using System.Runtime.InteropServices;

namespace Launcher.Host;

internal static class GameSession
{
    public static bool SceneQuitPrefix(int __0) => __0 != 0 || !TryReturn();

    public static void GameQuitPostfix() => TryReturn();

    public static void AsyncGameQuitPostfix(Task __result) => _ = ReturnAfterQuitAsync(__result);

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
