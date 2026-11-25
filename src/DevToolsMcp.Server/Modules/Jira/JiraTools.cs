using System.ComponentModel;
using DevToolsMcp.Server.Core.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Modules.Jira;

/// <summary>
/// The seven Jira tools: basic issue work with the user's own permissions, no admin features.
/// Descriptions are intentionally terse: every word here is paid for in every prompt.
/// </summary>
[McpServerToolType]
public sealed class JiraTools(JiraService jira)
{
    [McpServerTool(Name = "jira_search", ReadOnly = true, Idempotent = true)]
    [Description("Search issues. Returns lines: key | type | status | assignee | updated | summary.")]
    public Task<string> Search(
        [Description("JQL, an issue key, or plain text")] string jql,
        [Description("Max 50")] int limit = 20,
        CancellationToken cancellationToken = default) =>
        Run("jira_search", () => jira.SearchAsync(jql, limit, cancellationToken));

    [McpServerTool(Name = "jira_get_issue", ReadOnly = true, Idempotent = true)]
    [Description("Get an issue: fields, Markdown description, latest comments.")]
    public Task<string> GetIssue(
        string issue_key,
        [Description("Latest comments to include, 0-50")] int comments = 5,
        CancellationToken cancellationToken = default) =>
        Run("jira_get_issue", () => jira.GetIssueAsync(issue_key, comments, cancellationToken));

    [McpServerTool(Name = "jira_create_issue")]
    [Description("Create an issue. Returns key and URL.")]
    public Task<string> CreateIssue(
        string project_key,
        string summary,
        [Description("Task, Bug, Story, Sub-task, ...")] string issue_type = "Task",
        [Description("Markdown")] string? description = null,
        [Description("me, username, email or name")] string? assignee = null,
        string[]? labels = null,
        string? priority = null,
        [Description("Parent issue key (sub-tasks)")] string? parent_key = null,
        CancellationToken cancellationToken = default) =>
        Run("jira_create_issue", () => jira.CreateIssueAsync(project_key, summary, issue_type, description, assignee, labels, priority, parent_key, cancellationToken));

    [McpServerTool(Name = "jira_update_issue", Idempotent = true)]
    [Description("Edit an issue; only the given fields change. Also assigns and adds/removes labels.")]
    public Task<string> UpdateIssue(
        string issue_key,
        string? summary = null,
        [Description("Markdown; replaces the description")] string? description = null,
        string? priority = null,
        string[]? add_labels = null,
        string[]? remove_labels = null,
        [Description("me, none, username, email or name")] string? assignee = null,
        CancellationToken cancellationToken = default) =>
        Run("jira_update_issue", () => jira.UpdateIssueAsync(issue_key, summary, description, priority, add_labels, remove_labels, assignee, cancellationToken));

    [McpServerTool(Name = "jira_transition_issue")]
    [Description("Change status (e.g. close: transition=Done). Omit transition to list the available ones.")]
    public Task<string> TransitionIssue(
        string issue_key,
        [Description("Transition id or name, or target status")] string? transition = null,
        [Description("Markdown comment to add")] string? comment = null,
        [Description("e.g. Fixed, Won't Do, if the workflow asks for one")] string? resolution = null,
        CancellationToken cancellationToken = default) =>
        Run("jira_transition_issue", () => jira.TransitionAsync(issue_key, transition, comment, resolution, cancellationToken));

    [McpServerTool(Name = "jira_add_comment")]
    [Description("Add a Markdown comment to an issue.")]
    public Task<string> AddComment(
        string issue_key,
        string body,
        CancellationToken cancellationToken = default) =>
        Run("jira_add_comment", () => jira.AddCommentAsync(issue_key, body, cancellationToken));

    [McpServerTool(Name = "jira_attach_file")]
    [Description("Attach a local file to an issue.")]
    public Task<string> AttachFile(
        string issue_key,
        [Description("Absolute path")] string file_path,
        CancellationToken cancellationToken = default) =>
        Run("jira_attach_file", () => jira.AttachFileAsync(issue_key, file_path, cancellationToken));

    /// <summary>Maps failures to concise MCP tool errors and records the output size.</summary>
    private static async Task<string> Run(string tool, Func<Task<string>> action)
    {
        try
        {
            return DevToolsTelemetry.Output(tool, await action().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JiraApiException or JiraNotConfiguredException or ArgumentException or HttpRequestException
                                       or TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
