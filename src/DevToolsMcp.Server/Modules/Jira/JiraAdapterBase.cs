using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DevToolsMcp.Server.Modules.Jira;

/// <summary>Shared Jira REST API v2 plumbing; identical on Cloud and Data Center except search and user references.</summary>
public abstract class JiraAdapterBase(IHttpClientFactory httpClientFactory, Uri baseAddress, AuthenticationHeaderValue authorization)
    : IJiraApiAdapter
{
    protected const string Api = "rest/api/2/";

    /// <summary>Fields fetched for <c>jira_get_issue</c>; everything else (custom fields, changelog, ...) is never requested.</summary>
    internal const string IssueFields = "summary,description,status,issuetype,priority,resolution,assignee,reporter,labels,parent,attachment,subtasks,issuelinks,fixVersions,components,created,updated";

    /// <summary>Fields fetched per search hit.</summary>
    internal const string SearchFields = "summary,status,issuetype,assignee,updated";

    public abstract JiraDeploymentType DeploymentType { get; }

    public Uri SiteBase { get; } = baseAddress;

    public abstract Task<JiraSearchResponse> SearchAsync(string jql, int limit, string? cursor, CancellationToken ct, string fields = SearchFields);

    public abstract Task<IReadOnlyList<JiraUser>> FindUsersAsync(string query, CancellationToken ct);

    public abstract JiraUserRef ToRef(JiraUser user);

    public abstract Task AssignAsync(string key, JiraUser? user, CancellationToken ct);

    public Task<JiraIssue> GetIssueAsync(string key, CancellationToken ct) =>
        GetAsync($"{Api}issue/{Uri.EscapeDataString(key)}?fields={IssueFields}", JiraJsonContext.Default.JiraIssue, ct);

    public Task<JiraCommentPage> GetCommentsAsync(string key, int limit, CancellationToken ct) =>
        GetAsync($"{Api}issue/{Uri.EscapeDataString(key)}/comment?orderBy=-created&maxResults={limit}",
            JiraJsonContext.Default.JiraCommentPage, ct);

    public Task<JiraCommentPage> GetCommentsPageAsync(string key, int startAt, int limit, CancellationToken ct) =>
        GetAsync($"{Api}issue/{Uri.EscapeDataString(key)}/comment?orderBy=created&startAt={startAt}&maxResults={limit}",
            JiraJsonContext.Default.JiraCommentPage, ct);

    public async Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken ct)
    {
        var uri = new Uri(SiteBase, url);
        if (!string.Equals(uri.Host, SiteBase.Host, StringComparison.OrdinalIgnoreCase))
        {
            throw new JiraApiException(HttpStatusCode.BadRequest, $"Refusing to send Jira credentials to {uri.Host}.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("*/*");
        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new JiraApiException(HttpStatusCode.RequestEntityTooLarge, $"Attachment larger than {maxBytes / (1024 * 1024)} MB.");
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public Task<JiraCreated> CreateIssueAsync(JiraCreateFields fields, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Api}issue", new JiraCreateRequest { Fields = fields },
            JiraJsonContext.Default.JiraCreateRequest, JiraJsonContext.Default.JiraCreated, ct);

    public Task UpdateIssueAsync(string key, JiraEditRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"{Api}issue/{Uri.EscapeDataString(key)}", request, JiraJsonContext.Default.JiraEditRequest, ct);

    public Task<JiraUser> GetMyselfAsync(CancellationToken ct) =>
        GetAsync($"{Api}myself", JiraJsonContext.Default.JiraUser, ct);

    public async Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(string key, CancellationToken ct) =>
        (await GetAsync($"{Api}issue/{Uri.EscapeDataString(key)}/transitions", JiraJsonContext.Default.JiraTransitionList, ct)
            .ConfigureAwait(false)).Transitions;

    public Task TransitionAsync(string key, string transitionId, string? resolution, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Api}issue/{Uri.EscapeDataString(key)}/transitions", new JiraTransitionRequest
        {
            Transition = new JiraNamed { Id = transitionId },
            Fields = resolution is null ? null : new JiraTransitionFields { Resolution = new JiraNamed { Name = resolution } },
        }, JiraJsonContext.Default.JiraTransitionRequest, ct);

    public Task AddCommentAsync(string key, string wikiBody, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"{Api}issue/{Uri.EscapeDataString(key)}/comment", new JiraCommentRequest { Body = wikiBody },
            JiraJsonContext.Default.JiraCommentRequest, ct);

    public Task<JiraProject> GetProjectAsync(string projectKey, CancellationToken ct) =>
        GetAsync($"{Api}project/{Uri.EscapeDataString(projectKey)}", JiraJsonContext.Default.JiraProject, ct);

    public async Task<IReadOnlyList<JiraAttachment>> AttachAsync(string key, string fileName, Stream content, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Quoted name/filename: some reverse proxies and older Jira versions reject unquoted multipart parameters.
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"file\"",
            FileName = "\"" + fileName.Replace("\"", "'", StringComparison.Ordinal) + "\"",
            FileNameStar = fileName,
        };
        form.Add(file);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}issue/{Uri.EscapeDataString(key)}/attachments") { Content = form };
        request.Headers.Add("X-Atlassian-Token", "no-check"); // required by Jira's XSRF protection for multipart uploads
        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, JiraJsonContext.Default.ListJiraAttachment, ct).ConfigureAwait(false);
    }

    protected async Task<T> GetAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, typeInfo, ct).ConfigureAwait(false);
    }

    protected async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest body,
        JsonTypeInfo<TRequest> requestInfo, JsonTypeInfo<TResponse> responseInfo, CancellationToken ct)
    {
        using var request = JsonRequest(method, path, body, requestInfo);
        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        return await ReadAsync(response, responseInfo, ct).ConfigureAwait(false);
    }

    /// <summary>For endpoints that answer 204 No Content (edit, assign, transition) or whose body is not needed.</summary>
    protected async Task SendAsync<TRequest>(HttpMethod method, string path, TRequest body, JsonTypeInfo<TRequest> requestInfo, CancellationToken ct)
    {
        using var request = JsonRequest(method, path, body, requestInfo);
        using var response = await SendRawAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage JsonRequest<TRequest>(HttpMethod method, string path, TRequest body, JsonTypeInfo<TRequest> requestInfo)
    {
        // Buffered (Content-Length) rather than chunked: some Data Center reverse proxies reject chunked uploads.
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, requestInfo));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return new HttpRequestMessage(method, path) { Content = content };
    }

    // Base address and credentials live on the adapter, so saving new settings swaps them without a restart.
    private Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = authorization;
        var client = httpClientFactory.CreateClient(JiraModule.HttpClientName);
        client.BaseAddress = SiteBase;
        return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        return await response.Content.ReadFromJsonAsync(typeInfo, ct).ConfigureAwait(false)
            ?? throw new JiraApiException(response.StatusCode, "Empty response from Jira.");
    }

    internal static async Task<JiraApiException> CreateErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? detail = null;
        try
        {
            var error = JsonSerializer.Deserialize(raw, JiraJsonContext.Default.JiraErrorResponse);
            var parts = new List<string>();
            parts.AddRange(error?.ErrorMessages ?? []);
            parts.AddRange((error?.Errors ?? []).Select(e => $"{e.Key}: {e.Value}"));
            if (!string.IsNullOrWhiteSpace(error?.Message))
            {
                parts.Add(error.Message);
            }

            detail = parts.Count > 0 ? string.Join("; ", parts) : null;
        }
        catch (JsonException)
        {
            // not JSON (HTML error page, proxy, ...)
        }

        detail ??= response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "authentication failed; check the Jira credentials",
            HttpStatusCode.Forbidden => "permission denied",
            HttpStatusCode.NotFound => "not found",
            HttpStatusCode.RequestEntityTooLarge => "file too large for this Jira instance",
            _ => Shorten(raw, 200),
        };

        return new JiraApiException(response.StatusCode, $"Jira API {(int)response.StatusCode}: {detail}");
    }

    private static string Shorten(string text, int max)
    {
        var sb = new StringBuilder(Math.Min(text.Length, max));
        var inTag = false;
        foreach (var c in text)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) { sb.Append(char.IsWhiteSpace(c) ? ' ' : c); }
        }

        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max] + "…";
    }
}
