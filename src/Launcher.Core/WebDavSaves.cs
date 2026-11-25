using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Launcher.Core;

public enum WebDavAutomationMode { Manual, PullBeforePlay, SyncBeforePlay, SyncInBackground }

public sealed record WebDavSettings(string Endpoint, string User, string Password, string Slot, bool IncludeSettings,
    WebDavAutomationMode Automation = WebDavAutomationMode.Manual)
{
    public Uri BaseUri()
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter an HTTPS WebDAV collection URL.");
        if (Slot.Length is < 1 or > 128 || Slot is "." or ".." ||
            Slot.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("Enter a remote slot containing letters, digits, dots, hyphens, or underscores.");
        if (User.Contains(':')) throw new ArgumentException("A WebDAV username cannot contain a colon.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }

    public override string ToString() => "WebDAV settings [redacted]";
}

public sealed class WebDavSaves(HttpClient http, WebDavSettings settings)
{
    static readonly HttpMethod PropFind = new("PROPFIND");
    static readonly HttpMethod MakeCollection = new("MKCOL");
    static readonly XNamespace Dav = "DAV:";
    readonly Uri endpoint = settings.BaseUri();
    readonly string slot = $"SlayTheSpire2/saves/{settings.Slot}";
    readonly AuthenticationHeaderValue? credentials = settings.User.Length == 0 ? null : new("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.User}:{settings.Password}")));

    sealed record SaveFile(string Relative, string? File, string Hash, EntityTagHeaderValue? ETag = null,
        DateTimeOffset? Modified = null, long? Length = null);
    sealed record RemoteDigest(string ETag, string Hash, long Length);

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        using var response = await ListCollection(endpoint, ct);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.MultiStatus) throw new IOException("The URL did not return a WebDAV collection.");
    }

    public async Task<SaveSyncResult> SyncAsync(string localRoot, string snapshotRoot, string baselineRoot, string profileKey,
        bool upload, bool overwriteConflicts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        localRoot = Path.GetFullPath(localRoot);
        var identity = $"{endpoint}\n{settings.User}\n{slot}\n{profileKey}";
        var history = new SaveSyncHistory(baselineRoot, identity);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var cachePath = Payload.SafePath(baselineRoot, cacheKey + ".webdav-etags");
        var cache = ReadRemoteDigests(cachePath);
        var scratch = Directory.CreateTempSubdirectory("sts2-webdav-").FullName;
        try
        {
            var local = await ReadLocal(localRoot, ct);
            var remote = await ReadRemote(scratch, cache, ct);
            SaveRemoteDigests(cachePath, remote);
            var source = upload ? local : remote;
            var destination = upload ? remote : local;
            var changes = history.Changes(Hashes(source), Hashes(destination), overwriteConflicts)
                .Select(name => source[name]).ToArray();

            if (upload)
            {
                var prepared = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    remote.TryGetValue(file.Relative, out var previous);
                    var path = $"{slot}/{previous?.Relative ?? file.Relative}";
                    await EnsureCollections(path[..path.LastIndexOf('/')], prepared, ct);
                    using var input = File.OpenRead(file.File!);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != file.Hash)
                        throw new IOException("A local save changed while preparing the upload.");
                    input.Position = 0;
                    using var content = new StreamContent(input);
                    using var request = Request(HttpMethod.Put, Location(path));
                    request.Content = content;
                    if (previous is null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                    else if (Strong(previous.ETag)) request.Headers.IfMatch.Add(previous.ETag!);
                    else if (previous.Modified is not null) request.Headers.IfUnmodifiedSince = previous.Modified;
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    history.Remember(file.Relative, file.Hash);
                }
            }
            else
            {
                var files = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in changes)
                {
                    var received = file.File is null
                        ? await DownloadRemote(file.Relative, scratch, file.ETag, file.Modified, file.Length, ct) : file;
                    if (received.Hash != file.Hash) throw new IOException("A remote WebDAV save changed while preparing the download.");
                    files.Add(local.TryGetValue(file.Relative, out var item) ? item.Relative : file.Relative, received.File!);
                }
                new SaveSnapshots(localRoot, snapshotRoot).ApplyDownloadedFiles(files, ct);
                foreach (var file in changes) history.Remember(file.Relative, file.Hash);
            }
            history.Save();
            return new(changes.Length, source.Count);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    async Task<Dictionary<string, SaveFile>> ReadLocal(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, SaveFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, file) in SaveFileRules.Enumerate(root, settings.IncludeSettings, ct))
            result.Add(relative, new(relative, file, await Hash(file, ct)));
        return result;
    }

    async Task<Dictionary<string, SaveFile>> ReadRemote(string scratch, IReadOnlyDictionary<string, RemoteDigest> cache, CancellationToken ct)
    {
        var result = new Dictionary<string, SaveFile>(StringComparer.OrdinalIgnoreCase);
        var folders = new Stack<string>();
        folders.Push("");
        while (folders.TryPop(out var folder))
        {
            var uri = Location(Join(slot, folder) + "/");
            using var response = await ListCollection(uri, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && folder.Length == 0) return result;
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var xml = XmlReader.Create(body, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = 8 * 1024 * 1024
            });
            var document = await XDocument.LoadAsync(xml, LoadOptions.None, ct);
            if (document.Root?.Name != Dav + "multistatus") throw new InvalidDataException("Invalid WebDAV collection response.");
            var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.Root.Elements(Dav + "response"))
            {
                var href = (string?)item.Element(Dav + "href");
                if (href is null || !Uri.TryCreate(uri, href, out var member) || !IsSameOrigin(member) ||
                    !member.AbsolutePath.StartsWith(uri.AbsolutePath, StringComparison.Ordinal)) continue;
                var child = member.AbsolutePath[uri.AbsolutePath.Length..].TrimEnd('/');
                if (child.Length == 0 || child.Contains('/')) continue;
                var name = Uri.UnescapeDataString(child);
                if (name.Contains('/') || name.Contains('\\') || !children.Add(name))
                    throw new InvalidDataException("Ambiguous WebDAV save path.");
                var relative = Join(folder, name);
                var collection = item.Descendants(Dav + "collection").Any();
                if (collection && SaveFileRules.Visit(relative)) folders.Push(relative);
                else if (!collection && SaveFileRules.Include(relative, settings.IncludeSettings))
                {
                    var properties = item.Elements(Dav + "propstat").Where(status =>
                        ((string?)status.Element(Dav + "status"))?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, "200", ..])
                        .SelectMany(status => status.Elements(Dav + "prop")).ToArray();
                    string? Value(string property) => properties.Select(element => (string?)element.Element(Dav + property)).FirstOrDefault(value => value is not null);
                    EntityTagHeaderValue.TryParse(Value("getetag"), out var etag);
                    var length = long.TryParse(Value("getcontentlength"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                        ? (long?)size : null;
                    var modified = DateTimeOffset.TryParse(Value("getlastmodified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                        ? (DateTimeOffset?)date : null;
                    var file = Strong(etag) && length is long fileSize && cache.TryGetValue(relative, out var known) && known is not null &&
                        known.ETag == etag!.Tag && known.Length == fileSize
                        ? new SaveFile(relative, null, known.Hash, etag, modified, length)
                        : await DownloadRemote(relative, scratch, etag, modified, length, ct);
                    result.Add(relative, file);
                }
            }
        }
        return result;
    }

    async Task<SaveFile> DownloadRemote(string relative, string scratch, EntityTagHeaderValue? etag,
        DateTimeOffset? modified, long? expectedLength, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, Location($"{slot}/{relative}"));
        if (Strong(etag)) request.Headers.IfMatch.Add(etag!);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (Strong(etag) && response.Headers.ETag is { } receivedTag && !receivedTag.Equals(etag))
            throw new IOException("A remote WebDAV save changed while downloading.");
        var length = response.Content.Headers.ContentLength ?? expectedLength;
        if (length is > 0) PayloadStorage.EnsureCapacity(scratch, length.Value);
        var file = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        await using (var output = File.Create(file))
            await response.Content.CopyToAsync(output, ct);
        var actual = new FileInfo(file).Length;
        if ((length is not null && length.Value != actual) || (expectedLength is not null && expectedLength.Value != actual))
            throw new InvalidDataException("The WebDAV save does not match its advertised length.");
        return new(relative, file, await Hash(file, ct), response.Headers.ETag ?? etag,
            response.Content.Headers.LastModified ?? modified, actual);
    }

    static Dictionary<string, RemoteDigest> ReadRemoteDigests(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize<Dictionary<string, RemoteDigest>>(stream) is { } items)
                    return new(items, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    static void SaveRemoteDigests(string path, Dictionary<string, SaveFile> remote)
    {
        var items = remote.Values.Where(file => Strong(file.ETag) && file.Length.HasValue)
            .ToDictionary(file => file.Relative, file => new RemoteDigest(file.ETag!.Tag, file.Hash, file.Length!.Value));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, items);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    static bool Strong(EntityTagHeaderValue? etag) => etag is { IsWeak: false, Tag: not "*" };

    async Task EnsureCollections(string path, HashSet<string> prepared, CancellationToken ct)
    {
        var prefix = "";
        foreach (var segment in path.Split('/'))
        {
            prefix = Join(prefix, segment);
            if (!prepared.Add(prefix)) continue;
            var uri = Location(prefix + "/");
            using var request = Request(MakeCollection, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.MethodNotAllowed) response.EnsureSuccessStatusCode();
        }
    }

    async Task<HttpResponseMessage> ListCollection(Uri uri, CancellationToken ct)
    {
        using var request = Request(PropFind, uri);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            "<propfind xmlns=\"DAV:\"><prop><resourcetype/><getetag/><getcontentlength/><getlastmodified/></prop></propfind>",
            Encoding.UTF8, "application/xml");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    HttpRequestMessage Request(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = credentials;
        return request;
    }

    Uri Location(string path) => new(endpoint, string.Join('/', path.Split('/').Select(Uri.EscapeDataString)));

    bool IsSameOrigin(Uri uri) => uri.Scheme == endpoint.Scheme &&
        uri.Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == endpoint.Port;

    static string Join(string folder, string name) => name.Length == 0 ? folder : folder.Length == 0 ? name : folder + "/" + name;

    static async Task<string> Hash(string path, CancellationToken ct)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    static Dictionary<string, string> Hashes(Dictionary<string, SaveFile> files)
        => files.ToDictionary(pair => pair.Key, pair => pair.Value.Hash, StringComparer.OrdinalIgnoreCase);
}
