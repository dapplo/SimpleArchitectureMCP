using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DevToolsMcp.Server.Modules.GitHub;

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

    public Task<GhSearchResult> SearchIssuesAsync(string q, int limit, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"search/issues?q={Uri.EscapeDataString(q)}&per_page={limit}&sort=updated&order=desc",
            null, GitHubJsonContext.Default.GhSearchResult, ct)!;

    public Task<GhIssue> GetIssueAsync(string repo, int number, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/issues/{number}", null, GitHubJsonContext.Default.GhIssue, ct)!;

    public Task<List<GhComment>> GetCommentsAsync(string repo, int number, int page, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"repos/{repo}/issues/{number}/comments?per_page=100&page={page}",
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

            request.Content = new ByteArrayContent(buffer.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        var client = httpClientFactory.CreateClient(GitHubModule.HttpClientName);
        client.BaseAddress = _baseAddress; // per adapter, so saving new settings takes effect without a restart
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

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
