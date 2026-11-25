using System.Net;
using System.Text;

namespace DevToolsMcp.Tests.Confluence;

/// <summary>Records requests and replies with canned JSON by "METHOD path" (path includes the query).</summary>
internal sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly Uri _baseAddress;
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);

    public FakeHttp(Uri baseAddress) => _baseAddress = baseAddress;

    public List<(HttpMethod Method, string PathAndQuery, string? Body)> Requests { get; } = [];

    public FakeHttp On(string method, string pathAndQuery, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[$"{method} {pathAndQuery}"] = (status, json);
        return this;
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = _baseAddress };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            Requests.Add((request.Method, path, body)); // requests may run in parallel
        }

        if (!_routes.TryGetValue($"{request.Method.Method} {path}", out var route))
        {
            route = (HttpStatusCode.NotFound, "{\"message\":\"no route " + path + "\"}");
        }

        return new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
    }
}
