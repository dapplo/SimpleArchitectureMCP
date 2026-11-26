using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DevToolsMcp.Server.Modules.GitHub;
using DevToolsMcp.Server.Modules.Jira;

namespace DevToolsMcp.Server.Modules.Migration;

/// <summary>
/// Copies issues between Jira and GitHub on the server, so descriptions and comments never pass through the model:
/// the tool answers with one line per issue. Everything runs with the user's own permissions, so copied comments are
/// posted by the user, with the original author and date in a header line. Progress is recorded in a
/// <see cref="MigrationMap"/> after every step, which makes re-runs skip finished work and resume unfinished issues.
/// </summary>
public sealed partial class MigrationService
{
    public const int MaxBatch = 25;
    public const long MaxCommitBytes = 25L * 1024 * 1024;

    private readonly Func<IJiraApiAdapter> _jira;
    private readonly JiraService _jiraService;
    private readonly Func<GitHubApi> _github;
    private readonly Func<MigrationOptions> _options;
    private readonly string _defaultMapFile;
    private DateTimeOffset _lastGitHubWrite = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MigrationService(Func<IJiraApiAdapter> jira, JiraService jiraService, Func<GitHubApi> github, Func<MigrationOptions> options, string defaultMapFile)
    {
        _jira = jira;
        _jiraService = jiraService;
        _github = github;
        _options = options;
        _defaultMapFile = defaultMapFile;
    }

    /// <summary>GitHub asks for at least a second between content-creating requests; tests set it to zero.</summary>
    internal TimeSpan GitHubWritePause { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The clock for <see cref="MigrationRecord.Creating"/>; tests fix it.</summary>
    internal Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public async Task<string> MigrateAsync(string target, string[]? issues, string? query, string? cursor, int limit, bool dryRun, CancellationToken ct)
    {
        var t = target?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            throw new ArgumentException("target is required: owner/repo (Jira → GitHub) or a Jira project key (GitHub → Jira).");
        }

        var list = (issues ?? []).Select(i => i?.Trim() ?? string.Empty).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0 && string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Pass issues (a list) or query (which source issues to copy).");
        }

        if (list.Count > MaxBatch)
        {
            throw new ArgumentException($"At most {MaxBatch} issues per call.");
        }

        var options = _options();
        var take = Math.Clamp(limit, 1, MaxBatch);

        // One migration at a time, and the map is read once the previous one is done: two calls copying the same
        // issue would otherwise both create it before either records it.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var run = new Run(options, MigrationMap.Load(MapPath(options.MapFile, _defaultMapFile)), options.Users(), dryRun);
            return t.Contains('/', StringComparison.Ordinal)
                ? await ToGitHubAsync(run, GitHubService.ValidateRepo(t), list, query, cursor, take, ct).ConfigureAwait(false)
                : await ToJiraAsync(run, JiraService.ValidateProjectKey(t), list, query, cursor, take, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The map file: the default next to settings.json, or the configured one. A relative path is taken relative to
    /// the settings folder, not the working directory, which differs between a stdio start by the MCP client and a
    /// double-click; otherwise a resumed run would not find the record and would create every issue again.
    /// </summary>
    internal static string MapPath(string? configured, string defaultMapFile)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultMapFile;
        }

        var p = configured.Trim().Trim('"');
        if (p.StartsWith('~'))
        {
            p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        }

        return Path.GetFullPath(p, Path.GetDirectoryName(Path.GetFullPath(defaultMapFile))!);
    }

    private sealed class Run(MigrationOptions options, MigrationMap map, UserMap users, bool dryRun)
    {
        public MigrationOptions Options { get; } = options;
        public MigrationMap Map { get; } = map;
        public UserMap Users { get; } = users;
        public bool DryRun { get; } = dryRun;
        public Dictionary<string, int>? Milestones { get; set; }
        public List<JiraNamed>? Versions { get; set; }
        public bool BranchReady { get; set; }
    }

    /// <summary>
    /// The copy runs on the server, so the client's approval prompt shows only the target. Anyone can open issues in a
    /// public repository: a prompt-injected target such as <c>attacker/inbox</c> would receive private Jira issues.
    /// Only repositories the user can push to (their own or their team's) are accepted.
    /// </summary>
    private static async Task EnsureCanPushAsync(GitHubApi github, string repo, CancellationToken ct)
    {
        var info = await github.GetRepoAsync(repo, ct).ConfigureAwait(false);
        if (info.Permissions?.Push != true)
        {
            throw new ArgumentException($"Refusing to migrate into {repo}: your GitHub account has no write access there. "
                                        + "Migrations only go to repositories you can push to.");
        }
    }

    // ---- Batch driver ------------------------------------------------------------------------------------------

    private static async Task<string> BatchAsync(IReadOnlyList<string> sources, string? next, Func<string, Task<string>> migrate, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                sb.Append(await migrate(source).ConfigureAwait(false)).Append('\n');
            }
            catch (Exception ex) when (IsRateLimit(ex))
            {
                sb.Append(source).Append(" ✗ ").Append(ex.Message).Append('\n')
                    .Append("Stopped: rate limited. Run the same call again later; finished steps are skipped.");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) when (Core.Tools.ToolErrors.Describe(ex, "Jira or GitHub", ct) is { } error)
            {
                // One failed issue (a timeout, a refused write, ...) doesn't stop the batch.
                sb.Append(source).Append(" ✗ ").Append(error).Append('\n');
            }
        }

        if (sources.Count == 0)
        {
            sb.Append("No issues.\n");
        }

        if (next is not null)
        {
            sb.Append("next cursor: ").Append(next);
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsRateLimit(Exception ex) => ex switch
    {
        GitHubApiException g => g.Status == HttpStatusCode.TooManyRequests
                                || (g.Status == HttpStatusCode.Forbidden && g.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)),
        JiraApiException j => j.Status == HttpStatusCode.TooManyRequests,
        _ => false,
    };

    // ---- Jira → GitHub -----------------------------------------------------------------------------------------

    private async Task<string> ToGitHubAsync(Run run, string repo, List<string> keys, string? query, string? cursor, int limit, CancellationToken ct)
    {
        var jira = _jira();
        var github = _github();
        string? next = null;
        if (keys.Count == 0)
        {
            var jql = JiraJql.Build(query!);
            if (!OrderBy().IsMatch(jql))
            {
                jql += " ORDER BY created ASC"; // oldest first, so GitHub numbers follow the original order
            }

            var page = await jira.SearchAsync(jql, limit, string.IsNullOrWhiteSpace(cursor) ? null : cursor.Trim(), ct, "summary").ConfigureAwait(false);
            keys = page.Issues.Select(i => i.Key!).Where(k => k is not null).ToList();
            next = JiraService.NextCursor(page);
        }
        else
        {
            keys = keys.Select(JiraService.ValidateKey).ToList();
        }

        var targetId = $"github:{github.WebHost}/{repo}";
        // Before anything is written; a dry run and a rerun of finished issues write nothing, so they skip the check.
        if (!run.DryRun && keys.Any(k => run.Map.Get(MigrationMap.Key($"jira:{jira.SiteBase.Host}/{k}", targetId)) is not { Done: true }))
        {
            await EnsureCanPushAsync(github, repo, ct).ConfigureAwait(false);
        }

        return await BatchAsync(keys, next, key => JiraIssueToGitHubAsync(run, jira, github, repo, targetId, key, ct), ct).ConfigureAwait(false);
    }

    private async Task<string> JiraIssueToGitHubAsync(Run run, IJiraApiAdapter jira, GitHubApi github, string repo, string targetId, string key, CancellationToken ct)
    {
        var mapKey = MigrationMap.Key($"jira:{jira.SiteBase.Host}/{key}", targetId);
        var record = run.Map.Get(mapKey);
        if (record is { Done: true })
        {
            return $"{key} = {record.Target} (already migrated)";
        }

        var issue = await jira.GetIssueAsync(key, ct).ConfigureAwait(false);
        var f = issue.Fields ?? new JiraIssueFields();
        var comments = await AllJiraCommentsAsync(jira, key, ct).ConfigureAwait(false);
        var browse = _jiraService.BrowseUrl(key);

        var done = string.Equals(f.Status?.StatusCategory?.Key, "done", StringComparison.OrdinalIgnoreCase);
        var reason = done ? MigrationText.StateReason(f.Resolution?.Name) : null;
        var labels = GitHubLabels(f, done);
        var assignee = run.Users.ToGitHub(f.Assignee);
        var attachments = f.Attachment ?? [];

        if (run.DryRun)
        {
            return $"{key} → {repo} (dry run): \"{f.Summary}\" | labels: {(labels.Count == 0 ? "-" : string.Join(", ", labels))}"
                   + $" | assignee: {(assignee is null ? "-" : "@" + assignee)} | {(done ? $"closed ({reason})" : "open")}"
                   + $" | milestone: {f.FixVersions?.FirstOrDefault()?.Name ?? "-"} | {comments.Count} comments | {attachments.Count} attachments";
        }

        // Attachment links: Jira's own URLs, or files committed to the repository (before the body that links them).
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in attachments.Where(a => a.Filename is not null && a.Content is not null))
        {
            urls.TryAdd(a.Filename!, run.Options.CommitAttachments
                ? await CommitAttachmentAsync(run, jira, github, repo, key, a, upload: record?.Target is null, ct).ConfigureAwait(false)
                : a.Content!);
        }

        string? Login(string jiraName) => run.Users.ToGitHub(jiraName);

        if (record?.Target is null)
        {
            // An earlier run that stopped between creating the issue and recording it: adopt that issue.
            var created = record?.Creating is { } started
                ? await FindGitHubCopyAsync(github, repo, key, browse, started, ct).ConfigureAwait(false)
                : null;
            if (created is null)
            {
                var body = GitHubBody(run, issue, key, browse, targetId, urls, Login);
                int? milestone = f.FixVersions is [{ Name: { Length: > 0 } version }, ..]
                    ? await MilestoneAsync(run, github, repo, version, ct).ConfigureAwait(false)
                    : null;
                run.Map.Set(mapKey, new MigrationRecord { Creating = Now() });
                created = await CreateGitHubIssueAsync(github, repo, f.Summary ?? key, body, labels, assignee, milestone, ct).ConfigureAwait(false);
            }

            record = new MigrationRecord { Target = $"{repo}#{created.Number}", Url = created.HtmlUrl, TargetId = created.Id, Attachments = true };
            run.Map.Set(mapKey, record);
        }

        var number = Number(record.Target);
        for (var i = record.Comments; i < comments.Count; i++)
        {
            var c = comments[i];
            var text = $"**{JiraService.UserName(c.Author) ?? "Unknown"}** commented on {MigrationText.Stamp(MigrationText.JiraDate(c.Created))} (Jira):\n\n"
                       + MigrationText.ForGitHub(JiraWikiConverter.ToMarkdown(c.Body), urls, Login);
            await PauseAsync(ct).ConfigureAwait(false);
            await github.AddCommentAsync(repo, number, MigrationText.Truncate(text, MigrationText.GitHubMaxBody, browse), ct).ConfigureAwait(false);
            record.Comments = i + 1;
            run.Map.Set(mapKey, record);
        }

        var notes = new List<string>();
        if (done && !record.Closed)
        {
            await PauseAsync(ct).ConfigureAwait(false);
            await github.UpdateIssueAsync(repo, number, w =>
            {
                w.WriteString("state", "closed");
                w.WriteString("state_reason", reason);
            }, ct).ConfigureAwait(false);
            record.Closed = true;
            run.Map.Set(mapKey, record);
        }

        // Sub-issues: whichever of parent and child is migrated second creates the link.
        var children = (f.Subtasks ?? []).Select(s => s.Key).Where(k => k is not null).Select(k => run.Map.Get(MigrationMap.Key($"jira:{jira.SiteBase.Host}/{k}", targetId)))
            .Where(r => r is { TargetId: > 0 }).ToList();
        var parent = f.Parent?.Key is { } pk ? run.Map.Get(MigrationMap.Key($"jira:{jira.SiteBase.Host}/{pk}", targetId)) : null;
        var linked = 0;
        foreach (var (parentNumber, childId) in children.Select(c => (number, c!.TargetId))
                     .Concat(parent is { Target: not null } ? [(Number(parent.Target), record.TargetId)] : []))
        {
            try
            {
                await PauseAsync(ct).ConfigureAwait(false);
                await github.AddSubIssueAsync(repo, parentNumber, childId, ct).ConfigureAwait(false);
                linked++;
            }
            catch (GitHubApiException ex) when (!IsRateLimit(ex))
            {
                notes.Add("sub-issue link failed: " + ex.Message);
            }
        }

        record.Done = true;
        run.Map.Set(mapKey, record);
        return $"{key} → {record.Target} | {comments.Count} comments, {attachments.Count} attachments"
               + (linked > 0 ? $", {linked} sub-issue links" : string.Empty)
               + (notes.Count > 0 ? " | " + string.Join("; ", notes) : string.Empty) + $" | {record.Url}";
    }

    private string GitHubBody(Run run, JiraIssue issue, string key, string browse, string targetId, IReadOnlyDictionary<string, string> urls, Func<string, string?> login)
    {
        var f = issue.Fields ?? new JiraIssueFields();
        var jira = _jira();
        string Ref(string? k)
        {
            var r = k is null ? null : run.Map.Get(MigrationMap.Key($"jira:{jira.SiteBase.Host}/{k}", targetId));
            return r?.Target is { } t ? $"{k} (#{t[(t.LastIndexOf('#') + 1)..]})" : k ?? "?";
        }

        var assignee = f.Assignee is null ? null : run.Users.ToGitHub(f.Assignee) ?? JiraService.UserName(f.Assignee);
        var sb = new StringBuilder();
        sb.Append("> Migrated from Jira [").Append(key).Append("](").Append(browse).Append(") · ").Append(f.Issuetype?.Name ?? "Issue")
            .Append(" · ").Append(f.Status?.Name ?? "?");
        if (f.Resolution?.Name is { } resolution)
        {
            sb.Append(" (").Append(resolution).Append(')');
        }

        sb.Append("\n> Reported by ").Append(JiraService.UserName(f.Reporter) ?? "unknown").Append(" on ").Append(MigrationText.Day(MigrationText.JiraDate(f.Created)));
        if (assignee is not null)
        {
            sb.Append(" · assigned to ").Append(assignee);
        }

        sb.Append("\n\n");
        var description = MigrationText.ForGitHub(JiraWikiConverter.ToMarkdown(f.Description), urls, login);
        sb.Append(string.IsNullOrWhiteSpace(description) ? "*(no description)*" : description.Trim()).Append('\n');

        var extra = new StringBuilder();
        if (f.Attachment is { Count: > 0 } files)
        {
            extra.Append("\n**Attachments:** ").AppendJoin(", ", files.Select(a =>
                urls.TryGetValue(a.Filename ?? string.Empty, out var url) ? $"[{a.Filename}]({url}) ({JiraService.Size(a.Size)})" : a.Filename)).Append('\n');
        }

        if (f.Parent?.Key is { } parent)
        {
            extra.Append("\n**Parent:** ").Append(Ref(parent)).Append('\n');
        }

        if (f.Subtasks is { Count: > 0 } subtasks)
        {
            extra.Append("\n**Sub-tasks:** ").AppendJoin(", ", subtasks.Select(s => Ref(s.Key))).Append('\n');
        }

        if (f.Issuelinks is { Count: > 0 } links)
        {
            extra.Append("\n**Links:** ").AppendJoin(", ", links.Select(l => l.OutwardIssue is { } o
                ? $"{l.Type?.Outward ?? "relates to"} {Ref(o.Key)}"
                : $"{l.Type?.Inward ?? "relates to"} {Ref(l.InwardIssue?.Key)}")).Append('\n');
        }

        if (extra.Length > 0)
        {
            sb.Append("\n---").Append(extra);
        }

        return MigrationText.Truncate(sb.ToString().TrimEnd(), MigrationText.GitHubMaxBody, browse);
    }

    private static List<string> GitHubLabels(JiraIssueFields f, bool done)
    {
        var labels = new List<string>();
        labels.AddRange(f.Labels ?? []);
        if (f.Issuetype?.Name is { Length: > 0 } type)
        {
            labels.Add(type.ToLowerInvariant());
        }

        labels.AddRange((f.Components ?? []).Select(c => c.Name).OfType<string>());
        if (f.Priority?.Name is { Length: > 0 } priority)
        {
            labels.Add("priority: " + priority);
        }

        if (!done && string.Equals(f.Status?.StatusCategory?.Key, "indeterminate", StringComparison.OrdinalIgnoreCase) && f.Status?.Name is { } status)
        {
            labels.Add("status: " + status);
        }

        return labels.Select(MigrationText.GitHubLabel).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();
    }

    private async Task<GhIssue> CreateGitHubIssueAsync(GitHubApi github, string repo, string title, string body, List<string> labels, string? assignee,
        int? milestone, CancellationToken ct)
    {
        Task<GhIssue> Create(bool withAssignee) => github.CreateIssueAsync(repo, w =>
        {
            w.WriteString("title", title.Length <= 256 ? title : title[..256]);
            w.WriteString("body", body);
            if (labels.Count > 0)
            {
                w.WriteStartArray("labels");
                labels.ForEach(w.WriteStringValue);
                w.WriteEndArray();
            }

            if (withAssignee && assignee is not null)
            {
                w.WriteStartArray("assignees");
                w.WriteStringValue(assignee);
                w.WriteEndArray();
            }

            if (milestone is > 0)
            {
                w.WriteNumber("milestone", milestone.Value);
            }
        }, ct);

        await PauseAsync(ct).ConfigureAwait(false);
        try
        {
            return await Create(withAssignee: true).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (assignee is not null && ex.Status == HttpStatusCode.UnprocessableEntity
                                            && ex.Message.Contains("assignee", StringComparison.OrdinalIgnoreCase))
        {
            await PauseAsync(ct).ConfigureAwait(false);
            return await Create(withAssignee: false).ConfigureAwait(false); // mapped login can't be assigned in this repository
        }
    }

    private async Task<int> MilestoneAsync(Run run, GitHubApi github, string repo, string title, CancellationToken ct)
    {
        run.Milestones ??= (await github.GetMilestonesAsync(repo, ct).ConfigureAwait(false))
            .Where(m => m.Title is not null).GroupBy(m => m.Title!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Number, StringComparer.OrdinalIgnoreCase);
        if (run.Milestones.TryGetValue(title, out var number))
        {
            return number;
        }

        await PauseAsync(ct).ConfigureAwait(false);
        var created = await github.CreateMilestoneAsync(repo, title, ct).ConfigureAwait(false);
        run.Milestones[title] = created.Number;
        return created.Number;
    }

    /// <param name="upload">false on a resumed issue: the files were committed when it was created.</param>
    private async Task<string> CommitAttachmentAsync(Run run, IJiraApiAdapter jira, GitHubApi github, string repo, string key, JiraAttachment a,
        bool upload, CancellationToken ct)
    {
        var branch = run.Options.Branch;
        var path = $"{key}/{a.Filename}";
        var web = $"https://{github.WebHost}/{repo}/blob/{Uri.EscapeDataString(branch)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}?raw=true";
        if (a.Size > MaxCommitBytes)
        {
            return a.Content!; // too large for the contents API: keep the Jira link
        }

        if (!upload)
        {
            return web;
        }

        if (!run.BranchReady)
        {
            try
            {
                await github.GetBranchRefAsync(repo, branch, ct).ConfigureAwait(false);
            }
            catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.NotFound)
            {
                var info = await github.GetRepoAsync(repo, ct).ConfigureAwait(false);
                var head = await github.GetBranchRefAsync(repo, info.DefaultBranch ?? "main", ct).ConfigureAwait(false);
                await PauseAsync(ct).ConfigureAwait(false);
                await github.CreateBranchAsync(repo, branch, head.Object?.Sha ?? throw new GitHubApiException(HttpStatusCode.NotFound, "default branch has no commit"), ct)
                    .ConfigureAwait(false);
            }

            run.BranchReady = true;
        }

        var data = await jira.DownloadAsync(a.Content!, MaxCommitBytes, ct).ConfigureAwait(false);
        try
        {
            await PauseAsync(ct).ConfigureAwait(false);
            await github.CreateFileAsync(repo, path, branch, data, $"Attachment {a.Filename} of {key}", ct).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.Status == HttpStatusCode.UnprocessableEntity)
        {
            // Already committed by an earlier, interrupted run.
        }

        return web;
    }

    private static async Task<List<JiraComment>> AllJiraCommentsAsync(IJiraApiAdapter jira, string key, CancellationToken ct)
    {
        var all = new List<JiraComment>();
        while (true)
        {
            var page = await jira.GetCommentsPageAsync(key, all.Count, 100, ct).ConfigureAwait(false);
            all.AddRange(page.Comments);
            if (page.Comments.Count == 0 || all.Count >= page.Total)
            {
                return all;
            }
        }
    }

    // ---- GitHub → Jira -----------------------------------------------------------------------------------------

    private async Task<string> ToJiraAsync(Run run, string project, List<string> refs, string? query, string? cursor, int limit, CancellationToken ct)
    {
        var jira = _jira();
        var github = _github();
        string? next = null;
        List<(string Repo, int Number)> sources;
        if (refs.Count == 0)
        {
            var q = query!.Trim();
            if (!RepoQualifier().IsMatch(q))
            {
                q = $"repo:{github.DefaultRepo ?? throw new ArgumentException("Add repo:owner/name to the query (no default repository is configured).")} {q}";
            }

            if (!TypeQualifier().IsMatch(q))
            {
                q = "is:issue " + q;
            }

            var page = int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 1 ? p : 1;
            if ((page - 1) * limit >= GitHubService.MaxSearchResults)
            {
                throw new ArgumentException("GitHub search stops at 1000 results; slice the query, e.g. created:2024-01-01..2024-06-30.");
            }

            var result = await github.SearchIssuesAsync(q, limit, page, "created", "asc", ct).ConfigureAwait(false);
            sources = result.Items.Where(i => i.PullRequest is null)
                .Select(i => (GitHubService.RepoFromApiUrl(i.RepositoryUrl) ?? github.DefaultRepo ?? string.Empty, i.Number)).ToList();
            var seen = page * limit;
            next = result.TotalCount > seen && seen < GitHubService.MaxSearchResults && result.Items.Count > 0 ? (page + 1).ToString(CultureInfo.InvariantCulture) : null;
        }
        else
        {
            sources = refs.Select(r => ParseRef(r, github.DefaultRepo)).DistinctBy(s => $"{s.Repo}#{s.Number}", StringComparer.OrdinalIgnoreCase).ToList();
        }

        var targetId = $"jira:{jira.SiteBase.Host}/{project}";
        var labels = sources.Select(s => $"{s.Repo}#{s.Number}").ToList();
        var byLabel = labels.Zip(sources).DistinctBy(x => x.First, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.First, x => x.Second, StringComparer.OrdinalIgnoreCase);
        return await BatchAsync(labels, next, label => GitHubIssueToJiraAsync(run, jira, github, project, targetId, byLabel[label].Repo, byLabel[label].Number, ct), ct)
            .ConfigureAwait(false);
    }

    private async Task<string> GitHubIssueToJiraAsync(Run run, IJiraApiAdapter jira, GitHubApi github, string project, string targetId, string repo, int number,
        CancellationToken ct)
    {
        var source = $"{repo}#{number}";
        var mapKey = MigrationMap.Key($"github:{github.WebHost}/{source}", targetId);
        var record = run.Map.Get(mapKey);
        if (record is { Done: true })
        {
            return $"{source} = {record.Target} (already migrated)";
        }

        var issue = await github.GetIssueAsync(repo, number, ct).ConfigureAwait(false);
        if (issue.PullRequest is not null)
        {
            return $"{source} skipped: pull request";
        }

        var comments = new List<GhComment>();
        for (var page = 1; comments.Count < issue.Comments; page++)
        {
            var batch = await github.GetCommentsAsync(repo, number, 100, page, ct).ConfigureAwait(false);
            comments.AddRange(batch);
            if (batch.Count < 100)
            {
                break;
            }
        }

        var closed = string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase);
        var labels = issue.Labels.Select(l => l.Name).OfType<string>().Select(MigrationText.JiraLabel).Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var type = issue.Labels.Any(l => string.Equals(l.Name, "bug", StringComparison.OrdinalIgnoreCase)) ? "Bug" : "Task";
        var assigneeName = issue.Assignees.Select(a => run.Users.ToJira(a.Login)).FirstOrDefault(j => j is not null);
        var assets = MigrationText.GitHubAssets(issue.Body, github.WebHost)
            .Concat(comments.SelectMany(c => MigrationText.GitHubAssets(c.Body, github.WebHost))).Distinct(StringComparer.Ordinal).ToList();

        if (run.DryRun)
        {
            return $"{source} → {project} (dry run): {type} \"{issue.Title}\" | labels: {(labels.Count == 0 ? "-" : string.Join(", ", labels))}"
                   + $" | assignee: {assigneeName ?? "-"} | {(closed ? $"closed ({issue.StateReason ?? "completed"})" : "open")}"
                   + $" | milestone: {issue.Milestone?.Title ?? "-"} | {comments.Count} comments | {assets.Count} attachments";
        }

        // Download attachments first: image references in the text then point at the Jira attachment by file name.
        var files = new List<(string Url, string Name, byte[] Data)>();
        var notes = new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in assets)
        {
            try
            {
                var download = await github.DownloadAsync(new Uri(url), JiraService.MaxAttachmentBytes, ct).ConfigureAwait(false);
                files.Add((url, MigrationText.AssetFileName(url, download.FileName, download.MediaType, used), download.Data));
            }
            catch (Exception ex) when (ex is GitHubApiException or HttpRequestException && !IsRateLimit(ex))
            {
                notes.Add("attachment not copied: " + url);
            }
        }

        var names = files.ToDictionary(f => f.Url, f => f.Name, StringComparer.Ordinal);

        var resumed = record?.Target is not null;
        var adopted = record?.Creating is { } began ? await FindJiraCopyAsync(jira, project, source, began, Now(), ct).ConfigureAwait(false) : null;
        if (adopted is not null)
        {
            // An earlier run stopped between creating the issue and recording it: continue with that issue.
            record = new MigrationRecord { Target = adopted, Url = _jiraService.BrowseUrl(adopted) };
            run.Map.Set(mapKey, record);
            resumed = true; // it may already have some attachments
        }

        if (record?.Target is null)
        {
            var fields = new JiraCreateFields
            {
                Project = new JiraKeyRef { Key = project },
                Summary = Summary(issue.Title ?? source),
                Issuetype = new JiraNamed { Name = type },
                Description = JiraWikiConverter.ToWiki(JiraBody(run, jira, github, issue, source, targetId, names, await SubIssuesAsync(github, repo, number, ct).ConfigureAwait(false))),
                Labels = labels.Count > 0 ? labels : null,
            };

            if (issue.Milestone?.Title is { Length: > 0 } milestone)
            {
                run.Versions ??= (await jira.GetProjectAsync(project, ct).ConfigureAwait(false)).Versions ?? [];
                if (run.Versions.FirstOrDefault(v => string.Equals(v.Name, milestone, StringComparison.OrdinalIgnoreCase)) is { } version)
                {
                    fields.FixVersions = [new JiraNamed { Name = version.Name }];
                }
            }

            if (assigneeName is not null)
            {
                try
                {
                    if ((await _jiraService.ResolveUserAsync(assigneeName, ct).ConfigureAwait(false)).User is { } user)
                    {
                        fields.Assignee = jira.ToRef(user);
                    }
                }
                catch (ArgumentException ex)
                {
                    notes.Add(ex.Message);
                }
            }

            run.Map.Set(mapKey, new MigrationRecord { Creating = Now() });
            var created = await CreateJiraIssueAsync(jira, fields, ct).ConfigureAwait(false);
            record = new MigrationRecord { Target = created.Key, Url = _jiraService.BrowseUrl(created.Key ?? string.Empty) };
            run.Map.Set(mapKey, record);
        }

        var key = record.Target!;
        if (!record.Attachments)
        {
            // A resumed issue may already have some of the files (the earlier run stopped part way): skip those.
            var existing = resumed
                ? ((await jira.GetIssueAsync(key, ct).ConfigureAwait(false)).Fields?.Attachment ?? [])
                    .Select(a => a.Filename).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            foreach (var file in files.Where(f => !existing.Contains(f.Name)))
            {
                await jira.AttachAsync(key, file.Name, new MemoryStream(file.Data, writable: false), ct).ConfigureAwait(false);
            }

            record.Attachments = true;
            run.Map.Set(mapKey, record);
        }

        for (var i = record.Comments; i < comments.Count; i++)
        {
            var c = comments[i];
            var text = $"**@{c.User?.Login}** commented on {MigrationText.Stamp(c.CreatedAt)} ([GitHub]({c.HtmlUrl ?? issue.HtmlUrl})):\n\n"
                       + MigrationText.ImagesToAttachments(c.Body ?? string.Empty, names);
            await jira.AddCommentAsync(key, JiraWikiConverter.ToWiki(MigrationText.Truncate(text, MigrationText.JiraMaxMarkdown, c.HtmlUrl ?? issue.HtmlUrl ?? source)), ct)
                .ConfigureAwait(false);
            record.Comments = i + 1;
            run.Map.Set(mapKey, record);
        }

        if (closed && !record.Closed)
        {
            if (await CloseJiraIssueAsync(jira, key, issue.StateReason, ct).ConfigureAwait(false) is { } problem)
            {
                notes.Add(problem);
            }
            else
            {
                record.Closed = true;
                run.Map.Set(mapKey, record);
            }
        }

        record.Done = true;
        run.Map.Set(mapKey, record);
        return $"{source} → {key} | {comments.Count} comments, {files.Count} attachments"
               + (notes.Count > 0 ? " | " + string.Join("; ", notes) : string.Empty) + $" | {record.Url}";
    }

    private static string JiraBody(Run run, IJiraApiAdapter jira, GitHubApi github, GhIssue issue, string source, string targetId,
        IReadOnlyDictionary<string, string> names, IReadOnlyList<GhIssue> subIssues)
    {
        var sb = new StringBuilder();
        sb.Append("> Migrated from GitHub [").Append(source).Append("](").Append(issue.HtmlUrl).Append(") · opened by @").Append(issue.User?.Login)
            .Append(" on ").Append(MigrationText.Day(issue.CreatedAt));
        if (issue.Assignees.Count > 0)
        {
            sb.Append(" · assigned to ").AppendJoin(", ", issue.Assignees.Select(a => "@" + a.Login));
        }

        if (issue.Milestone?.Title is { } milestone)
        {
            sb.Append(" · milestone ").Append(milestone);
        }

        if (string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" · closed ").Append(MigrationText.Day(issue.ClosedAt)).Append(issue.StateReason is { } r and not "completed" ? $" ({r})" : string.Empty);
        }

        sb.Append("\n\n").Append(string.IsNullOrWhiteSpace(issue.Body) ? "*(no description)*" : MigrationText.ImagesToAttachments(issue.Body.Trim(), names)).Append('\n');
        if (subIssues.Count > 0)
        {
            var repo = source[..source.LastIndexOf('#')];
            sb.Append("\n**Sub-issues:** ").AppendJoin(", ", subIssues.Select(s =>
            {
                var r = $"{GitHubService.RepoFromApiUrl(s.RepositoryUrl) ?? repo}#{s.Number}";
                var mapped = run.Map.Get(MigrationMap.Key($"github:{github.WebHost}/{r}", targetId));
                return mapped?.Target is { } k ? $"{r} ({k})" : r;
            })).Append('\n');
        }

        return MigrationText.Truncate(sb.ToString().TrimEnd(), MigrationText.JiraMaxMarkdown, issue.HtmlUrl ?? source);
    }

    private static async Task<IReadOnlyList<GhIssue>> SubIssuesAsync(GitHubApi github, string repo, int number, CancellationToken ct)
    {
        try
        {
            return await github.GetSubIssuesAsync(repo, number, ct).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (!IsRateLimit(ex))
        {
            return []; // sub-issues not available (older GitHub Enterprise Server)
        }
    }

    private static async Task<JiraCreated> CreateJiraIssueAsync(IJiraApiAdapter jira, JiraCreateFields fields, CancellationToken ct)
    {
        try
        {
            return await jira.CreateIssueAsync(fields, ct).ConfigureAwait(false);
        }
        catch (JiraApiException ex) when (ex.Status == HttpStatusCode.BadRequest && fields.Issuetype.Name != "Task"
                                          && ex.Message.Contains("issuetype", StringComparison.OrdinalIgnoreCase))
        {
            fields.Issuetype = new JiraNamed { Name = "Task" }; // the project has no Bug type
            return await jira.CreateIssueAsync(fields, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Moves the issue to a status in the "done" category; null on success, otherwise why not.</summary>
    private static async Task<string?> CloseJiraIssueAsync(IJiraApiAdapter jira, string key, string? stateReason, CancellationToken ct)
    {
        var transitions = await jira.GetTransitionsAsync(key, ct).ConfigureAwait(false);
        var resolutions = MigrationText.Resolutions(stateReason);
        var target = transitions.Where(t => string.Equals(t.To?.StatusCategory?.Key, "done", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => resolutions.Any(r => string.Equals(r, t.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(r, t.To?.Name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
        if (target?.Id is null)
        {
            return "not closed: no transition to a done status";
        }

        JiraApiException? last = null;
        foreach (var resolution in new string?[] { null }.Concat(resolutions))
        {
            try
            {
                await jira.TransitionAsync(key, target.Id, resolution, ct).ConfigureAwait(false);
                return null;
            }
            catch (JiraApiException ex) when (ex.Status == HttpStatusCode.BadRequest)
            {
                last = ex; // the screen wants a (different) resolution
            }
        }

        return "not closed: " + last?.Message;
    }

    // ---- Recovering an issue created by an interrupted run -------------------------------------------------------

    /// <summary>
    /// The issue an interrupted run created for <paramref name="key"/> after <paramref name="started"/>: the user's own
    /// issues in the repository (REST listing, which unlike search has no index delay) whose body names the source.
    /// </summary>
    private static async Task<GhIssue?> FindGitHubCopyAsync(GitHubApi github, string repo, string key, string browse, DateTimeOffset started, CancellationToken ct)
    {
        var me = (await github.GetViewerAsync(ct).ConfigureAwait(false)).Login ?? throw new GitHubApiException(HttpStatusCode.Unauthorized, "unknown GitHub user");
        var marker = $"Migrated from Jira [{key}]({browse})";
        return (await github.ListIssuesByCreatorAsync(repo, me, started - TimeSpan.FromMinutes(1), ct).ConfigureAwait(false))
            .Where(i => i.PullRequest is null && i.Body?.Contains(marker, StringComparison.Ordinal) == true)
            .OrderBy(i => i.Number)
            .FirstOrDefault();
    }

    /// <summary>The key of the issue an interrupted run created in <paramref name="project"/> for <paramref name="source"/>.</summary>
    private static async Task<string?> FindJiraCopyAsync(IJiraApiAdapter jira, string project, string source, DateTimeOffset started, DateTimeOffset now,
        CancellationToken ct)
    {
        // A relative date: absolute ones are read in the Jira user's time zone.
        var minutes = (int)Math.Ceiling((now - started).TotalMinutes) + 2;
        var jql = $"project = {project} AND creator = currentUser() AND created >= -{minutes}m ORDER BY created ASC";
        var marker = $"Migrated from GitHub [{source}|";
        var page = await jira.SearchAsync(jql, 50, null, ct, "description").ConfigureAwait(false);
        return page.Issues.FirstOrDefault(i => i.Fields?.Description?.Contains(marker, StringComparison.Ordinal) == true)?.Key;
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------

    private async Task PauseAsync(CancellationToken ct)
    {
        var wait = _lastGitHubWrite + GitHubWritePause - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }

        _lastGitHubWrite = DateTimeOffset.UtcNow;
    }

    private static string Summary(string title)
    {
        var t = title.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length <= 255 ? t : t[..254] + "…";
    }

    private static int Number(string? target)
    {
        var t = target ?? throw new InvalidDataException("Migration record without a target.");
        return int.Parse(t[(t.LastIndexOf('#') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    internal static (string Repo, int Number) ParseRef(string reference, string? defaultRepo)
    {
        var m = IssueRef().Match(reference.Trim());
        if (!m.Success)
        {
            throw new ArgumentException($"Invalid GitHub issue '{reference}'. Expected #12, 12 or owner/repo#12.");
        }

        var repo = m.Groups[1].Success
            ? GitHubService.ValidateRepo(m.Groups[1].Value)
            : defaultRepo ?? throw new ArgumentException($"'{reference}' needs owner/repo (no default repository is configured).");
        return (repo, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"\border\s+by\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrderBy();

    [GeneratedRegex(@"(?:^|\s)repo:", RegexOptions.IgnoreCase)]
    private static partial Regex RepoQualifier();

    [GeneratedRegex(@"(?:^|\s)-?(?:is:(?:issue|pr|pull-request)|type:(?:issue|pr))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TypeQualifier();

    [GeneratedRegex(@"^(?:([\w.-]+/[\w.-]+))?#?(\d{1,9})$")]
    private static partial Regex IssueRef();
}
