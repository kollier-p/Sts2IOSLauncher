using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Launcher.Core;
using SteamKit2;
using SteamKit2.CDN;

namespace Launcher.Steam;

internal sealed class SteamDepotTransfer(SteamClient client, SteamContent content, uint depot, Server[] servers, byte[] key)
{
    readonly ConcurrentDictionary<string, SteamContent.CDNAuthToken> authTokens = new();

    public static async Task<SteamDepotTransfer> OpenAsync(SteamClient client, SteamContent content, uint depot,
        byte[] key, CancellationToken ct)
    {
        var servers = (await content.GetServersForSteamPipe().WaitAsync(ct))
            .Where(server => !server.UseAsProxy && !server.SteamChinaOnly && server.Host is not null &&
                (server.AllowedAppIds.Length == 0 || server.AllowedAppIds.Contains(Payload.AppId)))
            .OrderBy(server => server.WeightedLoad).Take(8).ToArray();
        if (servers.Length == 0) throw new IOException("No suitable Steam CDN servers.");
        return new SteamDepotTransfer(client, content, depot, servers, key);
    }

    public async Task<DepotManifest> ManifestAsync(ulong id, ulong code, CancellationToken ct)
    {
        using var cdn = new Client(client);
        var manifest = await FromCdn((server, token) =>
            cdn.DownloadManifestAsync(depot, id, code, server, key, cdnAuthToken: token), ct);
        if (manifest.ManifestGID != id || manifest.DepotID != depot || manifest.FilenamesEncrypted || manifest.Files is null)
            throw new InvalidDataException("Unexpected or encrypted Steam manifest.");
        return manifest;
    }

    async Task<T> FromCdn<T>(Func<Server, string?, Task<T>> action, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var server in servers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                authTokens.TryGetValue(server.Host!, out var cached);
                if (cached is not null && cached.Expiration <= DateTime.UtcNow) cached = null;
                try
                {
                    var value = await action(server, cached?.Token);
                    ct.ThrowIfCancellationRequested();
                    return value;
                }
                catch (SteamKitWebRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    var auth = await content.GetCDNAuthToken(Payload.AppId, depot, server.Host!).WaitAsync(ct);
                    if (auth.Result != EResult.OK || string.IsNullOrEmpty(auth.Token))
                        throw new UnauthorizedAccessException("Steam CDN authorization rejected.");
                    ct.ThrowIfCancellationRequested();
                    authTokens[server.Host!] = auth;
                    var value = await action(server, auth.Token);
                    ct.ThrowIfCancellationRequested();
                    return value;
                }
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
            {
                last = error;
            }
        }
        throw new IOException("All selected Steam CDN servers failed.", last);
    }

    public static (DepotManifest.FileData File, string Path)[] SelectFiles(DepotManifest manifest, Func<string, bool> include)
    {
        var files = manifest.Files!.Where(file => !file.Flags.HasFlag(EDepotFileFlag.Directory))
            .Select(file => (File: file, Path: Payload.Normalize(file.FileName))).Where(entry => include(entry.Path)).ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in files)
        {
            if (!paths.Add(entry.Path) || !string.IsNullOrEmpty(entry.File.LinkTarget) || entry.File.Flags.HasFlag(EDepotFileFlag.Symlink))
                throw new InvalidDataException("Duplicate or linked Steam manifest file.");
            ChunkLayout.Validate(entry.File.TotalSize, entry.File.FileHash,
                entry.File.Chunks.Select(chunk => new ChunkExtent(chunk.Offset, chunk.UncompressedLength, chunk.ChunkID)));
        }
        return files;
    }

    public async Task<long> DownloadAsync((DepotManifest.FileData File, string Path)[] files, string staging, int concurrency,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var total = files.Sum(entry => checked((long)entry.File.TotalSize));
        var remaining = PayloadStorage.RemainingBytes(staging, files.Select(entry => (entry.Path, (long)entry.File.TotalSize)));
        PayloadStorage.EnsureCapacity(Path.GetDirectoryName(staging)!, remaining);
        Directory.CreateDirectory(staging);
        var progressLock = new object();
        var meter = new TransferMeter(total);
        var clock = Stopwatch.StartNew();
        void ReportBytes(long bytes, bool downloaded)
        {
            lock (progressLock)
            {
                var sample = meter.Add(bytes, downloaded, clock.Elapsed);
                progress?.Report(new("download", sample.Completed, total, sample.Estimate.BytesPerSecond, sample.Estimate.Remaining));
            }
        }

        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            (entry, token) => new ValueTask(WriteFileAsync(entry.File, Payload.SafePath(staging, entry.Path), ReportBytes, token)));
        return total;
    }

    async Task WriteFileAsync(DepotManifest.FileData file, string output, Action<long, bool> report, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var length = checked((long)file.TotalSize);
        if (File.Exists(output) && DownloadCache.IsVerified(output, length, file.FileHash))
        {
            report(length, false);
            return;
        }
        using var cdn = new Client(client);
        using (var stream = new FileStream(output, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var chunk in file.Chunks.OrderBy(chunk => chunk.Offset))
            {
                ct.ThrowIfCancellationRequested();
                var offset = checked((long)chunk.Offset);
                var chunkLength = checked((int)chunk.UncompressedLength);
                var hash = chunk.ChunkID ?? throw new InvalidDataException("Missing Steam chunk hash.");
                if (ChunkCache.Verify(stream, offset, chunkLength, hash))
                {
                    report(chunkLength, false);
                    continue;
                }
                stream.SetLength(offset);
                stream.Position = offset;
                var data = new byte[chunkLength];
                var size = await FromCdn(async (server, token) =>
                {
                    var count = await cdn.DownloadDepotChunkAsync(depot, chunk, server, data, key, cdnAuthToken: token);
                    ChunkLayout.VerifyDownloaded(data, count, hash);
                    return count;
                }, ct);
                await stream.WriteAsync(data, ct);
                report(size, true);
            }
            stream.SetLength(length);
            stream.Flush(true);
        }
        using var read = File.OpenRead(output);
        if (!CryptographicOperations.FixedTimeEquals(SHA1.HashData(read), file.FileHash))
            throw new InvalidDataException("Downloaded file hash mismatch.");
    }
}
