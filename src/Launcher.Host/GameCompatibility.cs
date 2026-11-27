using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Launcher.Core;
namespace Launcher.Host;

// Android compatibility behaviour, implemented without a compile-time dependency
// on any proprietary game DLL. The game must already be installed and verified.
internal static class GameCompatibility
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly;
    static string releaseInfo="";
    static LaunchProfilePaths? profilePaths;
    static MethodInfo? profileDirectory;
    static object? harmony;
    static Type? harmonyMethod;
    static MethodInfo? patch;
    static MethodInfo? modDirectoryCheck;
    static Type? modSettingsType;
    static bool enableLauncherMods;
    static GameLaunchOptions? pendingLaunchOptions;
    static bool pendingSafeMode;
    static Assembly? pendingGame;
    public static void Apply(Assembly game, LaunchProfile profile, LaunchProfilePaths paths, string release, bool hasEnabledMods)
    {
        profilePaths=paths;releaseInfo=release;
        var library=AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("0Harmony"));
        var harmonyType=library.GetType("HarmonyLib.Harmony",true)!;
        harmonyMethod=library.GetType("HarmonyLib.HarmonyMethod",true)!;
        harmony=Activator.CreateInstance(harmonyType,"org.sts2mobile.ios.compat")!;
        patch=harmonyType.GetMethods().Single(m=>m.Name=="Patch"&&m.GetParameters().Length>=2&&m.GetParameters()[0].ParameterType==typeof(MethodBase));
        // The game is owned and downloaded through Steam. Desktop native Steamworks
        // initialization is not usable on iOS; multiplayer needs its own adapter.
        Require(game,"MegaCrit.Sts2.Core.Nodes.NGame","InitializePlatform",typeof(Task<bool>),nameof(Platform));
        Optional(game,"MegaCrit.Sts2.Core.Debug.OsDebugInfo","LogSystemInfo",typeof(Task),nameof(Complete));
        Optional(game,"MegaCrit.Sts2.Core.Debug.SentryService","Initialize",typeof(void),nameof(Skip));
        Optional(game,"MegaCrit.Sts2.Core.Saves.PrefsSave","get_UploadData",typeof(bool),nameof(False));
        if (profile.IsolateSaves) PatchIsolatedSaves(game);
        enableLauncherMods = hasEnabledMods && !paths.ModsPaused;
        var mods=game.GetType("MegaCrit.Sts2.Core.Modding.ModManager");
        var scanner=mods?.GetMethods(Flags).Where(m=>m.Name=="ReadModsInDirRecursive"&&m.GetParameters().FirstOrDefault()?.ParameterType==typeof(string)).ToArray()??[];
        if(scanner.Length==1) Patch(scanner[0],typeof(GameCompatibility).GetMethod(nameof(ModPath),Flags)!);
        else if (profile.IsolateMods || paths.ModsPaused)
            throw new NotSupportedException("This game build does not expose a compatible mod directory scanner.");
        var initializers = mods?.GetMethods(Flags).Where(method => method.IsStatic && method.Name == "Initialize" &&
            method.GetParameters() is [var io, var settings, ..] && io.ParameterType.Name == "IModManagerFileIo" &&
            settings.ParameterType.Name == "ModSettings").ToArray() ?? [];
        foreach (var init in initializers)
        {
            modSettingsType = init.GetParameters()[1].ParameterType;
            Patch(init, typeof(GameCompatibility).GetMethod(nameof(InitializeMods), Flags)!);
            if (init.ReturnType == typeof(void))
                Patch(init, typeof(GameCompatibility).GetMethod(nameof(ModsInitialized), Flags)!, "postfix");
            else if (init.ReturnType == typeof(Task))
                Patch(init, typeof(GameCompatibility).GetMethod(nameof(ModsInitializedAsync), Flags)!, "postfix");
        }
        if (initializers.Length == 0 && enableLauncherMods)
            HostDiagnostics.Stage("mod initialization hook unavailable; using the game scanner");
        var steamScan=mods?.GetMethods(Flags).Where(m=>m.Name=="ReadSteamMods").ToArray()??[];
        foreach(var method in steamScan)
        {
            if(method.ReturnType==typeof(void))Patch(method,typeof(GameCompatibility).GetMethod(nameof(Skip),Flags)!);
            else if(method.ReturnType==typeof(Task))Patch(method,typeof(GameCompatibility).GetMethod(nameof(Complete),Flags)!);
        }
        var releaseMethods=game.GetType("MegaCrit.Sts2.Core.Debug.ReleaseInfoManager")?.GetMethods(Flags).Where(m=>m.Name=="LoadConfig"&&m.ReturnType.IsClass).ToArray()??[];
        if(releaseMethods.Length==1)Patch(releaseMethods[0],typeof(GameCompatibility).GetMethod(nameof(Release),Flags)!.MakeGenericMethod(releaseMethods[0].ReturnType),"postfix");
    }

    static void PatchUnsupportedMouseCalls(Assembly godot)
    {
        try
        {
            var features = godot.GetType("Godot.DisplayServer+Feature", throwOnError: true)!;
            var hasFeature = godot.GetType("Godot.DisplayServer", throwOnError: true)!.GetMethod("HasFeature", [features])!;
            var input = godot.GetType("Godot.Input", throwOnError: true)!;
            var skip = typeof(GameCompatibility).GetMethod(nameof(Skip), Flags)!;
            foreach (var (feature, method, parameterCount) in new[]
            {
                ("Mouse", "SetMouseMode", 1), ("CursorShape", "SetCustomMouseCursor", 3)
            })
            {
                if (hasFeature.Invoke(null, [Enum.Parse(features, feature)]) is not false) continue;
                var target = input.GetMethods(Flags).SingleOrDefault(candidate => candidate.IsStatic &&
                    candidate.Name == method && candidate.ReturnType == typeof(void) && candidate.GetParameters().Length == parameterCount);
                if (target is null && method == "SetMouseMode") target = input.GetProperty("MouseMode", Flags)?.SetMethod;
                if (target is not null) Patch(target, skip);
            }
        }
        catch (Exception error) { HostDiagnostics.Error("configure iOS pointer compatibility", error); }
    }

    public static void ApplyLaunchOptions(GameLaunchOptions options, Assembly game, bool safeMode = false)
    {
        if (!OperatingSystem.IsIOS() && !options.RenderWarmup && options.FrameLimit is null && options.TouchAsMouse is null)
            return;
        try
        {
            var menu = game.GetType("MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu");
            var ready = menu?.GetMethod("_Ready", Flags, binder: null, types: Type.EmptyTypes, modifiers: null);
            if (ready?.ReturnType != typeof(void))
            {
                HostDiagnostics.Stage("main menu launch options unavailable for this game version");
                return;
            }
            // Godot registers the C# object-binding callbacks after the managed
            // entry point returns. Singleton calls such as DisplayServer.HasFeature
            // must wait until a scene is active.
            Patch(ready, typeof(GameCompatibility).GetMethod(nameof(MainMenuReady), Flags)!, "postfix");
            pendingGame = game;
            pendingLaunchOptions = options;
            pendingSafeMode = safeMode;
            HostDiagnostics.Stage("main menu launch options hook installed");
        }
        catch (Exception error) { HostDiagnostics.Error("install main menu launch options", error); }
    }

    static void MainMenuReady(object __instance)
    {
        var options = pendingLaunchOptions;
        bool ios = OperatingSystem.IsIOS();
        if (options is null && !ios) return;
        pendingLaunchOptions = null;
        var game = pendingGame;
        pendingGame = null;
        var node = __instance.GetType();
        while (node is not null && node.FullName != "Godot.Node") node = node.BaseType;
        if (node is null)
        {
            HostDiagnostics.Stage("main menu launch options need a Godot node");
            return;
        }
        var godot = node.Assembly;
        if (ios)
        {
            try { GameSession.ObserveMainMenu(__instance, godot); }
            catch (Exception error) { HostDiagnostics.Error("observe game main menu", error); }
        }
        if (options is null) return;
        HostDiagnostics.Stage("applying launch options after the game menu opened");
        if (ios)
        {
            ConfigureSessionControls(__instance, godot, game);
            if (game is not null)
            {
                try { GameAssetPreload.Install(game, (load, prefix) => Patch(load, prefix), GameMemory.AvailableBytes); }
                catch (Exception error) { HostDiagnostics.Error("install game menu preload memory guard", error); }
                try { GameViewDiagnostics.Install(game, godot, Patch); }
                catch (Exception error) { HostDiagnostics.Error("install game view diagnostics", error); }
                try
                {
                    GameMemoryTrace.Install(__instance, godot, Patch);
                    GameMemoryTrace.Begin("main menu");
                }
                catch (Exception error) { HostDiagnostics.Error("install game memory trace", error); }
            }
            if (!pendingSafeMode) PatchUnsupportedMouseCalls(godot);
        }
        ApplyGodotOptions(options, godot);
        if (options.RenderWarmup)
        {
            GameRenderWarmup.Extended = options.ExtendedWarmup;
            GameRenderWarmup.MainMenuReady(__instance, godot);
        }
    }

    static void ConfigureSessionControls(object menu, Assembly godot, Assembly? game)
    {
        try
        {
            var tree = godot.GetType("Godot.SceneTree", throwOnError: true)!;
            var quit = tree.GetMethod("Quit", [typeof(int)])!;
            Patch(quit, typeof(GameSession).GetMethod(nameof(GameSession.SceneQuitPrefix))!);
        }
        catch (Exception error) { HostDiagnostics.Error("configure scene quit", error); }

        try
        {
            // Some game versions skip SceneTree.Quit on mobile. Keep their original
            // save/quit code and show the launcher when it has completed.
            var gameType = game?.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
            var gameQuit = gameType?.GetMethod("Quit", Flags,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (gameQuit is not null && (gameQuit.ReturnType == typeof(void) || gameQuit.ReturnType == typeof(Task)))
            {
                Patch(gameQuit, typeof(GameSession).GetMethod(nameof(GameSession.GameQuitPrefix))!);
                var postfix = gameQuit.ReturnType == typeof(void) ? nameof(GameSession.GameQuitPostfix) :
                    nameof(GameSession.AsyncGameQuitPostfix);
                Patch(gameQuit, typeof(GameSession).GetMethod(postfix)!, "postfix");
            }
        }
        catch (Exception error) { HostDiagnostics.Error("configure game quit", error); }

        try
        {
            var methods = game?.GetType("MegaCrit.Sts2.Core.Nodes.NGame")?.GetMethods(Flags);
            var gameReturn = methods?.SingleOrDefault(method => method.Name == "ReturnToMainMenu" &&
                !method.IsStatic && method.ReturnType == typeof(Task));
            if (gameReturn is not null)
            {
                Patch(gameReturn, typeof(GameSession).GetMethod(nameof(GameSession.ReturnToMainMenuPrefix))!);
                Patch(gameReturn, typeof(GameSession).GetMethod(nameof(GameSession.ReturnToMainMenuPostfix))!, "postfix");
            }
            else HostDiagnostics.Stage("game menu return diagnostics unavailable for this version");

            var loadMenu = methods?.SingleOrDefault(method => method.Name == "LoadMainMenu" &&
                !method.IsStatic && method.ReturnType == typeof(Task));
            if (loadMenu is not null)
                Patch(loadMenu, typeof(GameSession).GetMethod(nameof(GameSession.LoadMainMenuPrefix))!);
        }
        catch (Exception error) { HostDiagnostics.Error("configure game menu return diagnostics", error); }

        try
        {
            GameKeyboard.Attach(menu, godot);
            GameSession.KeyboardReady();
        }
        catch (Exception error) { HostDiagnostics.Error("configure game keyboard", error); }
    }

    static void ApplyGodotOptions(GameLaunchOptions options, Assembly godot)
    {
        if (options.FrameLimit is null && options.TouchAsMouse is null) return;
        void Set(string type, string name, object value)
        {
            try
            {
                var property = godot.GetType(type, throwOnError: true)!.GetProperty(name, BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingMemberException(type, name);
                property.SetValue(null, value);
            }
            catch (Exception error) when (error is TypeLoadException or MissingMemberException or TargetInvocationException or ArgumentException)
            {
                HostDiagnostics.Error($"set {type}.{name}", error);
            }
        }
        if (options.FrameLimit is int fps) Set("Godot.Engine", "MaxFps", fps);
        if (options.TouchAsMouse is bool touch) Set("Godot.Input", "EmulateMouseFromTouch", touch);
    }

    static void PatchIsolatedSaves(Assembly game)
    {
        var provider = game.GetType("MegaCrit.Sts2.Core.Saves.UserDataPathProvider");
        MethodInfo? Find(string name, params Type[] parameters)
        {
            var method = provider?.GetMethod(name, Flags, binder: null, types: parameters, modifiers: null);
            return method?.ReturnType == typeof(string) ? method : null;
        }

        profileDirectory = Find("GetProfileDir", typeof(int));
        var account = Find("GetAccountScopedBasePath", typeof(string));
        var slot = Find("GetProfileScopedBasePath", typeof(int));
        var slotData = Find("GetProfileScopedPath", typeof(int), typeof(string));
        if (profileDirectory?.IsStatic != true || account is null || slot is null || slotData is null)
            throw new NotSupportedException("This game build does not expose compatible save path methods.");

        Patch(account, typeof(GameCompatibility).GetMethod(nameof(AccountSavePath), Flags)!);
        Patch(slot, typeof(GameCompatibility).GetMethod(nameof(SlotSavePath), Flags)!);
        Patch(slotData, typeof(GameCompatibility).GetMethod(nameof(SlotDataPath), Flags)!);
    }

    static string GetProfileDirectory(int slot) => (string?)profileDirectory!.Invoke(null, [slot])
        ?? throw new InvalidDataException("The game returned no profile directory.");

    static bool AccountSavePath(string __0, ref string __result)
    {
        __result = profilePaths!.GodotSavePath(__0);
        return false;
    }

    static bool SlotSavePath(int __0, ref string __result)
    {
        __result = profilePaths!.GodotSavePath(GetProfileDirectory(__0));
        return false;
    }

    static bool SlotDataPath(int __0, string __1, ref string __result)
    {
        __result = profilePaths!.GodotSavePath(GetProfileDirectory(__0), __1);
        return false;
    }
    static void Require(Assembly game,string type,string name,Type returns,string hook)
    {
        if(!Optional(game,type,name,returns,hook))throw new NotSupportedException($"Required game compatibility method missing: {type}.{name}");
    }
    static bool Optional(Assembly game,string type,string name,Type returns,string hook)
    {
        var methods=game.GetType(type)?.GetMethods(Flags).Where(m=>m.Name==name&&m.ReturnType==returns).ToArray()??[];
        if(methods.Length!=1)return false;
        Patch(methods[0],typeof(GameCompatibility).GetMethod(hook,Flags)!);
        return true;
    }
    static void Patch(MethodInfo original,MethodInfo hook,string kind="prefix")
    {
        HostDiagnostics.Stage($"install compatibility hook {original.DeclaringType?.Name}.{original.Name}");
        var hm=Activator.CreateInstance(harmonyMethod!,hook);
        var parameters=patch!.GetParameters();
        var args=new object?[parameters.Length];args[0]=original;
        var position=Array.FindIndex(parameters,p=>p.Name==kind);
        if(position<1)throw new MissingMethodException("Harmony patch argument");
        args[position]=hm;patch.Invoke(harmony,args);
    }
    public static bool Platform(ref Task<bool> __result){__result=Task.FromResult(true);return false;}
    public static bool Complete(ref Task __result){__result=Task.CompletedTask;return false;}
    public static bool Skip()=>false;
    public static bool False(ref bool __result){__result=false;return false;}
    static bool IsModRoot(string? path) => !string.IsNullOrEmpty(path) &&
        Path.GetFileName(path.Replace('\\', '/').TrimEnd('/')).Equals("mods", StringComparison.OrdinalIgnoreCase);

    static long modsStarted;

    // Mod assemblies load and apply their Harmony patches here, which takes most
    // of the time before the main menu on the iPad JIT. Report how long it takes.
    public static void ModsInitialized()
    {
        if (modsStarted != 0)
            HostDiagnostics.Stage($"game mod initialization took " +
                $"{System.Diagnostics.Stopwatch.GetElapsedTime(modsStarted).TotalMilliseconds:F0} ms");
    }

    public static void ModsInitializedAsync(Task __result) =>
        _ = __result.ContinueWith(_ => ModsInitialized(), TaskScheduler.Default);

    public static void InitializeMods(object[] __args)
    {
        modsStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var io = __args[0];
            if (io is not null)
            {
                // The desktop directory existence check runs before the recursive scanner.
                // Route both operations to the same profile directory.
                var runtimeType = io.GetType();
                var method = runtimeType.GetMethod("DirectoryExists", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: [typeof(string)], modifiers: null);
                if (method?.ReturnType == typeof(bool))
                {
                    if (method != modDirectoryCheck)
                    {
                        Patch(method, typeof(GameCompatibility).GetMethod(nameof(ModDirectoryExists), Flags)!);
                        modDirectoryCheck = method;
                    }
                }
                else HostDiagnostics.Stage("game mod directory check unavailable");
            }
            if (enableLauncherMods && modSettingsType?.GetProperty("PlayerAgreedToModLoading") is { CanWrite: true } enabled)
            {
                var settings = __args[1] ?? Activator.CreateInstance(modSettingsType);
                enabled.SetValue(settings, true);
                __args[1] = settings!;
                HostDiagnostics.Stage("game mod loading enabled for installed launcher mods");
            }
        }
        catch (Exception error) { HostDiagnostics.Error("configure game mod startup", error); }
    }

    public static bool ModDirectoryExists(string __0, ref bool __result)
    {
        if (!IsModRoot(__0)) return true;
        __result = Directory.Exists(profilePaths!.ModsDirectory);
        return false;
    }

    public static void ModPath(ref string __0)
    {
        if (!IsModRoot(__0)) return;
        __0 = profilePaths!.ModsDirectory;
        HostDiagnostics.Stage(profilePaths.ModsPaused ? "game scanning paused mod directory" : "game scanning launcher mod directory");
    }
    public static void Release<T>(ref T __result) where T:class
    {
        if(__result is not null||!File.Exists(releaseInfo))return;
        try
        {
            using var json=JsonDocument.Parse(File.ReadAllBytes(releaseInfo));
            var result=Activator.CreateInstance<T>();
            var map=new Dictionary<string,string>{{"Commit","commit"},{"Version","version"},{"Branch","branch"},{"Date","date"},{"MainAssemblyHash","main_assembly_hash"}};
            foreach(var (property,key) in map)
            {
                var p=typeof(T).GetProperty(property);
                if(p?.SetMethod is not null&&json.RootElement.TryGetProperty(key,out var value))
                    p.SetValue(result,value.Deserialize(p.PropertyType));
            }
            __result=result;
        }catch(Exception e){Console.Error.WriteLine("[iOS] Release info fallback: "+e.GetType().Name);}
    }
}
