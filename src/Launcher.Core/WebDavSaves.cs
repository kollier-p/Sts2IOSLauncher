using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Launcher.Core;

public sealed record WebDavSettings(string Endpoint, string User, string Password, string Slot, bool IncludeSettings)
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

public sealed class SaveSyncConflictException(int count) : IOException("The local and remote saves both changed.")
{
    public int Count { get; } = count;
}

public sealed record SaveSyncResult(int Transferred, int Compared);

public sealed class WebDavSaves(HttpClient http, WebDavSettings settings)
{
    static readonly HttpMethod PropFind = new("PROPFIND");
    static readonly HttpMethod MakeCollection = new("MKCOL");
    static readonly XNamespace Dav = "DAV:";
    readonly Uri endpoint = settings.BaseUri();
    readonly string slot = $"SlayTheSpire2/saves/{settings.Slot}";
    readonly AuthenticationHeaderValue? credentials = settings.User.Length == 0 ? null : new("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.User}:{settings.Password}")));

    sealed record SaveFile(string Relative, string File, string Hash, EntityTagHeaderValue? ETag = null, DateTimeOffset? Modified = null);

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
        Directory.CreateDirectory(baselineRoot);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}\n{settings.User}\n{slot}\n{profileKey}")));
        var baselineFile = Payload.SafePath(baselineRoot, key + ".json");
        var baseline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(baselineFile))
        {
            using var stream = File.OpenRead(baselineFile);
            var previous = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: ct);
            if (previous is not null) baseline = new(previous, StringComparer.OrdinalIgnoreCase);
        }

        var scratch = Directory.CreateTempSubdirectory("sts2-webdav-").FullName;
        try
        {
            var local = await ReadLocal(localRoot, ct);
            var remote = await ReadRemote(scratch, ct);
            var source = upload ? local : remote;
            var destination = upload ? remote : local;
            var changes = new List<SaveFile>();
            var conflicts = 0;
            foreach (var file in source.Values)
            {
                if (!destination.TryGetValue(file.Relative, out var other)) { changes.Add(file); continue; }
                if (file.Hash == other.Hash) { baseline[file.Relative] = file.Hash; continue; }
                if (overwriteConflicts || (baseline.TryGetValue(file.Relative, out var agreed) && other.Hash == agreed))
                    changes.Add(file);
                else if (!baseline.TryGetValue(file.Relative, out agreed) || file.Hash != agreed)
                    conflicts++;
            }
            if (conflicts != 0) throw new SaveSyncConflictException(conflicts);

            if (upload)
            {
                var prepared = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    remote.TryGetValue(file.Relative, out var previous);
                    var path = $"{slot}/{previous?.Relative ?? file.Relative}";
                    await EnsureCollections(path[..path.LastIndexOf('/')], prepared, ct);
                    using var content = new StreamContent(File.OpenRead(file.File));
                    using var request = Request(HttpMethod.Put, Location(path));
                    request.Content = content;
                    if (previous is null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                    else if (previous.ETag is { IsWeak: false }) request.Headers.IfMatch.Add(previous.ETag);
                    else if (previous.Modified is not null) request.Headers.IfUnmodifiedSince = previous.Modified;
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    baseline[file.Relative] = file.Hash;
                }
            }
            else
            {
                var files = changes.ToDictionary(file => local.TryGetValue(file.Relative, out var item) ? item.Relative : file.Relative,
                    file => file.File, StringComparer.Ordinal);
                new SaveSnapshots(localRoot, snapshotRoot).ApplyDownloadedFiles(files, ct);
                foreach (var file in changes) baseline[file.Relative] = file.Hash;
            }
            WriteBaseline(baselineFile, baseline);
            return new(changes.Count, source.Count);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    async Task<Dictionary<string, SaveFile>> ReadLocal(string root, CancellationToken ct)
    {
        var result = new Dictionary<string, SaveFile>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        var folders = new Stack<string>();
        folders.Push("");
        while (folders.TryPop(out var folder))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder.Length == 0 ? root : Payload.SafePath(root, folder)))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Join(folder, Path.GetFileName(path));
                if (SaveFileRules.Visit(relative) && Directory.Exists(path))
                {
                    Payload.SafePath(root, relative);
                    folders.Push(relative);
                }
                else if (SaveFileRules.Include(relative, settings.IncludeSettings) && File.Exists(path))
                {
                    var file = Payload.SafePath(root, relative);
                    result.Add(relative, new(relative, file, await Hash(file, ct)));
                }
            }
        }
        return result;
    }

    async Task<Dictionary<string, SaveFile>> ReadRemote(string scratch, CancellationToken ct)
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
                    using var request = Request(HttpMethod.Get, Location($"{slot}/{relative}"));
                    using var download = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    download.EnsureSuccessStatusCode();
                    if (download.Content.Headers.ContentLength is > 0 and var length)
                        PayloadStorage.EnsureCapacity(scratch, length);
                    var file = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
                    await using (var output = File.Create(file))
                        await download.Content.CopyToAsync(output, ct);
                    result.Add(relative, new(relative, file, await Hash(file, ct),
                        download.Headers.ETag, download.Content.Headers.LastModified));
                }
            }
        }
        return result;
    }

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
        request.Content = new StringContent("<propfind xmlns=\"DAV:\"><prop><resourcetype/></prop></propfind>", Encoding.UTF8, "application/xml");
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

    static void WriteBaseline(string path, Dictionary<string, string> baseline)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, baseline);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
