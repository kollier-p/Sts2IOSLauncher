using System.Globalization;
using System.Text.Json;
using Launcher.Core;

namespace Launcher.Steam;

public sealed record WorkshopItem(ulong Id, string Title, Uri? Download, long? Size, ulong? Manifest);

public sealed class WorkshopFiles(HttpClient http)
{
    const string Details = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    public static ulong? ParseId(string input)
    {
        var value = input.Trim();
        if (ParseNumber(value) is ulong direct) return direct;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            !url.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.TrimEnd('/') is not ("/sharedfiles/filedetails" or "/workshop/filedetails")) return null;
        foreach (var parameter in url.Query.TrimStart('?').Split('&'))
            if (parameter.StartsWith("id=", StringComparison.Ordinal)) return ParseNumber(parameter[3..]);
        return null;
    }

    public async Task<WorkshopItem> LookupAsync(ulong id, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["itemcount"] = "1", ["publishedfileids[0]"] = id.ToString(CultureInfo.InvariantCulture)
        });
        using var response = await http.PostAsync(Details, form, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var item = document.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray().Single();
        if (Number(item, "result") != 1 || Number(item, "publishedfileid") != id || Number(item, "consumer_app_id") != Payload.AppId ||
            (item.TryGetProperty("banned", out var banned) && banned.ValueKind == JsonValueKind.True))
            throw new InvalidDataException("The workshop item is unavailable or belongs to another game.");
        var title = String(item, "title");
        var url = String(item, "file_url");
        var size = Number(item, "file_size");
        return new(id, string.IsNullOrWhiteSpace(title) ? $"Workshop {id}" : title,
            PublicTransferUri(url), size is > 0 and <= long.MaxValue ? (long)size.Value : null, Number(item, "hcontent_file"));
    }

    public Task DownloadAsync(WorkshopItem item, string path, CancellationToken ct)
    {
        if (item.Download is null || PublicTransferUri(item.Download.AbsoluteUri) is not { } uri)
            throw new NotSupportedException("Steam did not provide a public download for this item.");
        return ModDownloads.FetchAsync(http, uri, path, item.Size, ct);
    }

    static Uri? PublicTransferUri(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || uri.Port != 443) return null;
        var roots = new[] { "steamusercontent.com", "steamstatic.com", "steamcontent.com", "steamcommunity.com", "akamaihd.net" };
        return roots.Any(root => uri.Host.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase)) ? uri : null;
    }

    static string? String(JsonElement item, string name) => item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static ulong? Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number :
            value.ValueKind == JsonValueKind.String ? ParseNumber(value.GetString()) : null;
    }

    static ulong? ParseNumber(string? input) => ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value != 0 ? value : null;
}
