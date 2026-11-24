using System.Net;

internal sealed class NexusFixture : HttpMessageHandler
{
    public bool Downloaded { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "cdn.nexusmods.com")
        {
            if (request.Headers.Contains("apikey")) throw new InvalidOperationException("The API key reached the download mirror.");
            Downloaded = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
        if (uri.Host != "api.nexusmods.com" || request.Headers.GetValues("apikey").Single() != "fixture")
            throw new InvalidOperationException("The Nexus API request has the wrong origin or credentials.");
        var content = uri.AbsolutePath.Split('/').Last() switch
        {
            "validate.json" => """{"name":"fixture"}""",
            "42.json" => """{"mod_id":42,"name":"Fixture","version":"1.0","summary":"Test mod"}""",
            "files.json" => """{"files":[{"file_id":37,"name":"Fixture.zip","version":"1.1","is_primary":true}]}""",
            "download_link.json" when uri.Query == "?key=public%2Bkey&expires=1234" =>
                """[{"URI":"https://mirror.example/forbidden"},{"URI":"https://cdn.nexusmods.com/mods/fixture.zip"}]""",
            _ => throw new InvalidOperationException("Unexpected Nexus request.")
        };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
    }
}
