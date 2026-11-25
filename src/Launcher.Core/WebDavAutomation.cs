using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core;

public sealed class WebDavAutomation(WebDavSettings settings, string stateRoot, string profileKey)
{
    public bool BackgroundEnabled => settings.Automation == WebDavAutomationMode.SyncInBackground;
    bool UploadEnabled => settings.Automation is WebDavAutomationMode.SyncBeforePlay or WebDavAutomationMode.SyncInBackground;
    string PendingPath()
    {
        var identity = Encoding.UTF8.GetBytes($"{settings.BaseUri()}\n{settings.User}\n{settings.Slot}\n{profileKey}");
        return Payload.SafePath(stateRoot, Convert.ToHexString(SHA256.HashData(identity)) + ".pending");
    }

    public async Task BeforePlayAsync(Func<bool, CancellationToken, Task> sync, CancellationToken ct)
    {
        if (settings.Automation != WebDavAutomationMode.PullBeforePlay && !UploadEnabled) return;
        ct.ThrowIfCancellationRequested();
        if (UploadEnabled)
        {
            var pending = PendingPath();
            if (File.Exists(pending))
            {
                await sync(true, ct);
                File.Delete(pending);
            }
        }
        await sync(false, ct);
    }

    public void GameStarting()
    {
        if (!UploadEnabled) return;
        Directory.CreateDirectory(stateRoot);
        using var marker = new FileStream(PendingPath(), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        marker.Flush(flushToDisk: true);
    }

    public async Task WhileBackgroundedAsync(Func<bool, CancellationToken, Task> sync, CancellationToken ct)
    {
        if (!BackgroundEnabled || !File.Exists(PendingPath())) return;
        // Keep the marker for the next launch since the game can resume and write more saves.
        await sync(true, ct);
    }
}
