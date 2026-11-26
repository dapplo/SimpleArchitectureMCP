using System.ComponentModel;
using DevToolsMcp.Server.Core.Telemetry;
using DevToolsMcp.Server.Modules.GitHub;
using DevToolsMcp.Server.Modules.Jira;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Modules.Migration;

/// <summary>
/// One tool that copies issues between Jira and GitHub on the server. Listed only while both modules are configured.
/// </summary>
[McpServerToolType]
public sealed class MigrationTools(MigrationService migration)
{
    [McpServerTool(Name = "migrate_issues")]
    [Description("Copy issues Jira→GitHub or GitHub→Jira with all comments, labels, status, assignee, milestone and attachments. "
                 + "Already copied issues are skipped; an interrupted run resumes. One line per issue.")]
    public Task<string> Migrate(
        [Description("owner/repo (copy from Jira) or a Jira project key (copy from GitHub)")] string target,
        [Description("Jira keys, or GitHub #12 / owner/repo#12")] string[]? issues = null,
        [Description("Instead of issues: JQL, or a GitHub search (repo: qualifier or the default repo)")] string? query = null,
        [Description("From the previous result's 'next cursor'")] string? cursor = null,
        [Description("Issues per call, max 25")] int limit = 10,
        [Description("Only show what would be created")] bool dry_run = false,
        CancellationToken cancellationToken = default) =>
        Run(() => migration.MigrateAsync(target, issues, query, cursor, limit, dry_run, cancellationToken));

    private static async Task<string> Run(Func<Task<string>> action)
    {
        try
        {
            return DevToolsTelemetry.Output("migrate_issues", await action().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is JiraApiException or GitHubApiException or JiraNotConfiguredException or GitHubNotConfiguredException
                                       or ArgumentException or HttpRequestException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
