using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SimpleArchitectureMcp.Server.Modules.GitHub;

/// <summary>
/// Tool logic: input validation, repository resolution and compact, token-lean text output. Issue and comment
/// bodies are already Markdown, so they pass through unchanged.
/// </summary>
public sealed partial class GitHubService
{
    public const int MaxSearchLimit = 50;
    public const int MaxComments = 100;

    private readonly Func<GitHubApi> _api;

    /// <param name="api">Resolves the current API client (settings can change at runtime).</param>
    public GitHubService(Func<GitHubApi> api) => _api = api;

    public GitHubService(GitHubApi api) : this(() => api)
    {
    }

    private GitHubApi Api => _api();

    public Task<string> SearchAsync(string query, string? repo, int limit, CancellationToken ct) => SearchAsync(query, repo, limit, 1, ct);

    /// <summary>GitHub search returns at most 1,000 results per query.</summary>
    public const int MaxSearchResults = 1000;

    public async Task<string> SearchAsync(string query, string? repo, int limit, int page, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("query is required.");
        }

        var api = Api;
        var scope = string.IsNullOrWhiteSpace(repo) ? null : ValidateRepo(repo);
        scope ??= HasRepoQualifier().IsMatch(query) ? null : api.DefaultRepo;

        var q = query.Trim();
        if (!TypeQualifier().IsMatch(q))
        {
            q = "is:issue " + q;
        }

        if (scope is not null)
        {
            q = $"repo:{scope} {q}";
        }

        var perPage = Math.Clamp(limit, 1, MaxSearchLimit);
        var p = Math.Max(1, page);
        if ((p - 1) * perPage >= MaxSearchResults)
        {
            throw new ArgumentException($"GitHub search stops at {MaxSearchResults} results; narrow the query (e.g. created:2024-01-01..2024-06-30).");
        }

        var result = await api.SearchIssuesAsync(q, perPage, p, "updated", "desc", ct).ConfigureAwait(false);
        if (result.Items.Count == 0)
        {
            return "No results.";
        }

        // One line per hit: ref | state | title | labels | assignees | updated. Inside one repo, refs are just #n.
        var sb = new StringBuilder();
        foreach (var i in result.Items)
        {
            var itemRepo = RepoFromApiUrl(i.RepositoryUrl);
            sb.Append(scope is not null && string.Equals(itemRepo, scope, StringComparison.OrdinalIgnoreCase) ? string.Empty : itemRepo)
                .Append('#').Append(i.Number).Append(" | ").Append(State(i)).Append(" | ").Append(i.Title)
                .Append(" | ").Append(Join(i.Labels.Select(l => l.Name)))
                .Append(" | ").Append(Join(i.Assignees.Select(a => "@" + a.Login)))
                .Append(" | ").Append(Day(i.UpdatedAt)).Append('\n');
        }

        var seen = (p - 1) * perPage + result.Items.Count;
        if (result.TotalCount > seen)
        {
            sb.Append(seen < MaxSearchResults ? $"({seen} of {result.TotalCount}; next page: {p + 1})" : $"({seen} of {result.TotalCount}; narrow the query for the rest)");
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> GetIssueAsync(string? repo, int number, int comments, CancellationToken ct)
    {
        var api = Api;
        var r = ResolveRepo(api, repo);
        var issue = await api.GetIssueAsync(r, ValidateNumber(number), ct).ConfigureAwait(false);

        var sb = new StringBuilder("---\n");
        sb.Append("issue: ").Append(r).Append('#').Append(issue.Number).Append(issue.PullRequest is null ? "\n" : " (pull request)\n");
        sb.Append("title: ").Append(issue.Title).Append('\n');
        sb.Append("state: ").Append(State(issue)).Append('\n');
        sb.Append("author: @").Append(issue.User?.Login).Append('\n');
        AppendIf(sb, "labels", Join(issue.Labels.Select(l => l.Name), "-"));
        AppendIf(sb, "assignees", Join(issue.Assignees.Select(a => "@" + a.Login), "-"));
        AppendIf(sb, "milestone", issue.Milestone is null ? "-" : $"{issue.Milestone.Title} ({issue.Milestone.Number})");
        sb.Append("created: ").Append(Day(issue.CreatedAt)).Append(", updated: ").Append(Day(issue.UpdatedAt)).Append('\n');
        sb.Append("url: ").Append(issue.HtmlUrl).Append('\n');
        sb.Append("comments: ").Append(issue.Comments).Append('\n');
        sb.Append("---\n").Append(string.IsNullOrWhiteSpace(issue.Body) ? "(no description)" : issue.Body.Trim()).Append('\n');

        var take = Math.Clamp(comments, 0, MaxComments);
        if (take > 0 && issue.Comments > 0)
        {
            var list = await GetLastCommentsAsync(api, r, issue.Number, issue.Comments, take, ct).ConfigureAwait(false);
            sb.Append("\n## Comments");
            if (list.Count < issue.Comments)
            {
                sb.Append(" (last ").Append(list.Count).Append(" of ").Append(issue.Comments).Append(')');
            }

            sb.Append('\n');
            foreach (var c in list)
            {
                sb.Append("\n[").Append(c.Id).Append("] @").Append(c.User?.Login).Append(' ').Append(Day(c.CreatedAt)).Append(":\n")
                    .Append(c.Body?.Trim()).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> CreateIssueAsync(string? repo, string title, string? body, string[]? labels, string[]? assignees,
        int? milestone, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("title is required.");
        }

        var api = Api;
        var r = ResolveRepo(api, repo);
        var labelList = Clean(labels);
        var assigneeList = Clean(assignees, stripAt: true);
        var created = await api.CreateIssueAsync(r, w =>
        {
            w.WriteString("title", title.Trim());
            if (!string.IsNullOrWhiteSpace(body))
            {
                w.WriteString("body", body);
            }

            WriteList(w, "labels", labelList);
            WriteList(w, "assignees", assigneeList);
            if (milestone is > 0)
            {
                w.WriteNumber("milestone", milestone.Value);
            }
        }, ct).ConfigureAwait(false);

        return $"Created {r}#{created.Number} | {created.HtmlUrl}";
    }

    /// <summary>
    /// Edits fields, closes/reopens and adds/removes labels and assignees in one call. Only the given fields change.
    /// </summary>
    public async Task<string> UpdateIssueAsync(string? repo, int number, string? title, string? body, string? state, string? stateReason,
        int? milestone, string[]? addLabels, string[]? removeLabels, string[]? addAssignees, string[]? removeAssignees, CancellationToken ct)
    {
        var api = Api;
        var r = ResolveRepo(api, repo);
        var n = ValidateNumber(number);

        var normalizedState = state?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "open" or "reopen" or "reopened" => "open",
            "closed" or "close" => "closed",
            _ => throw new ArgumentException("state must be 'open' or 'closed'."),
        };
        var reason = stateReason?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "completed" or "not_planned" or "duplicate" or "reopened" => stateReason.Trim().ToLowerInvariant(),
            "not planned" => "not_planned",
            _ => throw new ArgumentException("state_reason must be completed, not_planned, duplicate or reopened."),
        };
        if (reason is not null && normalizedState is null)
        {
            normalizedState = reason == "reopened" ? "open" : "closed";
        }

        var toAdd = Clean(addLabels);
        var toRemove = Clean(removeLabels);
        var assign = Clean(addAssignees, stripAt: true);
        var unassign = Clean(removeAssignees, stripAt: true);

        var patch = title is not null || body is not null || normalizedState is not null || milestone is not null;
        if (!patch && toAdd.Count + toRemove.Count + assign.Count + unassign.Count == 0)
        {
            throw new ArgumentException("Nothing to update.");
        }

        var done = new List<string>();
        if (patch)
        {
            if (title is not null && string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException("title cannot be empty.");
            }

            await api.UpdateIssueAsync(r, n, w =>
            {
                if (title is not null)
                {
                    w.WriteString("title", title.Trim());
                }

                if (body is not null)
                {
                    w.WriteString("body", body);
                }

                if (normalizedState is not null)
                {
                    w.WriteString("state", normalizedState);
                }

                if (reason is not null)
                {
                    w.WriteString("state_reason", reason);
                }

                if (milestone is > 0)
                {
                    w.WriteNumber("milestone", milestone.Value);
                }
                else if (milestone is not null)
                {
                    w.WriteNull("milestone"); // 0 clears it
                }
            }, ct).ConfigureAwait(false);

            if (title is not null) done.Add("title");
            if (body is not null) done.Add("body");
            if (normalizedState is not null) done.Add(reason is null ? normalizedState : $"{normalizedState} ({reason})");
            if (milestone is not null) done.Add(milestone > 0 ? $"milestone {milestone}" : "milestone cleared");
        }

        if (toAdd.Count > 0)
        {
            await api.AddLabelsAsync(r, n, toAdd, ct).ConfigureAwait(false);
            done.Add("+labels " + string.Join(", ", toAdd));
        }

        foreach (var label in toRemove)
        {
            await api.RemoveLabelAsync(r, n, label, ct).ConfigureAwait(false);
        }

        if (toRemove.Count > 0)
        {
            done.Add("-labels " + string.Join(", ", toRemove));
        }

        if (assign.Count > 0)
        {
            await api.AddAssigneesAsync(r, n, assign, ct).ConfigureAwait(false);
            done.Add("+assignees " + string.Join(", ", assign.Select(a => "@" + a)));
        }

        if (unassign.Count > 0)
        {
            await api.RemoveAssigneesAsync(r, n, unassign, ct).ConfigureAwait(false);
            done.Add("-assignees " + string.Join(", ", unassign.Select(a => "@" + a)));
        }

        return $"Updated {r}#{n}: {string.Join("; ", done)}.";
    }

    /// <summary>Adds a comment, or replaces the text of an existing one when <paramref name="commentId"/> is given.</summary>
    public async Task<string> CommentAsync(string? repo, int number, string body, long? commentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("body is required.");
        }

        var api = Api;
        var r = ResolveRepo(api, repo);
        if (commentId is > 0)
        {
            var edited = await api.UpdateCommentAsync(r, commentId.Value, body, ct).ConfigureAwait(false);
            return $"Edited comment {edited.Id} | {edited.HtmlUrl}";
        }

        var created = await api.AddCommentAsync(r, ValidateNumber(number), body, ct).ConfigureAwait(false);
        return $"Commented {created.Id} | {created.HtmlUrl}";
    }

    public async Task<string> ListLabelsAsync(string? repo, CancellationToken ct)
    {
        var api = Api;
        var labels = await api.GetLabelsAsync(ResolveRepo(api, repo), ct).ConfigureAwait(false);
        return labels.Count == 0
            ? "No labels."
            : string.Join('\n', labels.Select(l => string.IsNullOrWhiteSpace(l.Description) ? l.Name : $"{l.Name} | {l.Description}"));
    }

    /// <summary>
    /// The comments endpoint is oldest-first; read the last page(s) to get the most recent ones. Pages are sized to
    /// <paramref name="take"/>, so at most 2 × take comment bodies are downloaded (both pages in parallel).
    /// </summary>
    private static async Task<List<GhComment>> GetLastCommentsAsync(GitHubApi api, string repo, int number, int total, int take, CancellationToken ct)
    {
        var last = Math.Max(1, (total + take - 1) / take);
        var lastTask = api.GetCommentsAsync(repo, number, take, last, ct);
        var partial = total % take != 0 && last > 1; // the last page holds fewer than take comments
        var list = partial
            ? [.. await api.GetCommentsAsync(repo, number, take, last - 1, ct).ConfigureAwait(false), .. await lastTask.ConfigureAwait(false)]
            : await lastTask.ConfigureAwait(false);

        return list.Count <= take ? list : list.GetRange(list.Count - take, take);
    }

    internal static string ResolveRepo(GitHubApi api, string? repo) =>
        !string.IsNullOrWhiteSpace(repo) ? ValidateRepo(repo)
        : api.DefaultRepo ?? throw new ArgumentException("repo is required (owner/repo); no default repository is configured.");

    internal static string ValidateRepo(string repo)
    {
        var trimmed = repo.Trim();
        // Accept a pasted URL like https://github.com/owner/repo or owner/repo.git.
        var m = RepoInUrl().Match(trimmed);
        if (m.Success)
        {
            trimmed = m.Groups[1].Value;
        }

        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return GitHubOptions.RepoName().IsMatch(trimmed) ? trimmed : throw new ArgumentException($"Invalid repo '{repo}'. Expected owner/repo.");
    }

    private static int ValidateNumber(int number) =>
        number > 0 ? number : throw new ArgumentException("number must be a positive issue number.");

    private static string State(GhIssue i) =>
        i.State == "closed" && !string.IsNullOrEmpty(i.StateReason) && i.StateReason != "completed" ? $"closed:{i.StateReason}" : i.State ?? "-";

    internal static string? RepoFromApiUrl(string? url)
    {
        var at = url?.IndexOf("/repos/", StringComparison.Ordinal) ?? -1;
        return at < 0 ? null : url![(at + 7)..];
    }

    private static string Join(IEnumerable<string?> values, string empty = "") =>
        string.Join(", ", values.Where(v => !string.IsNullOrEmpty(v))) is { Length: > 0 } s ? s : empty;

    private static void AppendIf(StringBuilder sb, string key, string value)
    {
        if (value != "-")
        {
            sb.Append(key).Append(": ").Append(value).Append('\n');
        }
    }

    private static string Day(DateTimeOffset? date) =>
        date?.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";

    private static List<string> Clean(string[]? values, bool stripAt = false) =>
        values is null
            ? []
            : values.Select(v => v?.Trim() ?? string.Empty)
                .Select(v => stripAt ? v.TrimStart('@') : v)
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static void WriteList(Utf8JsonWriter w, string name, List<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }

    [GeneratedRegex(@"(?:^|\s)-?(?:repo|org|user):", RegexOptions.IgnoreCase)]
    private static partial Regex HasRepoQualifier();

    [GeneratedRegex(@"(?:^|\s)-?(?:is:(?:issue|pr|pull-request)|type:(?:issue|pr))\b", RegexOptions.IgnoreCase)]
    private static partial Regex TypeQualifier();

    [GeneratedRegex(@"^https?://[^/]+/([^/]+/[^/#?]+)")]
    private static partial Regex RepoInUrl();
}
