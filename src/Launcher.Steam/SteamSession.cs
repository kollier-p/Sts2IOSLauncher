using SteamKit2;
using SteamKit2.Authentication;
using Launcher.Core;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

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
    public async Task<ulong> ResolveManifestAsync(string branch, CancellationToken ct)
    {
        RequireSession(); ct.ThrowIfCancellationRequested();
        var tokens = await Apps.PICSGetAccessTokens(Payload.AppId, null);
        tokens.AppTokens.TryGetValue(Payload.AppId, out var token);
        var info = await Apps.PICSGetProductInfo(new SteamApps.PICSRequest(Payload.AppId, token), null);
        ct.ThrowIfCancellationRequested();
        var product = info.Results?.FirstOrDefault(x => x.Apps.ContainsKey(Payload.AppId));
        if (product is null) throw new IOException("Steam did not return game product information.");
        var value = product.Apps[Payload.AppId].KeyValues["depots"][Payload.WindowsDepot.ToString(CultureInfo.InvariantCulture)]["manifests"][branch]["gid"].Value;
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0) throw new IOException("No accessible manifest for requested branch; no fallback attempted.");
        return id;
    }
    public async Task<InstallReceipt> DownloadAndInstallAsync(PayloadStore store, string branch, ulong? exactManifest, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        RequireSession(); if (string.IsNullOrWhiteSpace(branch) || exactManifest == 0) throw new ArgumentException("Invalid manifest selection.");
        progress?.Report(new("resolve", 0, 0));
        var manifestId = exactManifest ?? await ResolveManifestAsync(branch, ct);
        var ticket = await Apps.GetAppOwnershipTicket(Payload.AppId);
        if (ticket.Result != EResult.OK || ticket.Ticket.Length == 0) throw new UnauthorizedAccessException("Steam did not authorize access to the game.");
        var key = await Apps.GetDepotDecryptionKey(Payload.WindowsDepot, Payload.AppId);
        if (key.Result != EResult.OK || key.DepotKey.Length == 0) throw new UnauthorizedAccessException("Steam did not authorize access to the Windows depot.");
        var code = await Content.GetManifestRequestCode(Payload.WindowsDepot, Payload.AppId, manifestId, branch);
        if (code == 0) throw new UnauthorizedAccessException("Steam did not grant a manifest request code.");
        var servers = (await Content.GetServersForSteamPipe()).Where(s => !s.UseAsProxy && !s.SteamChinaOnly && s.Host != null && (s.AllowedAppIds.Length == 0 || s.AllowedAppIds.Contains(Payload.AppId))).OrderBy(s => s.WeightedLoad).Take(8).ToArray();
        if (servers.Length == 0) throw new IOException("No suitable Steam CDN servers.");
        using var cdn = new SteamKit2.CDN.Client(client);
        var authTokens = new System.Collections.Concurrent.ConcurrentDictionary<string, SteamContent.CDNAuthToken>();
        async Task<T> FromCdn<T>(Func<SteamKit2.CDN.Server, string?, Task<T>> action)
        {
            Exception? last = null;
            foreach (var server in servers)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    authTokens.TryGetValue(server.Host!, out var cached);
                    if (cached is not null && cached.Expiration <= DateTime.UtcNow) cached = null;
                    try { return await action(server, cached?.Token); }
                    catch (SteamKitWebRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    {
                        var auth = await Content.GetCDNAuthToken(Payload.AppId, Payload.WindowsDepot, server.Host!);
                        if (auth.Result != EResult.OK || string.IsNullOrEmpty(auth.Token)) throw new UnauthorizedAccessException("Steam CDN authorization rejected.");
                        authTokens[server.Host!] = auth; return await action(server, auth.Token);
                    }
                }
                catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or UnauthorizedAccessException) { last = e; }
            }
            throw new IOException("All selected Steam CDN servers failed.", last);
        }
        var manifest = await FromCdn((s, t) => cdn.DownloadManifestAsync(Payload.WindowsDepot, manifestId, code, s, key.DepotKey, cdnAuthToken: t));
        if (manifest.ManifestGID != manifestId || manifest.DepotID != Payload.WindowsDepot || manifest.FilenamesEncrypted || manifest.Files is null) throw new InvalidDataException("Unexpected or encrypted manifest.");
        var selected = manifest.Files.Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory)).Select(f => (File: f, Path: Payload.Normalize(f.FileName))).Where(x => Payload.Include(x.Path)).ToArray();
        if (selected.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length || !Payload.Required.All(p => selected.Any(x => x.Path == p))) throw new InvalidDataException("Duplicate or missing manifest payload files.");
        foreach (var x in selected) if (!string.IsNullOrEmpty(x.File.LinkTarget) || x.File.Flags.HasFlag(EDepotFileFlag.Symlink)) throw new InvalidDataException("Symlink entries unsupported.");
        long total = selected.Sum(x => checked((long)x.File.TotalSize)), done = 0;
        Directory.CreateDirectory(store.Root);
        // Serialize installers across sessions; the OS releases this lock on process exit.
        using var installLock = new FileStream(Path.Combine(store.Root, ".download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var staging = Payload.SafePath(store.Root, $"stage-resume-{manifestId}");
        Directory.CreateDirectory(staging);
        var progressLock = new object();
        var meter = new TransferMeter(total);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void ReportBytes(long bytes, bool downloaded = false)
        {
            lock (progressLock) { var sample = meter.Add(bytes, downloaded, clock.Elapsed); done = sample.Completed; progress?.Report(new("download", done, total, sample.Estimate.BytesPerSecond, sample.Estimate.Remaining)); }
        }
        bool processing = false;
        try
        {
            await Parallel.ForEachAsync(selected, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (entry, workerToken) =>
            {
                var (file, relative) = entry;
                using var workerCdn = new SteamKit2.CDN.Client(client);
                workerToken.ThrowIfCancellationRequested();
                var output = Payload.SafePath(staging, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                // Reuse only complete files whose length and Steam manifest hash match.
                if (File.Exists(output))
                {
                    if (DownloadCache.IsVerified(output, checked((long)file.TotalSize), file.FileHash))
                    {
                        ReportBytes(checked((long)file.TotalSize));
                        return;
                    }
                }
                using (var stream = new FileStream(output, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    ulong cursor = 0;
                    foreach (var chunk in file.Chunks.OrderBy(c => c.Offset))
                    {
                        workerToken.ThrowIfCancellationRequested();
                        if (chunk.Offset != cursor || chunk.UncompressedLength == 0 || chunk.UncompressedLength > 64 * 1024 * 1024 || cursor > file.TotalSize || chunk.UncompressedLength > file.TotalSize - cursor) throw new InvalidDataException("Invalid chunk bounds or gap.");
                        int length = checked((int)chunk.UncompressedLength);
                        if (ChunkCache.Verify(stream, checked((long)cursor), length, chunk.ChunkID))
                        {
                            cursor += (uint)length; ReportBytes(length);
                            continue;
                        }
                        stream.SetLength(checked((long)cursor));
                        stream.Position = checked((long)cursor);
                        var data = new byte[length];
                        var size = await FromCdn((s, t) => workerCdn.DownloadDepotChunkAsync(Payload.WindowsDepot, chunk, s, data, key.DepotKey, cdnAuthToken: t));
                        if (size != data.Length) throw new InvalidDataException("Unexpected chunk size.");
                        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(data), chunk.ChunkID))
                            throw new InvalidDataException("Downloaded chunk hash mismatch.");
                        await stream.WriteAsync(data, workerToken); cursor += (uint)size; ReportBytes(size, true);
                    }
                    if (cursor != file.TotalSize) throw new InvalidDataException("File length mismatch.");
                    stream.SetLength(checked((long)file.TotalSize));
                    stream.Flush(true);
                }
                using var read = File.OpenRead(output);
                if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(read), file.FileHash)) throw new InvalidDataException("Downloaded file hash mismatch.");
            });
            processing = true;
            progress?.Report(new("process", done, total));
            var receipt = store.Install(staging, manifestId, branch, ct);
            progress?.Report(new("readyToRelaunch", done, total)); return receipt;
        }
        finally { if (processing && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }
    void RequireSession() { if (!authenticated) throw new InvalidOperationException("Steam login required."); }
    public async ValueTask DisposeAsync() { stop.Cancel(); client.Disconnect(); await pump; stop.Dispose(); }
}
