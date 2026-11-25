using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>Shared HTTP plumbing and the v1 CQL search, which is identical on Cloud and Data Center.</summary>
public abstract partial class ConfluenceAdapterBase(IHttpClientFactory httpClientFactory, Uri baseAddress, AuthenticationHeaderValue authorization)
    : IConfluenceApiAdapter
{
    private const int MaxExcerpt = 160;

    public abstract ConfluenceDeploymentType DeploymentType { get; }

    /// <summary>Relative prefix of the v1 REST API: <c>wiki/rest/api/</c> (Cloud) or <c>rest/api/</c> (Data Center).</summary>
    protected abstract string RestApiPrefix { get; }

    /// <summary>HttpClient base address (always ends with '/').</summary>
    protected Uri BaseAddress { get; } = baseAddress;

    // Base address and credentials live on the adapter, so saving new settings swaps them without a restart.
    private HttpClient CreateClient()
    {
        var client = httpClientFactory.CreateClient(ConfluenceModule.HttpClientName);
        client.BaseAddress = BaseAddress;
        return client;
    }

    public async Task<IReadOnlyList<ConfluenceSearchHit>> SearchAsync(string cql, int limit, CancellationToken ct)
    {
        // /rest/api/search is the CQL search endpoint that returns excerpts and lastModified in a single call.
        var path = $"{RestApiPrefix}search?cql={Uri.EscapeDataString(cql)}&limit={limit}&excerpt=indexed&expand=content.space";
        var response = await GetAsync(path, ConfluenceJsonContext.Default.V1SearchResponse, ct).ConfigureAwait(false);

        var hits = new List<ConfluenceSearchHit>(response.Results.Count);
        foreach (var r in response.Results)
        {
            if (r.Content?.Id is not { } id)
            {
                continue; // spaces, users, ...
            }

            hits.Add(new ConfluenceSearchHit(
                id,
                CleanText(r.Content.Title ?? r.Title ?? string.Empty, int.MaxValue),
                r.Content.Space?.Key ?? SpaceKeyFromUrl(r.ResultGlobalContainer?.DisplayUrl),
                DateTimeOffset.TryParse(r.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
                    ? modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : null,
                string.IsNullOrWhiteSpace(r.Excerpt) ? null : CleanText(r.Excerpt, MaxExcerpt)));
        }

        return hits;
    }

    public abstract Task<ConfluencePage> GetPageAsync(string pageId, bool includeBody, CancellationToken ct);

    public abstract Task<IReadOnlyList<ConfluencePageRef>> GetChildrenAsync(string pageId, int limit, CancellationToken ct);

    public abstract Task<ConfluencePageRef> CreatePageAsync(string spaceKey, string? parentId, string title, string storageBody, CancellationToken ct);

    public abstract Task UpdatePageAsync(string pageId, string title, string storageBody, int newVersion, string? versionMessage, CancellationToken ct);

    protected async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = authorization;
        using var response = await CreateClient().SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, typeInfo, ct).ConfigureAwait(false);
    }

    protected async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest body,
        JsonTypeInfo<TRequest> requestInfo, JsonTypeInfo<TResponse> responseInfo, CancellationToken ct)
    {
        // Buffered (Content-Length) rather than chunked: some Data Center reverse proxies reject chunked uploads.
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, requestInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = authorization;
        using var response = await CreateClient().SendAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, responseInfo, ct).ConfigureAwait(false);
    }

    protected static string WebUrl(ConfluenceLinks? links, Uri fallbackBase)
    {
        if (string.IsNullOrEmpty(links?.Webui))
        {
            return string.Empty;
        }

        var root = string.IsNullOrEmpty(links.Base) ? fallbackBase.ToString() : links.Base;
        return root.TrimEnd('/') + "/" + links.Webui.TrimStart('/');
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync(typeInfo, ct).ConfigureAwait(false)
            ?? throw new ConfluenceApiException(response.StatusCode, "Empty response from Confluence.");
    }

    private static async Task<ConfluenceApiException> CreateErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? detail = null;
        try
        {
            var error = JsonSerializer.Deserialize(raw, ConfluenceJsonContext.Default.ConfluenceErrorResponse);
            detail = error?.Message ?? (error?.Errors is [var first, ..] ? first.Detail ?? first.Title : null);
        }
        catch (JsonException)
        {
            // not JSON (HTML error page, proxy, ...)
        }

        detail ??= response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "authentication failed; check the Confluence credentials",
            HttpStatusCode.Forbidden => "permission denied",
            HttpStatusCode.NotFound => "not found",
            _ => CleanText(raw, 200),
        };

        return new ConfluenceApiException(response.StatusCode, $"Confluence API {(int)response.StatusCode}: {detail}");
    }

    internal static string? SpaceKeyFromUrl(string? displayUrl)
    {
        if (string.IsNullOrEmpty(displayUrl))
        {
            return null;
        }

        var m = SpaceUrl().Match(displayUrl);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    /// <summary>Strips highlight markers and tags, decodes entities, collapses whitespace and truncates.</summary>
    internal static string CleanText(string text, int max)
    {
        text = text.Replace("@@@hl@@@", string.Empty).Replace("@@@endhl@@@", string.Empty);
        text = WebUtility.HtmlDecode(Tags().Replace(text, " "));
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"/(?:spaces|display)/([^/?#]+)")]
    private static partial Regex SpaceUrl();
}
