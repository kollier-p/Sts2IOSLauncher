namespace Launcher.Core;

// Platform-neutral contract between the installer and the native game host.
public sealed record LaunchPlan(InstallReceipt Receipt, string Pack, string Assemblies, string GameAssembly, string UserData)
{
    public static LaunchPlan Prepare(PayloadStore store, string userData, CancellationToken ct = default, string? payloadId = null)
    {
        var (receipt, directory) = store.VerifyForLaunch(ct, payloadId);
        var data = Path.GetFullPath(userData);
        var root = Path.GetFullPath(store.Root);
        var relative = Path.GetRelativePath(root, data);
        if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            throw new ArgumentException("Game saves must be outside the versioned payload store.");
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(data);
        return new(receipt, Payload.SafePath(directory, Payload.Required[0]),
            Payload.SafePath(directory, Payload.AssemblyDirectory),
            Payload.SafePath(directory, Payload.Required[2]), data);
    }
    public string[] EngineArguments() => ["--main-pack", Pack, "--path", Path.GetDirectoryName(Pack)!];
}
