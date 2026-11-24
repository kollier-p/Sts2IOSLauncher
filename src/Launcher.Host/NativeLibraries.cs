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
    public static nint Resolve(Assembly assembly,string name){
        if(name=="__Internal")return NativeLibrary.GetMainProgramHandle();
        if(SystemNames.TryGetValue(name,out var system)&&NativeLibrary.TryLoad(system,out var handle))return handle;
        // FMOD is statically linked. Its managed wrappers resolve exported symbols from the host.
        if(name is "fmod" or "fmodstudio" or "fmodL" or "fmodstudioL" or "libfmod" or "libfmodstudio")return NativeLibrary.GetMainProgramHandle();
        HostDiagnostics.Stage("native library unresolved: "+Path.GetFileName(name));
        return 0;
    }
}
