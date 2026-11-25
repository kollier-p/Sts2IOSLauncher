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
    public static void Apply(Assembly game, LaunchProfile profile, LaunchProfilePaths paths, string release)
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
        var mods=game.GetType("MegaCrit.Sts2.Core.Modding.ModManager");
        var scanner=mods?.GetMethods(Flags).Where(m=>m.Name=="ReadModsInDirRecursive"&&m.GetParameters().FirstOrDefault()?.ParameterType==typeof(string)).ToArray()??[];
        if(scanner.Length==1) Patch(scanner[0],typeof(GameCompatibility).GetMethod(nameof(ModPath),Flags)!);
        else if (profile.IsolateMods || paths.ModsPaused)
            throw new NotSupportedException("This game build does not expose a compatible mod directory scanner.");
        var steamScan=mods?.GetMethods(Flags).Where(m=>m.Name=="ReadSteamMods").ToArray()??[];
        foreach(var method in steamScan)
        {
            if(method.ReturnType==typeof(void))Patch(method,typeof(GameCompatibility).GetMethod(nameof(Skip),Flags)!);
            else if(method.ReturnType==typeof(Task))Patch(method,typeof(GameCompatibility).GetMethod(nameof(Complete),Flags)!);
        }
        var releaseMethods=game.GetType("MegaCrit.Sts2.Core.Debug.ReleaseInfoManager")?.GetMethods(Flags).Where(m=>m.Name=="LoadConfig"&&m.ReturnType.IsClass).ToArray()??[];
        if(releaseMethods.Length==1)Patch(releaseMethods[0],typeof(GameCompatibility).GetMethod(nameof(Release),Flags)!.MakeGenericMethod(releaseMethods[0].ReturnType),"postfix");
    }

    public static void ApplyLaunchOptions(GameLaunchOptions options)
    {
        if (options.FrameLimit is null && options.TouchAsMouse is null) return;
        var godot = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == "GodotSharp");
        if (godot is null)
        {
            HostDiagnostics.Error("load Godot launch options", new FileNotFoundException("GodotSharp"));
            return;
        }
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
    public static void ModPath(ref string __0)
    {
        if(string.IsNullOrWhiteSpace(__0))return;
        var full=Path.GetFullPath(__0);
        var desktop=Path.Combine(AppContext.BaseDirectory,"mods");
        if(full==desktop||Path.GetFileName(full)=="mods")__0=profilePaths!.ModsDirectory;
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
