using System.Globalization;
using System.Text.Json;

namespace Launcher.Core;

public sealed record NexusMod(ulong Id, string Name, string Summary, string Version);
public sealed record NexusModFile(ulong Id, string Name, string Version, bool Primary);
public sealed record NexusDownloadToken(ulong ModId, ulong FileId, string Key, ulong Expires)
{
    public override string ToString() => "Nexus download authorization [redacted]";
}

public sealed class NexusMods(HttpClient http, string apiKey)
{
    const string Domain = "slaythespire2";
    const string Api = "https://api.nexusmods.com/v1/";
    const string Mods = "games/" + Domain + "/mods/";

    public static ulong? ParseModId(string input)
    {
        var text = input.Trim();
        if (Number(text) is ulong id) return id;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host is not ("nexusmods.com" or "www.nexusmods.com")) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        return parts.Length == 3 && parts[0] == Domain && parts[1] == "mods" ? Number(parts[2]) : null;
    }

    public static NexusDownloadToken? ParseNxm(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "nxm" || uri.Host != Domain ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 4 || parts[0] != "mods" || parts[2] != "files" ||
            Number(parts[1]) is not ulong mod || Number(parts[3]) is not ulong file) return null;
        string? key = null;
        ulong? expires = null;
        foreach (var value in uri.Query.TrimStart('?').Split('&'))
        {
            if (value.StartsWith("key=", StringComparison.Ordinal)) key = Uri.UnescapeDataString(value[4..]);
            if (value.StartsWith("expires=", StringComparison.Ordinal)) expires = Number(value[8..]);
        }
        return string.IsNullOrEmpty(key) || expires is null ? null : new(mod, file, key, expires.Value);
    }

    public async Task ValidateAsync(CancellationToken ct)
    {
        using var document = await Get("users/validate.json", ct);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Nexus did not validate the API key.");
    }

    public async Task<IReadOnlyList<NexusMod>> SearchAsync(string query, CancellationToken ct)
    {
        if (ParseModId(query) is ulong id) return [await LookupAsync(id, ct)];
        var mods = new Dictionary<ulong, NexusMod>();
        foreach (var feed in new[] { "trending", "latest_added", "latest_updated" })
        {
            using var document = await Get(Mods + feed + ".json", ct);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var mod = ReadMod(item);
                mods.TryAdd(mod.Id, mod);
            }
        }
        return mods.Values.Where(mod => mod.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            mod.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public async Task<NexusMod> LookupAsync(ulong id, CancellationToken ct)
    {
        using var document = await Get(Mods + id + ".json", ct);
        var mod = ReadMod(document.RootElement);
        if (mod.Id != id) throw new InvalidDataException("Nexus returned another mod.");
        return mod;
    }

    public async Task<IReadOnlyList<NexusModFile>> FilesAsync(ulong id, CancellationToken ct)
    {
        using var document = await Get(Mods + id + "/files.json", ct);
        return document.RootElement.GetProperty("files").EnumerateArray().Select(item => new NexusModFile(
            Id(item, "file_id"), Text(item, "name"), Text(item, "version"),
            item.TryGetProperty("is_primary", out var primary) && primary.ValueKind == JsonValueKind.True))
            .OrderByDescending(file => file.Primary).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task DownloadAsync(ulong mod, ulong file, NexusDownloadToken? authorization, string output, CancellationToken ct)
    {
        var url = $"{Mods}{mod}/files/{file}/download_link.json";
        if (authorization is not null)
        {
            if (authorization.ModId != mod || authorization.FileId != file) throw new InvalidDataException("The download link names another file.");
            url += $"?key={Uri.EscapeDataString(authorization.Key)}&expires={authorization.Expires}";
        }
        using var document = await Get(url, ct);
        var download = document.RootElement.EnumerateArray().Select(link => Text(link, "URI"))
            .Select(Transfer).FirstOrDefault(link => link is not null) ?? throw new IOException("Nexus returned no HTTPS download mirror.");
        await ModDownloads.FetchAsync(http, download, output, null, ct);
    }

    async Task<JsonDocument> Get(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.Add("apikey", apiKey);
        request.Headers.Add("Application-Name", "STS2 iPad Launcher");
        request.Headers.Add("Application-Version", "1.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    static Uri? Transfer(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps ||
            address.UserInfo.Length != 0 || address.Fragment.Length != 0 || address.Port != 443) return null;
        return address.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
               address.Host.EndsWith(".nexus-cdn.com", StringComparison.OrdinalIgnoreCase) ? address : null;
    }

    static NexusMod ReadMod(JsonElement item) => new(Id(item, "mod_id"), Text(item, "name"), Text(item, "summary"), Text(item, "version"));
    static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : "";
    static ulong Id(JsonElement item, string name)
    {
        var value = item.GetProperty(name);
        var id = value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number :
            value.ValueKind == JsonValueKind.String ? Number(value.GetString()) ?? 0 : 0;
        return id != 0 ? id : throw new InvalidDataException("Invalid Nexus identifier.");
    }
    static ulong? Number(string? value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id != 0 ? id : null;
}
