using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Security.Cryptography;
namespace Launcher.Adapters;
// Adapted from the reference project's MIT-licensed low-level runtime rewriter.
// Retains strict input checksum and version checks.
public static class Rewriter {
public static void Prepare(string mode,string inputPath,string supportPath,string outputPath) {
string[] args=[mode,inputPath,supportPath,outputPath];
if (Path.GetFullPath(args[1]) == Path.GetFullPath(args[3]))
    throw new ArgumentException("The original assembly must remain unchanged.");
string expectedHash = args[0] switch
{
    "harmony" => "ef1898322c9f5c86dc1b0758b272a9c440823b4a41ca9a0b82a3aa6b3d206387",
    "corelib" => "2c04207e84e2b4fff5407d1687e1c85e56db71ad540a6aead1baf00b4a691089",
    "steamkit" => "0339e8490d46b0d121349d80d5c423abf4e0293b5ef1197f3b77c00cedf72785",
    _ => throw new ArgumentException("Unknown assembly adapter.")
};
using (var input = File.OpenRead(args[1]))
    if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        throw new NotSupportedException("Assembly checksum differs from the tested input; review the adapter before updating it.");

using var assembly = AssemblyDefinition.ReadAssembly(args[1]);
var module = assembly.MainModule;
MethodDefinition Method(string type, string name) => module.GetType(type).Methods.Single(m => m.Name == name);
ILProcessor Replace(MethodDefinition method)
{
    method.Body = new MethodBody(method);
    return method.Body.GetILProcessor();
}

if (args[0] == "corelib")
{
    var il = Replace(Method("System.Runtime.CompilerServices.RuntimeFeature", "get_IsDynamicCodeCompiled"));
    il.Emit(OpCodes.Call, Method("System.Runtime.CompilerServices.RuntimeFeature", "get_IsDynamicCodeSupported"));
    il.Emit(OpCodes.Ret);
}
else if (args[0] == "harmony")
{
    if (assembly.Name.Version != new Version(2, 4, 2, 0))
        throw new NotSupportedException("The iOS backend targets Harmony 2.4.2.0.");
    const string system = "MonoMod.Core.Platforms.Systems.MacOSSystem";
    var arm64 = (int)module.GetType("MonoMod.Utils.ArchitectureKind").Fields.Single(f => f.Name == "Arm64").Constant;
    var archIl = Replace(Method("MonoMod.Utils.PlatformDetection", "get_Architecture"));
    archIl.Emit(OpCodes.Ldc_I4, arm64);
    archIl.Emit(OpCodes.Ret);
    // Reuse Darwin's ARM64 ABI and region queries; executable writes stay in native code.
    var il = Replace(Method("MonoMod.Core.Platforms.PlatformTriple", "CreateCurrentSystem"));
    il.Emit(OpCodes.Newobj, Method(system, ".ctor"));
    il.Emit(OpCodes.Ret);
    il = Replace(Method(system, "get_NativeExceptionHelper"));
    il.Emit(OpCodes.Ldnull);
    il.Emit(OpCodes.Ret);
    using var support = AssemblyDefinition.ReadAssembly(args[2]);
    var patch = support.MainModule.GetType("STS2JitSupport.NativePatch").Methods.Single(m => m.Name == "PatchData");
    il = Replace(Method(system, "PatchData"));
    for (int i = 1; i <= 4; i++) il.Emit(OpCodes.Ldarg, i);
    il.Emit(OpCodes.Call, module.ImportReference(patch));
    il.Emit(OpCodes.Ret);

    var fix = support.MainModule.GetType("STS2JitSupport.SignatureFix").Methods.Single(m => m.Name == "PreserveReturnModifiers");
    var importer = module.GetType("MonoMod.Utils.MMReflectionImporter").Methods.Single(m =>
        m.Name == "_ImportReference" && m.Parameters[0].ParameterType.FullName == "System.Reflection.MethodBase");
    var setter = importer.Body.Instructions.Single(i => i.Operand is MethodReference called && called.Name == "set_ReturnType");
    var processor = importer.Body.GetILProcessor();
    var cursor = setter;
    void Append(Instruction instruction) { processor.InsertAfter(cursor, instruction); cursor = instruction; }
    Append(Instruction.Create(OpCodes.Ldarg_0));
    Append(Instruction.Create(OpCodes.Ldarg_1));
    Append(Instruction.Create(OpCodes.Ldloc, importer.Body.Variables.First(v => v.VariableType.FullName == "Mono.Cecil.MethodReference")));
    Append(Instruction.Create(OpCodes.Call, module.ImportReference(fix)));
}
else if (args[0] == "steamkit")
{
    ReplaceSteamKitJobTimestamp(module);
}
else throw new ArgumentException("Unknown assembly adapter.");

WriteAssembly(assembly, args[3]);
Console.WriteLine($"Prepared {args[0]} for the iOS JIT runtime.");
}

    public static bool PrepareGodotDiagnostics(string input, string output)
    {
        if (Path.GetFullPath(input) == Path.GetFullPath(output))
            throw new ArgumentException("The original assembly must remain unchanged.");
        using var assembly = AssemblyDefinition.ReadAssembly(input);
        if (assembly.Name.Name != "GodotSharp") return false;
        var formatter = assembly.MainModule.GetType("Godot.DebuggingUtils")?.Methods.FirstOrDefault(method =>
            method.Name == "GetStackFrameMethodDecl" && method.IsStatic && method.HasBody &&
            method.ReturnType.MetadataType == MetadataType.Void && method.Parameters.Count == 2 &&
            method.Parameters[0].ParameterType.FullName == "System.Diagnostics.StackFrame" &&
            method.Parameters[1].ParameterType.FullName == "System.String&");
        if (formatter is null) return false;
        var calls = formatter.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Callvirt &&
            instruction.Operand is MethodReference method && method.DeclaringType.FullName == "System.Reflection.MethodBase" &&
            method.Name == "GetParameters" && method.Parameters.Count == 0 &&
            method.ReturnType.FullName == "System.Reflection.ParameterInfo[]").ToArray();
        if (calls.Length != 1) return false;

        // Warning backtraces can walk Harmony-generated frames whose parameter
        // metadata aborts the iOS Mono runtime. Keep frame names and source lines.
        var call = calls[0];
        var parameterType = ((ArrayType)((MethodReference)call.Operand).ReturnType).ElementType;
        var il = formatter.Body.GetILProcessor();
        call.OpCode = OpCodes.Pop;
        call.Operand = null;
        var length = Instruction.Create(OpCodes.Ldc_I4_0);
        il.InsertAfter(call, length);
        il.InsertAfter(length, Instruction.Create(OpCodes.Newarr, parameterType));
        WriteAssembly(assembly, output);
        return true;
    }

    public static bool PrepareGameAssetLoading(string input, string output)
    {
        if (Path.GetFullPath(input) == Path.GetFullPath(output))
            throw new ArgumentException("The original assembly must remain unchanged.");
        using var assembly = AssemblyDefinition.ReadAssembly(input);
        if (assembly.Name.HasPublicKey) return false;
        var queue = assembly.MainModule.GetType("MegaCrit.Sts2.Core.Assets.AssetLoadingSession")?.Methods
            .SingleOrDefault(method => method.Name == "ProcessLoadingQueue" && !method.IsStatic && method.HasBody &&
                method.Parameters.Count == 0 && method.ReturnType.MetadataType == MetadataType.Void);
        if (queue is null || !queue.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference called && called.Name == "LoadThreadedRequest" &&
            called.DeclaringType.FullName == "Godot.ResourceLoader"))
            return false;

        // The desktop queue can retain decoded images faster than the renderer
        // uploads them. Bound the known capacity check while keeping all assets,
        // retries and load completion under the game's control.
        var limits = queue.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldc_I4 &&
            instruction.Operand is 128 &&
            instruction.Previous?.Operand is MethodReference count && count.Name == "get_Count" &&
            count.ReturnType.MetadataType == MetadataType.Int32 &&
            instruction.Next?.OpCode.Code is Code.Bge or Code.Bge_S or Code.Blt or Code.Blt_S or Code.Clt)
            .ToArray();
        if (limits is not [var limit]) return false;
        limit.OpCode = OpCodes.Ldc_I4_6;
        limit.Operand = null;
        WriteAssembly(assembly, output);
        return true;
    }

    static void WriteAssembly(AssemblyDefinition assembly, string output)
    {
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            assembly.Write(temporary);
            File.Move(temporary, output, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static void ReplaceSteamKitJobTimestamp(ModuleDefinition module)
    {
        var client = module.GetType("SteamKit2.SteamClient");
        var constructor = client.Methods.Single(method => method.IsConstructor && method.Parameters.Count == 2);
        var timestamp = client.Fields.Single(field => field.Name == "processStartTime");
        var body = constructor.Body;
        var getProcess = body.Instructions.Single(instruction => instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == "System.Diagnostics.Process" && method.Name == "GetCurrentProcess");
        var afterProcess = body.ExceptionHandlers.Single().HandlerEnd;
        var utcNow = new MethodReference("get_UtcNow", timestamp.FieldType, timestamp.FieldType);

        // The original using block reads Process.StartTime, which throws on iOS.
        // SteamKit only needs a timestamp that stays constant for the client's job IDs.
        var il = body.GetILProcessor();
        il.InsertBefore(getProcess, Instruction.Create(OpCodes.Ldarg_0));
        il.InsertBefore(getProcess, Instruction.Create(OpCodes.Call, utcNow));
        il.InsertBefore(getProcess, Instruction.Create(OpCodes.Stfld, timestamp));
        body.ExceptionHandlers.Clear();
        body.Variables.Clear();
        for (var instruction = getProcess; instruction != afterProcess;)
        {
            var next = instruction.Next;
            il.Remove(instruction);
            instruction = next;
        }
    }
}
