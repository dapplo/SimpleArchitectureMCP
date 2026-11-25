using System.ComponentModel;
using DevToolsMcp.Server.Core.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Modules.GitHub;

/// <summary>
/// The six GitHub issue tools. Descriptions are intentionally terse: every word here is paid for in every prompt.
/// <c>repo</c> is optional everywhere when <c>GitHub:DefaultRepo</c> is configured.
/// </summary>
[McpServerToolType]
public sealed class GitHubTools(GitHubService github)
{
    private const string Repo = "owner/repo";

    [McpServerTool(Name = "github_search_issues", ReadOnly = true, Idempotent = true)]
    [Description("Search issues (GitHub search syntax, e.g. 'is:open assignee:@me label:bug'). Lines: #n | state | title | labels | assignees | updated.")]
    public Task<string> Search(
        string query,
        [Description(Repo)] string? repo = null,
        [Description("Max 50")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        Run("github_search_issues", () => github.SearchAsync(query, repo, limit, cancellationToken));

    [McpServerTool(Name = "github_get_issue", ReadOnly = true, Idempotent = true)]
    [Description("Get an issue with metadata, body and the latest comments ([comment_id] @author date).")]
    public Task<string> GetIssue(
        int number,
        [Description(Repo)] string? repo = null,
        [Description("Latest comments to include, 0-100")] int comments = 20,
        CancellationToken cancellationToken = default) =>
        Run("github_get_issue", () => github.GetIssueAsync(repo, number, comments, cancellationToken));

    [McpServerTool(Name = "github_create_issue")]
    [Description("Create an issue (Markdown body). Returns its number and URL.")]
    public Task<string> CreateIssue(
        string title,
        string? body = null,
        [Description(Repo)] string? repo = null,
        string[]? labels = null,
        [Description("Logins")] string[]? assignees = null,
        [Description("Milestone number")] int? milestone = null,
        CancellationToken cancellationToken = default) =>
        Run("github_create_issue", () => github.CreateIssueAsync(repo, title, body, labels, assignees, milestone, cancellationToken));

    [McpServerTool(Name = "github_update_issue")]
    [Description("Edit, close/reopen, label or assign an issue. Only given fields change; body replaces the description.")]
    public Task<string> UpdateIssue(
        int number,
        [Description(Repo)] string? repo = null,
        string? title = null,
        string? body = null,
        [Description("open or closed")] string? state = null,
        [Description("completed, not_planned, duplicate or reopened")] string? state_reason = null,
        [Description("Milestone number; 0 clears")] int? milestone = null,
        string[]? add_labels = null,
        string[]? remove_labels = null,
        [Description("Logins")] string[]? add_assignees = null,
        [Description("Logins")] string[]? remove_assignees = null,
        CancellationToken cancellationToken = default) =>
        Run("github_update_issue", () => github.UpdateIssueAsync(repo, number, title, body, state, state_reason, milestone,
            add_labels, remove_labels, add_assignees, remove_assignees, cancellationToken));

    [McpServerTool(Name = "github_comment")]
    [Description("Comment on an issue (Markdown), or replace your comment's text when comment_id is given.")]
    public Task<string> Comment(
        int number,
        string body,
        [Description(Repo)] string? repo = null,
        long? comment_id = null,
        CancellationToken cancellationToken = default) =>
        Run("github_comment", () => github.CommentAsync(repo, number, body, comment_id, cancellationToken));

    [McpServerTool(Name = "github_list_labels", ReadOnly = true, Idempotent = true)]
    [Description("List the repository's labels (name | description).")]
    public Task<string> ListLabels(
        [Description(Repo)] string? repo = null,
        CancellationToken cancellationToken = default) =>
        Run("github_list_labels", () => github.ListLabelsAsync(repo, cancellationToken));

    /// <summary>Maps failures to concise MCP tool errors and records the output size.</summary>
    private static async Task<string> Run(string tool, Func<Task<string>> action)
    {
        try
        {
            return DevToolsTelemetry.Output(tool, await action().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is GitHubApiException or GitHubNotConfiguredException or ArgumentException or HttpRequestException or TimeoutException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
