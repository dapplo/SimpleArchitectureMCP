using System.Net;

namespace DevToolsMcp.Server.Modules.Jira;

/// <summary>
/// Jira REST API v2 calls. Cloud and Data Center share almost everything; they differ in search
/// (<c>/search/jql</c> vs <c>/search</c>) and in how users are identified (<c>accountId</c> vs <c>name</c>).
/// Business rules (validation, user resolution, Markdown conversion, output shaping) live in <see cref="JiraService"/>.
/// </summary>
public interface IJiraApiAdapter
{
    JiraDeploymentType DeploymentType { get; }

    /// <summary>Site root used for <c>browse/KEY</c> links (always ends with '/').</summary>
    Uri SiteBase { get; }

    /// <param name="cursor">null for the first page; then the value from <see cref="JiraService.NextCursor"/>.</param>
    /// <param name="fields">Comma-separated fields per hit.</param>
    Task<JiraSearchResponse> SearchAsync(string jql, int limit, string? cursor, CancellationToken ct, string fields = JiraAdapterBase.SearchFields);

    Task<JiraIssue> GetIssueAsync(string key, CancellationToken ct);

    /// <summary>The newest <paramref name="limit"/> comments, newest first.</summary>
    Task<JiraCommentPage> GetCommentsAsync(string key, int limit, CancellationToken ct);

    /// <summary>Comments oldest first, from <paramref name="startAt"/>.</summary>
    Task<JiraCommentPage> GetCommentsPageAsync(string key, int startAt, int limit, CancellationToken ct);

    /// <summary>Downloads an attachment's <c>content</c> URL (same host only, so credentials never leave the site).</summary>
    Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken ct);

    Task<JiraCreated> CreateIssueAsync(JiraCreateFields fields, CancellationToken ct);

    Task UpdateIssueAsync(string key, JiraEditRequest request, CancellationToken ct);

    Task<JiraUser> GetMyselfAsync(CancellationToken ct);

    Task<IReadOnlyList<JiraUser>> FindUsersAsync(string query, CancellationToken ct);

    /// <summary>The reference to put in an issue's <c>assignee</c> field.</summary>
    JiraUserRef ToRef(JiraUser user);

    /// <param name="user">null unassigns.</param>
    Task AssignAsync(string key, JiraUser? user, CancellationToken ct);

    Task<IReadOnlyList<JiraTransition>> GetTransitionsAsync(string key, CancellationToken ct);

    Task TransitionAsync(string key, string transitionId, string? resolution, CancellationToken ct);

    Task AddCommentAsync(string key, string wikiBody, CancellationToken ct);

    Task<IReadOnlyList<JiraAttachment>> AttachAsync(string key, string fileName, Stream content, CancellationToken ct);

    Task<JiraProject> GetProjectAsync(string projectKey, CancellationToken ct);
}

public sealed class JiraApiException(HttpStatusCode status, string message) : Exception(message), Core.Tools.IToolError
{
    public HttpStatusCode Status { get; } = status;
}
