using System.ComponentModel;
using SimpleArchitectureMcp.Server.Core.Tools;
using SimpleArchitectureMcp.Server.Modules.GitHub;
using SimpleArchitectureMcp.Server.Modules.Jira;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace SimpleArchitectureMcp.Server.Modules.Migration;

/// <summary>
/// One tool that copies issues between Jira and GitHub on the server. Listed only while both modules are configured.
/// </summary>
[McpServerToolType]
public sealed class MigrationTools(MigrationService migration)
{
    [McpServerTool(Name = "migrate_issues", Destructive = false)]
    [Description("Copy issues Jira↔GitHub with comments, labels, status, assignee, milestone, attachments. "
                 + "Skips copied issues, so a rerun resumes. One line per issue.")]
    public Task<string> Migrate(
        [Description("owner/repo (copy from Jira) or a Jira project key (copy from GitHub)")] string target,
        [Description("Jira keys, or GitHub #12 / owner/repo#12")] string[]? issues = null,
        [Description("Instead of issues: JQL or GitHub search")] string? query = null,
        [Description("From the previous result's 'next cursor'")] string? cursor = null,
        [Description("Max 25")] int limit = 10,
        [Description("Only show what would be created")] bool dry_run = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("migrate_issues", "Jira or GitHub", () => migration.MigrateAsync(target, issues, query, cursor, limit, dry_run, cancellationToken),
            cancellationToken);
}
