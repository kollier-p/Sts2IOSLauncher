using System.Runtime.InteropServices;
using Launcher.Core;

namespace Launcher.iOS;

// The native engine must own its own process startup; do not initialize a
// second Mono runtime inside the already-running .NET UIKit launcher.
internal static class GameHost
{
    internal static string RequestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sts2-launch-request.json");

    internal static void RequestLaunch(LaunchPlan plan)
    {
        var request = new LaunchRequest(1, plan.Receipt.PayloadId, plan.Receipt.ManifestId,
            plan.Pack, plan.Assemblies, plan.GameAssembly, plan.UserData);
        var temporary = RequestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                System.Text.Json.JsonSerializer.Serialize(stream, request);
                stream.Flush(true);
            }
            File.Move(temporary, RequestPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal sealed record LaunchRequest(int Schema, string PayloadId, ulong ManifestId,
        string Pack, string Assemblies, string GameAssembly, string UserData);
}
