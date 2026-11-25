using Mono.Cecil;

namespace Launcher.Adapters;

public static class GameCompatibilityAudit
{
    public static string? FirstIssue(string assemblyPath, bool checkSavePaths, bool checkModPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        var game = assembly.MainModule;
        var entry = game.GetType("GodotPlugins.Game.Main")?.Methods.Where(method => method.Name == "InitializeFromGameProject").ToArray();
        if (entry is not [var initialize] || !initialize.IsStatic ||
            !initialize.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute") ||
            !Parameters(initialize, "System.IntPtr", "System.IntPtr", "System.IntPtr", "System.Int32") ||
            initialize.ReturnType.FullName is not ("Godot.NativeInterop.godot_bool" or "System.Byte"))
            return "The game has no supported Godot managed entry point.";

        var node = game.GetType("MegaCrit.Sts2.Core.Nodes.NGame");
        if (node is null || node.Methods.Count(method => method.Name == "InitializePlatform" && IsBooleanTask(method.ReturnType)) != 1)
            return "The game has no supported platform startup hook.";

        if (checkSavePaths)
        {
            const string text = "System.String";
            const string number = "System.Int32";
            var paths = game.GetType("MegaCrit.Sts2.Core.Saves.UserDataPathProvider");
            if (paths is null || !paths.Methods.Any(method => method.IsStatic && IsSavePath(method, "GetProfileDir", number)) ||
                !paths.Methods.Any(method => IsSavePath(method, "GetAccountScopedBasePath", text)) ||
                !paths.Methods.Any(method => IsSavePath(method, "GetProfileScopedBasePath", number)) ||
                !paths.Methods.Any(method => IsSavePath(method, "GetProfileScopedPath", number, text)))
                return "This game build lacks the save hooks needed by this isolated launch profile.";
        }

        if (checkModPath)
        {
            var mods = game.GetType("MegaCrit.Sts2.Core.Modding.ModManager");
            if (mods is null || mods.Methods.Count(method => method.Name == "ReadModsInDirRecursive" &&
                method.Parameters.FirstOrDefault()?.ParameterType.FullName == "System.String") != 1)
                return "This game build lacks the mod path hook needed by the current mod settings.";
        }
        return null;
    }

    static bool IsBooleanTask(TypeReference type) => type is GenericInstanceType instance &&
        instance.ElementType.FullName == "System.Threading.Tasks.Task`1" &&
        instance.GenericArguments is [var argument] && argument.FullName == "System.Boolean";

    static bool IsSavePath(MethodDefinition method, string name, params string[] types)
        => method.Name == name && method.ReturnType.FullName == "System.String" && Parameters(method, types);

    static bool Parameters(MethodDefinition method, params string[] types)
        => method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(types);
}
