namespace Launcher.Core;

public static class ModDownloads
{
    public static async Task FetchAsync(HttpClient http, Uri uri, string path, long? expected, CancellationToken ct)
    {
        const long MaximumArchiveBytes = 16L * 1024 * 1024 * 1024;
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        expected ??= response.Content.Headers.ContentLength;
        if (expected > MaximumArchiveBytes) throw new InvalidDataException("The mod archive exceeds the supported size.");
        if (expected is > 0) PayloadStorage.EnsureCapacity(Path.GetDirectoryName(path)!, expected.Value);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            total += count;
            if (total > (expected ?? MaximumArchiveBytes)) throw new InvalidDataException("Mod download exceeds its expected size.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        if (total == 0 || (expected is not null && total != expected.Value)) throw new InvalidDataException("Incomplete mod download.");
    }
}
