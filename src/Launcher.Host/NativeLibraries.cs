using System.Reflection;
using System.Runtime.InteropServices;
namespace Launcher.Host;
internal static class NativeLibraries
{
    // Only resolve trusted iOS libraries; never load Windows DLLs from Steam.
    static readonly Dictionary<string,string> SystemNames=new(StringComparer.OrdinalIgnoreCase){
        ["libc"]="/usr/lib/libSystem.B.dylib",["libSystem.B.dylib"]="/usr/lib/libSystem.B.dylib",
        ["libdl"]="/usr/lib/libSystem.B.dylib",["libpthread"]="/usr/lib/libSystem.B.dylib",
        ["libm"]="/usr/lib/libSystem.B.dylib",["libz"]="/usr/lib/libz.1.dylib",
        ["libz.dylib"]="/usr/lib/libz.1.dylib"
    };
    static readonly HashSet<string> FmodNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fmod", "fmodstudio", "fmodL", "fmodstudioL", "libfmod", "libfmodstudio"
    };
    static readonly Lazy<nint> Fmod = new(() =>
    {
        var app = Path.GetDirectoryName(typeof(NativeLibraries).Assembly.Location)!;
        var framework = Path.GetFullPath(Path.Combine(app, "..", "Frameworks", "Sts2Fmod.framework", "Sts2Fmod"));
        return NativeLibrary.TryLoad(framework, out var handle) ? handle : 0;
    });
    public static nint Resolve(Assembly assembly,string name){
        if(name=="__Internal")return NativeLibrary.GetMainProgramHandle();
        if(SystemNames.TryGetValue(name,out var system)&&NativeLibrary.TryLoad(system,out var handle))return handle;
        var library = Path.GetFileNameWithoutExtension(name);
        if (FmodNames.Contains(library) && Fmod.Value != 0) return Fmod.Value;
        HostDiagnostics.Stage("native library unresolved: "+Path.GetFileName(name));
        return 0;
    }
}
