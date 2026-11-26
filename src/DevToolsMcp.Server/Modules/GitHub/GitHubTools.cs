using System.ComponentModel;
using DevToolsMcp.Server.Core.Tools;
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
    private const string Service = "GitHub";
    private const string Repo = "owner/repo";

    [McpServerTool(Name = "github_search_issues", ReadOnly = true, Idempotent = true)]
    [Description("Search issues (GitHub search syntax, e.g. 'is:open assignee:@me label:bug'). Lines: #n | state | title | labels | assignees | updated.")]
    public Task<string> Search(
        string query,
        [Description(Repo)] string? repo = null,
        [Description("Max 50")] int limit = 20,
        [Description("From the previous result's 'next page'")] int page = 1,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("github_search_issues", Service, () => github.SearchAsync(query, repo, limit, page, cancellationToken), cancellationToken);

    [McpServerTool(Name = "github_get_issue", ReadOnly = true, Idempotent = true)]
    [Description("Get an issue: metadata, body, latest comments ([comment_id] @author date).")]
    public Task<string> GetIssue(
        int number,
        [Description(Repo)] string? repo = null,
        [Description("Latest comments, 0-100")] int comments = 5,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("github_get_issue", Service, () => github.GetIssueAsync(repo, number, comments, cancellationToken), cancellationToken);

    [McpServerTool(Name = "github_create_issue", Destructive = false)]
    [Description("Create an issue (Markdown body). Returns its number and URL.")]
    public Task<string> CreateIssue(
        string title,
        string? body = null,
        [Description(Repo)] string? repo = null,
        string[]? labels = null,
        [Description("Logins")] string[]? assignees = null,
        [Description("Milestone number")] int? milestone = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("github_create_issue", Service, () => github.CreateIssueAsync(repo, title, body, labels, assignees, milestone, cancellationToken), cancellationToken);

    [McpServerTool(Name = "github_update_issue", Idempotent = true)]
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
        ToolErrors.RunAsync("github_update_issue", Service, () => github.UpdateIssueAsync(repo, number, title, body, state, state_reason, milestone,
            add_labels, remove_labels, add_assignees, remove_assignees, cancellationToken), cancellationToken);

    [McpServerTool(Name = "github_comment")]
    [Description("Comment on an issue (Markdown), or replace your comment's text when comment_id is given.")]
    public Task<string> Comment(
        int number,
        string body,
        [Description(Repo)] string? repo = null,
        long? comment_id = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("github_comment", Service, () => github.CommentAsync(repo, number, body, comment_id, cancellationToken), cancellationToken);

    [McpServerTool(Name = "github_list_labels", ReadOnly = true, Idempotent = true)]
    [Description("List the repository's labels (name | description).")]
    public Task<string> ListLabels(
        [Description(Repo)] string? repo = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("github_list_labels", Service, () => github.ListLabelsAsync(repo, cancellationToken), cancellationToken);
}
