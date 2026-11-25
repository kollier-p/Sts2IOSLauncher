using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Launcher.Core;
using SteamKit2;
using SteamKit2.Internal;

namespace Launcher.Steam;

public sealed record SteamCloudDownloadProgress(int VerifiedFiles, int TotalFiles, long CompletedBytes, long TotalBytes, bool Applying = false);

public sealed class SteamCloudSaves(Cloud cloud, HttpClient http, ulong account, uint cell)
{
    const string InstallPrefix = "%GameInstall%/";

    sealed record LocalFile(string Path, string Hash);
    sealed record RemoteFile(string Name, string Hash, uint Length);

    public static string? LocalPath(string remote, bool includeSettings)
    {
        var path = remote.Replace('\\', '/').TrimStart('/');
        if (path.StartsWith(InstallPrefix, StringComparison.OrdinalIgnoreCase)) path = path[InstallPrefix.Length..];
        return SaveFileRules.Include(path, includeSettings) ? path : null;
    }

    public async Task<SaveSyncResult> SyncAsync(string localRoot, string snapshotRoot, string baselineRoot,
        string profileKey, bool includeSettings, bool upload, bool overwriteConflicts, CancellationToken ct,
        IProgress<SteamCloudDownloadProgress>? progress = null)
    {
        var history = new SaveSyncHistory(baselineRoot, $"Steam\n{account}\n{profileKey}");
        var local = new Dictionary<string, LocalFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, file) in SaveFileRules.Enumerate(localRoot, includeSettings, ct))
        {
            await using var input = File.OpenRead(file);
            local.Add(relative, new(file, Convert.ToHexString(await SHA1.HashDataAsync(input, ct))));
        }

        var list = await List(ct);
        var remote = new Dictionary<string, RemoteFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list.files)
        {
            if (entry.persist_state != ECloudStoragePersistState.k_ECloudStoragePersistStatePersisted) continue;
            var prefix = entry.path_prefix_index < (uint)list.path_prefixes.Count
                ? list.path_prefixes[(int)entry.path_prefix_index] : "";
            var name = prefix.Length == 0 ? entry.file_name : prefix.EndsWith('/') || prefix.EndsWith('\\')
                ? prefix + entry.file_name : prefix + (prefix.Contains('\\') && !prefix.Contains('/') ? "\\" : "/") + entry.file_name;
            var path = LocalPath(name, includeSettings);
            if (path is null) continue;
            if (entry.sha_file is not { Length: 20 }) throw new InvalidDataException("Steam Cloud returned an invalid save checksum.");
            var candidate = new RemoteFile(name, Convert.ToHexString(entry.sha_file), entry.raw_file_size);
            if (remote.TryGetValue(path, out var previous))
            {
                if (previous.Hash != candidate.Hash || previous.Length != candidate.Length)
                    throw new InvalidDataException("Steam Cloud returned multiple versions of the same save path.");
                continue;
            }
            remote.Add(path, candidate);
        }

        var localHashes = local.ToDictionary(file => file.Key, file => file.Value.Hash, StringComparer.OrdinalIgnoreCase);
        var remoteHashes = remote.ToDictionary(file => file.Key, file => file.Value.Hash, StringComparer.OrdinalIgnoreCase);
        var changes = history.Changes(upload ? localHashes : remoteHashes, upload ? remoteHashes : localHashes, overwriteConflicts);
        if (changes.Count != 0)
        {
            if (upload)
            {
                if ((await List(ct)).current_change_number != list.current_change_number)
                    throw new IOException("Steam Cloud changed while preparing the upload.");
                await Upload(changes.Select(path => (remote.TryGetValue(path, out var existing) ? existing.Name : path, local[path])).ToArray(), ct);
            }
            else
            {
                var staging = Directory.CreateTempSubdirectory("sts2-steam-cloud-").FullName;
                try
                {
                    var files = new Dictionary<string, string>(StringComparer.Ordinal);
                    var totalBytes = changes.Sum(path => (long)remote[path].Length);
                    long verifiedBytes = 0;
                    foreach (var path in changes)
                    {
                        var file = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                        void ReportFile(long bytes) => progress?.Report(new(files.Count, changes.Count, verifiedBytes + bytes, totalBytes));
                        ReportFile(0);
                        await Download(remote[path], file, ReportFile, ct);
                        var destination = local.TryGetValue(path, out var existing)
                            ? Path.GetRelativePath(localRoot, existing.Path).Replace(Path.DirectorySeparatorChar, '/') : path;
                        files.Add(destination, file);
                        verifiedBytes += remote[path].Length;
                    }
                    progress?.Report(new(files.Count, changes.Count, verifiedBytes, totalBytes, Applying: true));
                    new SaveSnapshots(localRoot, snapshotRoot).ApplyDownloadedFiles(files, ct);
                }
                finally { Directory.Delete(staging, recursive: true); }
            }
            foreach (var path in changes) history.Remember(path, upload ? localHashes[path] : remoteHashes[path]);
        }
        history.Save();
        return new(changes.Count, upload ? local.Count : remote.Count);
    }

    async Task<CCloud_GetAppFileChangelist_Response> List(CancellationToken ct)
    {
        var result = await Call(cloud.GetAppFileChangelist(new() { appid = Payload.AppId, synced_change_number = 0 }), ct);
        if (result.is_only_delta) throw new InvalidDataException("Steam Cloud did not return a full save listing.");
        return result;
    }

    async Task Download(RemoteFile file, string destination, Action<long> progress, CancellationToken ct)
    {
        var info = await Call(cloud.ClientFileDownload(new() { appid = Payload.AppId, filename = file.Name }), ct);
        if (info.encrypted || info.is_explicit_delete || info.raw_file_size != file.Length)
            throw new InvalidDataException("Steam Cloud changed the requested save or returned an unsupported file.");
        using var request = new HttpRequestMessage(HttpMethod.Get, TransferUri(info.use_https, info.url_host, info.url_path));
        foreach (var header in info.request_headers) request.Headers.TryAddWithoutValidation(header.name, header.value);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        if (info.raw_file_size == info.file_size)
        {
            await WriteDownload(body, destination, file.Length, ct, progress);
        }
        else
        {
            var compressed = destination + ".zip";
            // Scale the compressed transfer to the raw size used by the save listing.
            await WriteDownload(body, compressed, info.file_size, ct,
                bytes => progress((long)((decimal)bytes * file.Length / info.file_size)));
            using (var archive = ZipFile.OpenRead(compressed))
            {
                if (archive.Entries.Count != 1 || archive.Entries[0].Length != file.Length)
                    throw new InvalidDataException("Unexpected Steam Cloud save archive.");
                await using var entry = archive.Entries[0].Open();
                await WriteDownload(entry, destination, file.Length, ct);
            }
            File.Delete(compressed);
        }
        await using var input = File.OpenRead(destination);
        if (Convert.ToHexString(await SHA1.HashDataAsync(input, ct)) != file.Hash)
            throw new InvalidDataException("Steam Cloud save checksum mismatch.");
    }

    async Task Upload((string Name, LocalFile File)[] files, CancellationToken ct)
    {
        var begin = new CCloud_BeginAppUploadBatch_Request { appid = Payload.AppId, machine_name = "STS2 iPad Launcher" };
        begin.files_to_upload.AddRange(files.Select(file => file.Name));
        var batch = await Call(cloud.BeginAppUploadBatch(begin), ct);
        if (batch.batch_id == 0) throw new IOException("Steam Cloud did not grant an upload batch.");
        var status = EResult.Fail;
        try
        {
            foreach (var (name, file) in files)
                await UploadFile(name, file, batch.batch_id, ct);
            status = EResult.OK;
        }
        finally
        {
            var complete = cloud.CompleteAppUploadBatchBlocking(new()
            {
                appid = Payload.AppId, batch_id = batch.batch_id, batch_eresult = (uint)status
            });
            if (status == EResult.OK) await Call(complete, ct);
            else
            {
                try { await Call(complete, CancellationToken.None); }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
            }
        }
    }

    async Task UploadFile(string name, LocalFile file, ulong batchId, CancellationToken ct)
    {
        await using var input = File.OpenRead(file.Path);
        var length = checked((uint)input.Length);
        var hash = await SHA1.HashDataAsync(input, ct);
        if (Convert.ToHexString(hash) != file.Hash) throw new InvalidDataException("A local save changed while preparing the upload.");
        var result = await Call(cloud.ClientBeginFileUpload(new()
        {
            appid = Payload.AppId, filename = name, file_size = length, raw_file_size = length, file_sha = hash,
            time_stamp = (ulong)Math.Max(0, new DateTimeOffset(File.GetLastWriteTimeUtc(file.Path)).ToUnixTimeSeconds()),
            cell_id = cell, platforms_to_sync = uint.MaxValue, can_encrypt = false, upload_batch_id = batchId
        }), ct);
        var sent = false;
        try
        {
            if (result.encrypt_file || result.block_requests.Count == 0)
                throw new IOException("Steam Cloud returned an unsupported upload request.");
            foreach (var block in result.block_requests)
            {
                HttpContent content;
                if (block.explicit_body_data is { Length: > 0 } data)
                    content = new ByteArrayContent(data);
                else
                {
                    if (block.block_offset > length || block.block_length > length - block.block_offset)
                        throw new InvalidDataException("Steam Cloud returned an invalid upload block.");
                    content = new FileRange(input, (long)block.block_offset, block.block_length);
                }
                using var blockContent = content;
                var method = block.http_method switch
                {
                    3 => HttpMethod.Post,
                    4 => HttpMethod.Put,
                    _ => throw new InvalidDataException("Steam Cloud returned an unsupported HTTP method.")
                };
                using var request = new HttpRequestMessage(method, TransferUri(block.use_https, block.url_host, block.url_path))
                {
                    Content = blockContent
                };
                foreach (var header in block.request_headers)
                    if (!request.Headers.TryAddWithoutValidation(header.name, header.value))
                        request.Content.Headers.TryAddWithoutValidation(header.name, header.value);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
            }
            sent = true;
        }
        finally
        {
            var commit = cloud.ClientCommitFileUpload(new()
            {
                appid = Payload.AppId, filename = name, file_sha = hash, transfer_succeeded = sent
            });
            if (sent)
            {
                if (!(await Call(commit, ct)).file_committed) throw new IOException("Steam Cloud did not commit the save.");
            }
            else
            {
                try { await Call(commit, CancellationToken.None); }
                catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException) { }
            }
        }
    }

    sealed class FileRange(Stream input, long offset, long length) : HttpContent
    {
        protected override bool TryComputeLength(out long size) { size = length; return true; }

        protected override Task SerializeToStreamAsync(Stream output, TransportContext? context)
            => SerializeToStreamAsync(output, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream output, TransportContext? context, CancellationToken ct)
        {
            input.Position = offset;
            var buffer = new byte[65536];
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
                remaining -= count;
            }
        }
    }

    static async Task WriteDownload(Stream source, string destination, uint length, CancellationToken ct,
        Action<long>? progress = null)
    {
        PayloadStorage.EnsureCapacity(Path.GetDirectoryName(destination)!, length);
        await using var output = File.Create(destination);
        var buffer = new byte[65536];
        long remaining = length;
        while (remaining != 0)
        {
            var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
            if (count == 0) throw new InvalidDataException("Truncated Steam Cloud save.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            remaining -= count;
            progress?.Invoke(length - remaining);
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), ct) != 0)
            throw new InvalidDataException("Steam Cloud save exceeds its declared size.");
    }

    static Uri TransferUri(bool https, string host, string path)
    {
        if (!https || host.Length == 0 || host.Contains('/') || host.Contains('@') ||
            !path.StartsWith('/') || !Uri.TryCreate($"https://{host}{path}", UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new IOException("Steam Cloud did not provide a valid HTTPS transfer URL.");
        return uri;
    }

    static async Task<T> Call<T>(AsyncJob<SteamUnifiedMessages.ServiceMethodResponse<T>> job, CancellationToken ct)
        where T : ProtoBuf.IExtensible, new()
    {
        var response = await job.ToTask().WaitAsync(TimeSpan.FromSeconds(30), ct);
        if (response.Result != EResult.OK) throw new IOException($"Steam Cloud returned {response.Result}.");
        return response.Body;
    }
}
