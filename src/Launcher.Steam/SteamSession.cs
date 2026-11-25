using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
using Launcher.Core;
using System.Globalization;

namespace Launcher.Steam;

// The UI supplies a Steam Guard authenticator and stores this record in iOS Keychain, never in logs.
public sealed record SteamIdentity(string AccountName, string RefreshToken, string? GuardData)
{
    public override string ToString() => "SteamIdentity [redacted]";
}
public sealed record DownloadProgress(string Stage, long Completed, long Total, double BytesPerSecond = 0, TimeSpan? Remaining = null);
public sealed class SteamSession : IAsyncDisposable
{
    readonly SteamClient client = new();
    readonly CallbackManager callbacks;
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<EResult> loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Task pump;
    volatile bool authenticated;
    SteamApps Apps => client.GetHandler<SteamApps>()!;
    SteamContent Content => client.GetHandler<SteamContent>()!;
    public SteamSession()
    {
        callbacks = new CallbackManager(client);
        callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => { connected.TrySetException(new IOException("Steam disconnected.")); loggedOn.TrySetException(new IOException("Steam disconnected.")); authenticated = false; });
        callbacks.Subscribe<SteamUser.LoggedOnCallback>(c => loggedOn.TrySetResult(c.Result));
        pump = Task.Run(() => { while (!stop.IsCancellationRequested) callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); });
        client.Connect();
    }
    public async Task<SteamIdentity> AuthenticateAsync(string username, string password, IAuthenticator authenticator, string? priorGuardData, bool remember, CancellationToken ct)
    {
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        var auth = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails { Username = username, Password = password, Authenticator = authenticator, GuardData = priorGuardData, IsPersistentSession = remember }).WaitAsync(ct);
        var result = await auth.PollingWaitForResultAsync(ct);
        var identity = new SteamIdentity(result.AccountName, result.RefreshToken, result.NewGuardData ?? priorGuardData);
        await LogOnAsync(identity, remember, ct);
        return identity;
    }
    public async Task LogOnAsync(SteamIdentity identity, bool remember, CancellationToken ct)
    {
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        client.GetHandler<SteamUser>()!.LogOn(new SteamUser.LogOnDetails { Username = identity.AccountName, AccessToken = identity.RefreshToken, ShouldRememberPassword = remember });
        var result = await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(45), ct);
        if (result != EResult.OK) throw new IOException($"Steam login failed: {result}");
        authenticated = true;
    }
    public sealed record GameBranch(string Name, string Description, bool PasswordRequired, ulong? ManifestId);
    public async Task<IReadOnlyList<GameBranch>> ListBranchesAsync(CancellationToken ct)
    {
        RequireSession(); ct.ThrowIfCancellationRequested();
        var tokens = await Apps.PICSGetAccessTokens(Payload.AppId, null).ToTask().WaitAsync(ct);
        tokens.AppTokens.TryGetValue(Payload.AppId, out var token);
        var info = await Apps.PICSGetProductInfo(new SteamApps.PICSRequest(Payload.AppId, token), null).ToTask().WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        var product = info.Results?.FirstOrDefault(x => x.Apps.ContainsKey(Payload.AppId))
            ?? throw new IOException("Steam did not return game product information.");
        var depots = product.Apps[Payload.AppId].KeyValues["depots"];
        var manifests = depots[Payload.WindowsDepot.ToString(CultureInfo.InvariantCulture)]["manifests"];
        var result = new List<GameBranch>();
        foreach (var branch in depots["branches"].Children)
        {
            var name = branch.Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = manifests[name]["gid"].Value;
            ulong? id = ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed != 0 ? parsed : null;
            result.Add(new(name, branch["description"].Value ?? "", branch["pwdrequired"].Value == "1", id));
        }
        return result.OrderBy(x => x.Name == "public" ? 0 : 1).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }
    public async Task<ulong> ResolveManifestAsync(string branch, CancellationToken ct, string? betaPassword = null)
    {
        RequireSession(); ct.ThrowIfCancellationRequested();
        var tokens = await Apps.PICSGetAccessTokens(Payload.AppId, null).ToTask().WaitAsync(ct);
        tokens.AppTokens.TryGetValue(Payload.AppId, out var token);
        var info = await Apps.PICSGetProductInfo(new SteamApps.PICSRequest(Payload.AppId, token), null).ToTask().WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        var product = info.Results?.FirstOrDefault(x => x.Apps.ContainsKey(Payload.AppId));
        if (product is null) throw new IOException("Steam did not return game product information.");
        var value = product.Apps[Payload.AppId].KeyValues["depots"][Payload.WindowsDepot.ToString(CultureInfo.InvariantCulture)]["manifests"][branch]["gid"].Value;
        if (ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0) return id;
        if (string.IsNullOrEmpty(betaPassword) || string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
            throw new IOException("No accessible manifest for requested branch; no fallback attempted.");
        var unlock = await Apps.CheckAppBetaPassword(Payload.AppId, betaPassword).ToTask().WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (unlock.Result != EResult.OK || !unlock.BetaPasswords.TryGetValue(branch, out var hash))
            throw new UnauthorizedAccessException("Steam rejected the beta branch password.");
        var privateInfo = await Apps.PICSGetPrivateBeta(Payload.AppId, token, branch, hash).ToTask().WaitAsync(ct);
        ct.ThrowIfCancellationRequested();
        if (privateInfo.Result != EResult.OK) throw new UnauthorizedAccessException("Steam rejected private branch access.");
        value = privateInfo.DepotSection[Payload.WindowsDepot.ToString(CultureInfo.InvariantCulture)]["manifests"][branch]["gid"].Value;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0)
            throw new IOException("No accessible manifest for unlocked branch; no fallback attempted.");
        return id;
    }
    public async Task<InstallReceipt> DownloadAndInstallAsync(PayloadStore store, string branch, ulong? exactManifest, IProgress<DownloadProgress>? progress, CancellationToken ct, string? betaPassword = null)
    {
        RequireSession(); if (string.IsNullOrWhiteSpace(branch) || exactManifest == 0) throw new ArgumentException("Invalid manifest selection.");
        progress?.Report(new("resolve", 0, 0));
        var manifestId = exactManifest ?? await ResolveManifestAsync(branch, ct, betaPassword);
        if (exactManifest.HasValue && !string.IsNullOrEmpty(betaPassword) && !string.Equals(branch, "public", StringComparison.OrdinalIgnoreCase))
            await ResolveManifestAsync(branch, ct, betaPassword);
        var ticket = await Apps.GetAppOwnershipTicket(Payload.AppId).ToTask().WaitAsync(ct);
        if (ticket.Result != EResult.OK || ticket.Ticket.Length == 0) throw new UnauthorizedAccessException("Steam did not authorize access to the game.");
        var key = await Apps.GetDepotDecryptionKey(Payload.WindowsDepot, Payload.AppId).ToTask().WaitAsync(ct);
        if (key.Result != EResult.OK || key.DepotKey.Length == 0) throw new UnauthorizedAccessException("Steam did not authorize access to the Windows depot.");
        var code = await Content.GetManifestRequestCode(Payload.WindowsDepot, Payload.AppId, manifestId, branch).WaitAsync(ct);
        if (code == 0) throw new UnauthorizedAccessException("Steam did not grant a manifest request code.");
        var transfer = await SteamDepotTransfer.OpenAsync(client, Content, Payload.WindowsDepot, key.DepotKey, ct);
        var manifest = await transfer.ManifestAsync(manifestId, code, ct);
        var files = SteamDepotTransfer.SelectFiles(manifest, Payload.Include);
        if (!Payload.Required.All(required => files.Any(entry => entry.Path == required)))
            throw new InvalidDataException("Missing required game files in manifest.");
        // Serialize installers across sessions; the OS releases this lock on process exit.
        using var installLock = store.AcquireWriteLock();
        var staging = Payload.SafePath(store.Root, $"stage-resume-{manifestId}");
        var processing = false;
        try
        {
            var total = await transfer.DownloadAsync(files, staging, 3, progress, ct);
            processing = true;
            progress?.Report(new("process", total, total));
            var receipt = store.Install(staging, manifestId, branch, ct);
            progress?.Report(new("readyToRelaunch", total, total));
            return receipt;
        }
        finally { if (processing && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    void RequireSession() { if (!authenticated) throw new InvalidOperationException("Steam login required."); }

    public async Task<(WorkshopItem Item, IReadOnlyList<ulong> Dependencies)> WorkshopDetailsAsync(ulong id, CancellationToken ct)
    {
        RequireSession();
        var published = client.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>();
        var query = new CPublishedFile_GetDetails_Request { appid = Payload.AppId, includechildren = true };
        query.publishedfileids.Add(id);
        var result = await published.GetDetails(query).ToTask().WaitAsync(ct);
        var details = result.Result == EResult.OK ? result.Body?.publishedfiledetails.SingleOrDefault(item =>
            item.publishedfileid == id && item.consumer_appid == Payload.AppId && item.result == (uint)EResult.OK && !item.banned) : null;
        if (details is null) throw new IOException($"Steam returned no authorized details for Workshop item {id}.");
        var title = string.IsNullOrWhiteSpace(details.title) ? $"Workshop {id}" : details.title;
        var item = new WorkshopItem(id, title, null, null, details.hcontent_file == 0 ? null : details.hcontent_file, details.time_updated);
        var dependencies = details.children.OrderBy(child => child.sortorder).Select(child => child.publishedfileid).Distinct().ToArray();
        return (item, dependencies);
    }

    public async Task<IReadOnlyList<WorkshopManifest>> WorkshopManifestsAsync(WorkshopItem item, CancellationToken ct)
    {
        RequireSession();
        var published = client.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>();
        var request = new CPublishedFile_GetItemInfo_Request { appid = Payload.AppId };
        request.workshop_items.Add(new() { published_file_id = item.Id });
        var result = await published.GetItemInfo(request).ToTask().WaitAsync(ct);
        if (result.Result == EResult.AccessDenied || result.Body?.private_items.Contains(item.Id) == true)
            throw new UnauthorizedAccessException("Steam did not authorize this Workshop item.");
        var info = result.Result == EResult.OK
            ? result.Body?.workshop_items.SingleOrDefault(entry => entry.published_file_id == item.Id) : null;
        var choices = info?.author_snapshots.Where(snapshot => snapshot.manifestid != 0).Select(snapshot =>
            WorkshopManifest.Snapshot(snapshot.manifestid, snapshot.game_branch_min, snapshot.game_branch_max,
                snapshot.timestamp, "Author snapshot")).ToList() ?? [];
        if (choices.Count == 0)
        {
            var history = await published.GetChangeHistory(new() { publishedfileid = item.Id, count = 50 }).ToTask().WaitAsync(ct);
            if (history.Result == EResult.OK)
                choices.AddRange(history.Body.changes.Where(change => change.saved_snapshot && change.manifest_id != 0)
                    .Select(change => WorkshopManifest.Snapshot(change.manifest_id, change.snapshot_game_branch_min,
                        change.snapshot_game_branch_max, change.timestamp, "Change history snapshot")));
        }
        if ((info?.manifest_id is > 0 ? info.manifest_id : item.Manifest) is ulong defaultId)
            choices.Add(new(defaultId, "public", "", "", info?.time_updated ?? 0, "Default manifest"));
        return choices.DistinctBy(choice => (choice.Id, choice.MinimumBranch, choice.MaximumBranch))
            .OrderByDescending(choice => choice.Timestamp).ToArray();
    }

    public async Task DownloadWorkshopManifestAsync(WorkshopManifest selection, string staging, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        RequireSession();
        const uint workshopDepot = Payload.AppId;
        var code = await Content.GetManifestRequestCode(workshopDepot, Payload.AppId, selection.Id, selection.Branch).WaitAsync(ct);
        if (code == 0) throw new UnauthorizedAccessException("Steam did not authorize this Workshop manifest or branch.");
        var key = await Apps.GetDepotDecryptionKey(workshopDepot, Payload.AppId).ToTask().WaitAsync(ct);
        if (key.Result != EResult.OK || key.DepotKey.Length == 0)
            throw new UnauthorizedAccessException("Steam did not authorize access to the Workshop depot.");
        var transfer = await SteamDepotTransfer.OpenAsync(client, Content, workshopDepot, key.DepotKey, ct);
        var manifest = await transfer.ManifestAsync(selection.Id, code, ct);
        var files = SteamDepotTransfer.SelectFiles(manifest, _ => true);
        if (files.Length is 0 or > 200_000 || files.Sum(file => checked((long)file.File.TotalSize)) > 16L * 1024 * 1024 * 1024)
            throw new InvalidDataException("The Workshop download is empty or exceeds the supported size.");
        await transfer.DownloadAsync(files, staging, 2, progress, ct);
    }

    public SteamCloudSaves OpenCloud(HttpClient http)
    {
        RequireSession();
        var account = client.SteamID?.ConvertToUInt64() ?? throw new InvalidOperationException("Steam account is unavailable.");
        var cloud = client.GetHandler<SteamUnifiedMessages>()!.CreateService<SteamKit2.Internal.Cloud>();
        return new SteamCloudSaves(cloud, http, account, client.CellID ?? 0);
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); client.Disconnect(); await pump; stop.Dispose(); }
}
