using Launcher.Core;

namespace Launcher.Host;

internal sealed class GameWebDavSync(WebDavSettings settings, string support, string profile, string saves)
{
    readonly WebDavAutomation automation = new(settings, Payload.SafePath(support, "WebDavAutomation"), profile);
    readonly string snapshots = Payload.SafePath(support, $"SaveSnapshots/{profile}");
    readonly string baselines = Payload.SafePath(support, "WebDavBaselines");
    readonly object sync = new();
    CancellationTokenSource? background;

    public async Task BeforePlayAsync(Action<bool> progress, CancellationToken ct)
    {
        using var http = OpenHttp();
        var cloud = new WebDavSaves(http, settings);
        await automation.BeforePlayAsync(async (upload, token) =>
        {
            progress(upload);
            await cloud.SyncAsync(saves, snapshots, baselines, profile, upload, overwriteConflicts: false, token);
        }, ct);
    }

    public void GameStarting() => automation.GameStarting();

    public bool StartBackground()
    {
        if (!automation.BackgroundEnabled) return false;
        CancellationTokenSource deadline;
        lock (sync)
        {
            if (background is not null) return false;
            background = deadline = new(TimeSpan.FromSeconds(20));
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = OpenHttp();
                var cloud = new WebDavSaves(http, settings);
                await automation.WhileBackgroundedAsync(async (upload, token) =>
                {
                    HostDiagnostics.Stage("background WebDAV upload");
                    await cloud.SyncAsync(saves, snapshots, baselines, profile, upload, overwriteConflicts: false, token);
                    HostDiagnostics.Stage("background WebDAV upload completed");
                }, deadline.Token);
            }
            catch (OperationCanceledException) { HostDiagnostics.Stage("background WebDAV paused"); }
            catch (Exception error) { HostDiagnostics.Error("background WebDAV sync", error); }
            finally
            {
                lock (sync) background = null;
                deadline.Dispose();
            }
        });
        return true;
    }

    public bool BackgroundActive
    {
        get { lock (sync) return background is not null; }
    }

    public void CancelBackground()
    {
        lock (sync)
            if (background is not null) _ = background.CancelAsync();
    }

    static HttpClient OpenHttp() => new(new HttpClientHandler { AllowAutoRedirect = false });
}
