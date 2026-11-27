using System.ComponentModel;
using SimpleArchitectureMcp.Server.Core.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SimpleArchitectureMcp.Server.Modules.Jira;

/// <summary>
/// The seven Jira tools: basic issue work with the user's own permissions, no admin features.
/// Descriptions are intentionally terse: every word here is paid for in every prompt.
/// </summary>
[McpServerToolType]
public sealed class JiraTools(JiraService jira)
{
    private const string Service = "Jira";

    [McpServerTool(Name = "jira_search", ReadOnly = true, Idempotent = true)]
    [Description("Search issues. Returns lines: key | type | status | assignee | updated | summary.")]
    public Task<string> Search(
        [Description("JQL, an issue key, or plain text")] string query,
        [Description("Max 50")] int limit = 20,
        [Description("From the previous result's 'next cursor'")] string? cursor = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("jira_search", Service, () => jira.SearchAsync(query, limit, cursor, cancellationToken), cancellationToken);

    [McpServerTool(Name = "jira_get_issue", ReadOnly = true, Idempotent = true)]
    [Description("Get an issue: fields, Markdown description, latest comments.")]
    public Task<string> GetIssue(
        string issue_key,
        [Description("Latest comments, 0-50")] int comments = 5,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("jira_get_issue", Service, () => jira.GetIssueAsync(issue_key, comments, cancellationToken), cancellationToken);

    [McpServerTool(Name = "jira_create_issue", Destructive = false)]
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
        ToolErrors.RunAsync("jira_create_issue", Service, () => jira.CreateIssueAsync(project_key, summary, issue_type, description, assignee, labels, priority, parent_key, cancellationToken), cancellationToken);

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
        ToolErrors.RunAsync("jira_update_issue", Service, () => jira.UpdateIssueAsync(issue_key, summary, description, priority, add_labels, remove_labels, assignee, cancellationToken), cancellationToken);

    [McpServerTool(Name = "jira_transition_issue")]
    [Description("Change status (e.g. close: transition=Done). Omit transition to list the available ones.")]
    public Task<string> TransitionIssue(
        string issue_key,
        [Description("Transition id or name, or target status")] string? transition = null,
        [Description("Markdown comment to add")] string? comment = null,
        [Description("e.g. Fixed, Won't Do, if the workflow asks for one")] string? resolution = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("jira_transition_issue", Service, () => jira.TransitionAsync(issue_key, transition, comment, resolution, cancellationToken), cancellationToken);

    [McpServerTool(Name = "jira_add_comment", Destructive = false)]
    [Description("Add a Markdown comment to an issue.")]
    public Task<string> AddComment(
        string issue_key,
        string body,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("jira_add_comment", Service, () => jira.AddCommentAsync(issue_key, body, cancellationToken), cancellationToken);

    [McpServerTool(Name = "jira_attach_file", Destructive = false)]
    [Description("Attach a local file to an issue.")]
    public Task<string> AttachFile(
        string issue_key,
        [Description("Absolute path")] string file,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("jira_attach_file", Service, () => jira.AttachFileAsync(issue_key, file, cancellationToken), cancellationToken);
}
