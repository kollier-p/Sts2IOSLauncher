using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

internal sealed class WebDavFixture : HttpMessageHandler
{
    static readonly XNamespace Dav = "DAV:";
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
    public EntityTagHeaderValue? LastIfMatch { get; private set; }
    public int GetRequests { get; private set; }

    public string Version(string path) => $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Files[path])))}\"";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "dav.example" || request.Headers.Authorization?.Scheme != "Basic")
            throw new InvalidOperationException("The WebDAV request changed its origin or authorization scope.");
        var path = request.RequestUri.AbsolutePath;
        switch (request.Method.Method)
        {
            case "PROPFIND":
                if (!request.Headers.TryGetValues("Depth", out var depths) || depths.Single() != "1")
                    throw new InvalidOperationException("Expected a depth-one directory listing.");
                var children = Files.Keys.Where(file => file.StartsWith(path, StringComparison.Ordinal))
                    .Select(file => file[path.Length..]).Where(name => name.Length != 0)
                    .Select(name => name.Contains('/') ? name[..(name.IndexOf('/') + 1)] : name)
                    .Distinct(StringComparer.Ordinal);
                var body = new XElement(Dav + "multistatus", children.Select(child =>
                {
                    var properties = child.EndsWith('/')
                        ? new XElement(Dav + "prop", new XElement(Dav + "resourcetype", new XElement(Dav + "collection")))
                        : new XElement(Dav + "prop", new XElement(Dav + "resourcetype"),
                            new XElement(Dav + "getetag", Version(path + child)),
                            new XElement(Dav + "getcontentlength", Encoding.UTF8.GetByteCount(Files[path + child])));
                    return new XElement(Dav + "response", new XElement(Dav + "href", path + child),
                        new XElement(Dav + "propstat", properties, new XElement(Dav + "status", "HTTP/1.1 200 OK")));
                }));
                return new(HttpStatusCode.MultiStatus) { Content = new StringContent(body.ToString()) };
            case "GET":
                GetRequests++;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Files[path]) };
                response.Headers.ETag = new EntityTagHeaderValue(Version(path));
                return response;
            case "MKCOL":
                return new(HttpStatusCode.MethodNotAllowed);
            case "PUT":
                LastIfMatch = request.Headers.IfMatch.SingleOrDefault();
                Files[path] = await request.Content!.ReadAsStringAsync(cancellationToken);
                return new(HttpStatusCode.Created);
            default:
                throw new InvalidOperationException($"Unexpected WebDAV method: {request.Method}");
        }
    }
}
