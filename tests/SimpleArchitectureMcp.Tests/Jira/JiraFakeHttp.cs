using System.Net;
using System.Text;

namespace SimpleArchitectureMcp.Tests.Jira;

/// <summary>Records requests (with headers) and replies with canned JSON by "METHOD path" (path includes the query).</summary>
internal sealed class JiraFakeHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);

    public sealed record Recorded(HttpMethod Method, string PathAndQuery, string? Body, string? ContentType, IReadOnlyDictionary<string, string> Headers);

    public List<Recorded> Requests { get; } = [];

    public JiraFakeHttp On(string method, string pathAndQuery, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[$"{method} {pathAndQuery}"] = (status, json);
        return this;
    }

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Requests)
        {
            Requests.Add(new Recorded(request.Method, path, body, request.Content?.Headers.ContentType?.MediaType, headers)); // issue + comments run in parallel
        }

        if (!_routes.TryGetValue($"{request.Method.Method} {path}", out var route))
        {
            route = (HttpStatusCode.NotFound, "{\"errorMessages\":[\"no route " + path + "\"]}");
        }

        return new HttpResponseMessage(route.Status) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") };
    }
}
