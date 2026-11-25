using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Launcher.Core;

namespace Launcher.Steam;

public sealed record WorkshopItem(ulong Id, string Title, Uri? Download, long? Size, ulong? Manifest, uint Updated);
public sealed record WorkshopListing(ulong Id, string Title, string Summary, Uri? Thumbnail);
public sealed record WorkshopPage(int Index, int Pages, IReadOnlyList<WorkshopListing> Items);
public enum WorkshopSort { Popular, Latest, Updated, Subscribed }

public sealed class WorkshopFiles(HttpClient http)
{
    const string Details = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    const string Community = "https://steamcommunity.com/workshop/browse/";

    public async Task<WorkshopPage> BrowseAsync(WorkshopSort sort, int page, string search, int days, CancellationToken ct)
    {
        if (page < 1 || search.Length > 128) throw new ArgumentException("Invalid Workshop page or search term.");
        if (days is not (7 or 30 or 90 or 180 or 365 or -1)) throw new ArgumentOutOfRangeException(nameof(days));
        var order = sort switch
        {
            WorkshopSort.Popular => "trend",
            WorkshopSort.Latest => "mostrecent",
            WorkshopSort.Updated => "lastupdated",
            WorkshopSort.Subscribed => "totaluniquesubscribers",
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };
        var url = $"{Community}?appid={Payload.AppId}&section=readytouseitems&browsesort={order}&p={page}&days={days}&searchtext={Uri.EscapeDataString(search)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        const int maximum = 4 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Workshop page exceeds the supported size.");
        using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Workshop page exceeds the supported size.");
            output.Write(buffer, 0, read);
        }
        return ParsePage(System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length));
    }

    public static WorkshopPage ParsePage(string html)
    {
        var block = Regex.Match(html, @"<script\b[^>]*\bid=[""']valve-ssr-data[""'][^>]*>(.*?)</script\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        if (!block.Success) throw new InvalidDataException("Steam Community returned no Workshop browsing data.");
        using var outer = JsonDocument.Parse(block.Groups[1].Value);
        var inner = outer.RootElement.GetProperty("renderContext").GetProperty("queryData");
        using var json = JsonDocument.Parse(inner.ValueKind == JsonValueKind.String ? inner.GetString()! : inner.GetRawText());
        foreach (var query in json.RootElement.GetProperty("queries").EnumerateArray())
        {
            var key = query.GetProperty("queryKey");
            if (key.ValueKind != JsonValueKind.Array || key.GetArrayLength() < 2 || key[0].GetString() != "workshop_browse" ||
                Number(key[1], "appid") != Payload.AppId) continue;
            var data = query.GetProperty("state").GetProperty("data");
            if (Number(data, "eresult") != 1) throw new IOException("Steam Community did not return Workshop results.");
            var index = Number(data, "current_page");
            var total = Number(data, "total_pages");
            if (index is null or 0 or > int.MaxValue || total is null or > int.MaxValue)
                throw new InvalidDataException("Steam Community returned invalid Workshop pagination.");
            var items = new List<WorkshopListing>();
            var seen = new HashSet<ulong>();
            foreach (var item in data.GetProperty("results").EnumerateArray())
            {
                if (Number(item, "consumer_appid") != Payload.AppId || Number(item, "publishedfileid") is not ulong id ||
                    id == 0 || !seen.Add(id)) continue;
                var title = String(item, "title");
                var summary = String(item, "short_description") ?? "";
                var thumbnail = PublicTransferUri(String(item, "preview_url"));
                items.Add(new(id, string.IsNullOrWhiteSpace(title) ? $"Workshop {id}" : title,
                    new string(summary.Where(character => !char.IsControl(character)).Take(160).ToArray()), thumbnail));
            }
            return new((int)index.Value, (int)total.Value, items);
        }
        throw new InvalidDataException("Steam Community did not return a page for Slay the Spire 2.");
    }

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
        var items = await LookupAsync([id], ct);
        return items.TryGetValue(id, out var item) ? item :
            throw new InvalidDataException("The Workshop item is unavailable or belongs to another game.");
    }

    public async Task<IReadOnlyDictionary<ulong, WorkshopItem>> LookupAsync(IEnumerable<ulong> ids, CancellationToken ct)
    {
        var results = new Dictionary<ulong, WorkshopItem>();
        foreach (var page in ids.Distinct().Chunk(50))
        {
            var fields = new Dictionary<string, string> { ["itemcount"] = page.Length.ToString(CultureInfo.InvariantCulture) };
            for (var i = 0; i < page.Length; i++)
                fields.Add($"publishedfileids[{i}]", page[i].ToString(CultureInfo.InvariantCulture));
            using var form = new FormUrlEncodedContent(fields);
            using var response = await http.PostAsync(Details, form, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            foreach (var item in document.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
            {
                if (Number(item, "result") != 1 || Number(item, "publishedfileid") is not ulong id || !page.Contains(id) ||
                    Number(item, "consumer_app_id") != Payload.AppId ||
                    (item.TryGetProperty("banned", out var banned) && banned.ValueKind == JsonValueKind.True)) continue;
                var title = String(item, "title");
                var size = Number(item, "file_size");
                var time = Number(item, "time_updated");
                results.TryAdd(id, new(id, string.IsNullOrWhiteSpace(title) ? $"Workshop {id}" : title,
                    PublicTransferUri(String(item, "file_url")), size is > 0 and <= long.MaxValue ? (long)size.Value : null,
                    Number(item, "hcontent_file"), time is > 0 and <= uint.MaxValue ? (uint)time.Value : 0));
            }
        }
        return results;
    }

    public Task DownloadAsync(WorkshopItem item, string path, CancellationToken ct)
    {
        if (item.Download is null || PublicTransferUri(item.Download.AbsoluteUri) is not { } uri)
            throw new NotSupportedException("Steam did not provide a public download for this item.");
        return ModDownloads.FetchAsync(http, uri, path, item.Size, address => PublicTransferUri(address.AbsoluteUri) is not null, ct);
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
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number) ? number :
            value.ValueKind == JsonValueKind.String ? ParseNumber(value.GetString()) : null;
    }

    static ulong? ParseNumber(string? input) => ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value != 0 ? value : null;
}
