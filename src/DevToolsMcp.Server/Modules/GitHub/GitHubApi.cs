using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DevToolsMcp.Server.Modules.GitHub;

/// <param name="FileName">From Content-Disposition, when the server sends one.</param>
public sealed record GhDownload(byte[] Data, string? MediaType, string? FileName);

public sealed class GitHubApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The small slice of the GitHub REST API (v2022-11-28) the tools use. Works against github.com, GitHub Enterprise
/// Cloud (ghe.com) and GitHub Enterprise Server. Request bodies are written with <see cref="Utf8JsonWriter"/>, which
/// keeps them AOT-safe and lets a PATCH send only the fields that change (including an explicit null).
/// </summary>
public sealed class GitHubApi(IHttpClientFactory httpClientFactory, GitHubOptions options)
{
    private readonly Uri _baseAddress = options.GetApiBaseAddress();
    private readonly AuthenticationHeaderValue _authorization = new("Bearer", options.ApiToken!.Trim());

    public Uri BaseAddress => _baseAddress;

    public string? DefaultRepo { get; } = string.IsNullOrWhiteSpace(options.DefaultRepo) ? null : options.DefaultRepo.Trim();

    public Task<GhUser> GetViewerAsync(CancellationToken ct) =>
        SendAsync(HttpMethod.Get, "user", null, GitHubJsonContext.Default.GhUser, ct)!;

    public Task<GhSearchResult> SearchIssuesAsync(string q, int limit, CancellationToken ct) => SearchIssuesAsync(q, limit, 1, "updated", "desc", ct);

    public Task<GhSearchResult> SearchIssuesAsync(string q, int limit, int page, string sort, string order, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"search/issues?q={Uri.EscapeDataString(q)}&per_page={limit}&sort={sort}&order={order}"
            + (page > 1 ? $"&page={page}" : string.Empty),
            null, GitHubJsonContext.Default.GhSearchResult, ct)!;

    /// <summary>Host of the web UI (github.com for api.github.com): where issue attachments live.</summary>
    public string WebHost => _baseAddress.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase) ? _baseAddress.Host[4..] : _baseAddress.Host;

    public Task<List<GhMilestone>> GetMilestonesAsync(string repo, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/milestones?state=all&per_page=100", null, GitHubJsonContext.Default.ListGhMilestone, ct)!;

    public Task<GhMilestone> CreateMilestoneAsync(string repo, string title, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"repos/{repo}/milestones", w => w.WriteString("title", title), GitHubJsonContext.Default.GhMilestone, ct)!;

    public Task<List<GhIssue>> GetSubIssuesAsync(string repo, int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/issues/{number}/sub_issues?per_page=100", null, GitHubJsonContext.Default.ListGhIssue, ct)!;

    /// <param name="childId">The sub-issue's database id (<see cref="GhIssue.Id"/>), not its number.</param>
    public Task AddSubIssueAsync(string repo, int parentNumber, long childId, CancellationToken ct) =>
        SendAsync<GhIssue>(HttpMethod.Post, $"repos/{repo}/issues/{parentNumber}/sub_issues", w => w.WriteNumber("sub_issue_id", childId), null, ct);

    public Task<GhRepo> GetRepoAsync(string repo, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}", null, GitHubJsonContext.Default.GhRepo, ct)!;

    public Task<GhRef> GetBranchRefAsync(string repo, string branch, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/git/ref/heads/{Uri.EscapeDataString(branch)}", null, GitHubJsonContext.Default.GhRef, ct)!;

    public Task CreateBranchAsync(string repo, string branch, string sha, CancellationToken ct) =>
        SendAsync<GhRef>(HttpMethod.Post, $"repos/{repo}/git/refs", w =>
        {
            w.WriteString("ref", "refs/heads/" + branch);
            w.WriteString("sha", sha);
        }, null, ct);

    /// <summary>Creates a file on a branch (contents API). Fails with 422 when the file already exists.</summary>
    public Task<GhContentResult> CreateFileAsync(string repo, string path, string branch, byte[] content, string message, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"repos/{repo}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}", w =>
        {
            w.WriteString("message", message);
            w.WriteString("branch", branch);
            w.WriteBase64String("content", content);
        }, GitHubJsonContext.Default.GhContentResult, ct)!;

    /// <summary>
    /// Downloads an issue attachment. The token is sent only to the GitHub web host itself (private-repository
    /// attachments need it); redirects to storage hosts never carry it.
    /// </summary>
    public async Task<GhDownload> DownloadAsync(Uri url, long maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (string.Equals(url.Host, WebHost, StringComparison.OrdinalIgnoreCase) && url.Scheme == Uri.UriSchemeHttps)
        {
            request.Headers.Authorization = _authorization;
        }

        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("*/*");
        var client = httpClientFactory.CreateClient(GitHubModule.HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(response.StatusCode, $"Download {(int)response.StatusCode}: {url}");
        }

        var data = await Core.Http.BoundedContent.ReadAsBytesAsync(response.Content, maxBytes,
            () => new GitHubApiException(HttpStatusCode.RequestEntityTooLarge, $"Attachment larger than {maxBytes / (1024 * 1024)} MB: {url}"), ct).ConfigureAwait(false);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        return new GhDownload(data, response.Content.Headers.ContentType?.MediaType, name);
    }

    /// <summary>Issues (and pull requests) by <paramref name="creator"/> updated since <paramref name="since"/>, newest first.</summary>
    public Task<List<GhIssue>> ListIssuesByCreatorAsync(string repo, string creator, DateTimeOffset since, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/issues?creator={Uri.EscapeDataString(creator)}&state=all&sort=created&direction=desc&per_page=100"
            + $"&since={Uri.EscapeDataString(since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture))}",
            null, GitHubJsonContext.Default.ListGhIssue, ct)!;

    public Task<GhIssue> GetIssueAsync(string repo, int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/issues/{number}", null, GitHubJsonContext.Default.GhIssue, ct)!;

    public Task<List<GhComment>> GetCommentsAsync(string repo, int number, int perPage, int page, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/issues/{number}/comments?per_page={perPage}&page={page}",
            null, GitHubJsonContext.Default.ListGhComment, ct)!;

    public Task<List<GhLabel>> GetLabelsAsync(string repo, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/labels?per_page=100", null, GitHubJsonContext.Default.ListGhLabel, ct)!;

    public Task<GhIssue> CreateIssueAsync(string repo, Action<Utf8JsonWriter> fields, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"repos/{repo}/issues", fields, GitHubJsonContext.Default.GhIssue, ct)!;

    public Task<GhIssue> UpdateIssueAsync(string repo, int number, Action<Utf8JsonWriter> fields, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"repos/{repo}/issues/{number}", fields, GitHubJsonContext.Default.GhIssue, ct)!;

    public Task AddLabelsAsync(string repo, int number, IReadOnlyList<string> labels, CancellationToken ct) =>
        SendAsync<GhIssue>(HttpMethod.Post, $"repos/{repo}/issues/{number}/labels", w => WriteArray(w, "labels", labels), null, ct);

    public async Task RemoveLabelAsync(string repo, int number, string label, CancellationToken ct)
    {
        try
        {
            await SendAsync<GhIssue>(HttpMethod.Delete, $"repos/{repo}/issues/{number}/labels/{Uri.EscapeDataString(label)}", null, null, ct)
                .ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // The label was not on the issue: nothing to remove.
        }
    }

    public Task AddAssigneesAsync(string repo, int number, IReadOnlyList<string> logins, CancellationToken ct) =>
        SendAsync<GhIssue>(HttpMethod.Post, $"repos/{repo}/issues/{number}/assignees", w => WriteArray(w, "assignees", logins), null, ct);

    public Task RemoveAssigneesAsync(string repo, int number, IReadOnlyList<string> logins, CancellationToken ct) =>
        SendAsync<GhIssue>(HttpMethod.Delete, $"repos/{repo}/issues/{number}/assignees", w => WriteArray(w, "assignees", logins), null, ct);

    public Task<GhComment> AddCommentAsync(string repo, int number, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"repos/{repo}/issues/{number}/comments", w => w.WriteString("body", body),
            GitHubJsonContext.Default.GhComment, ct)!;

    public Task<GhComment> UpdateCommentAsync(string repo, long commentId, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"repos/{repo}/issues/comments/{commentId}", w => w.WriteString("body", body),
            GitHubJsonContext.Default.GhComment, ct)!;

    private static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var v in values)
        {
            writer.WriteStringValue(v);
        }

        writer.WriteEndArray();
    }

    /// <param name="fields">Writes the properties of the JSON request object; null sends no body.</param>
    /// <param name="responseInfo">null ignores the response body.</param>
    private async Task<T?> SendAsync<T>(HttpMethod method, string path, Action<Utf8JsonWriter>? fields, JsonTypeInfo<T>? responseInfo, CancellationToken ct)
        where T : class
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = _authorization;
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        if (fields is not null)
        {
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                fields(writer);
                writer.WriteEndObject();
            }

            request.Content = new ByteArrayContent(buffer.GetBuffer(), 0, (int)buffer.Length); // no copy
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        var client = httpClientFactory.CreateClient(GitHubModule.HttpClientName);
        client.BaseAddress = _baseAddress; // per adapter, so saving new settings takes effect without a restart
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await CreateErrorAsync(response, ct).ConfigureAwait(false);
        }

        if (responseInfo is null)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync(responseInfo, ct).ConfigureAwait(false)
            ?? throw new GitHubApiException(response.StatusCode, "Empty response from GitHub.");
    }

    private static async Task<GitHubApiException> CreateErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? detail = null;
        try
        {
            var error = JsonSerializer.Deserialize(raw, GitHubJsonContext.Default.GhErrorResponse);
            detail = error?.Message;
            if (error?.Errors is [var first, ..])
            {
                var specific = first.Message ?? string.Join(' ', new[] { first.Field, first.Code }.Where(s => !string.IsNullOrEmpty(s)));
                if (!string.IsNullOrEmpty(specific))
                {
                    detail = detail is null ? specific : $"{detail} ({specific})";
                }
            }
        }
        catch (JsonException)
        {
            // not JSON (proxy or HTML error page)
        }

        detail ??= response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Bad credentials; check the GitHub token",
            HttpStatusCode.Forbidden => "permission denied or rate limited",
            HttpStatusCode.NotFound => "not found (or no access)",
            _ => raw.Length <= 200 ? raw : raw[..200] + "…",
        };

        if (response.StatusCode == HttpStatusCode.NotFound && detail == "Not Found")
        {
            detail = "Not Found (missing, or the token has no access)";
        }

        return new GitHubApiException(response.StatusCode, $"GitHub API {(int)response.StatusCode}: {detail}");
    }
}
