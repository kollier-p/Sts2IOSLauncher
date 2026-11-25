using System.Security.Cryptography;
using System.Text;

namespace Launcher.Core;

public sealed class WebDavAutomation(WebDavSettings settings, string stateRoot, string profileKey)
{
    string PendingPath()
    {
        var identity = Encoding.UTF8.GetBytes($"{settings.BaseUri()}\n{settings.User}\n{settings.Slot}\n{profileKey}");
        return Payload.SafePath(stateRoot, Convert.ToHexString(SHA256.HashData(identity)) + ".pending");
    }

    public async Task BeforePlayAsync(Func<bool, CancellationToken, Task> sync, CancellationToken ct)
    {
        if (settings.Automation is not (WebDavAutomationMode.PullBeforePlay or WebDavAutomationMode.SyncBeforePlay)) return;
        ct.ThrowIfCancellationRequested();
        if (settings.Automation == WebDavAutomationMode.SyncBeforePlay)
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
        if (settings.Automation != WebDavAutomationMode.SyncBeforePlay) return;
        Directory.CreateDirectory(stateRoot);
        using var marker = new FileStream(PendingPath(), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        marker.Flush(flushToDisk: true);
    }
}
