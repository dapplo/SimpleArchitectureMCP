using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SimpleArchitectureMcp.Server.Core.Security;

namespace SimpleArchitectureMcp.Server.Modules.Jira;

/// <summary>
/// Deployment-agnostic tool logic: input validation, user resolution ("me", name, email), Markdown ↔ wiki markup,
/// and compact, token-lean text output.
/// </summary>
public sealed partial class JiraService
{
    private readonly Func<IJiraApiAdapter> _adapter;

    /// <param name="adapter">Resolves the current adapter (settings can change at runtime).</param>
    public JiraService(Func<IJiraApiAdapter> adapter) => _adapter = adapter;

    public JiraService(IJiraApiAdapter adapter) : this(() => adapter)
    {
    }

    private IJiraApiAdapter Api => _adapter();

    public const int MaxSearchLimit = 50;
    public const int MaxComments = 50;
    public const long MaxAttachmentBytes = 100L * 1024 * 1024;

    public async Task<string> SearchAsync(string query, int limit, CancellationToken ct) => await SearchAsync(query, limit, null, ct).ConfigureAwait(false);

    public async Task<string> SearchAsync(string query, int limit, string? cursor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("query is required.");
        }

        var result = await Api.SearchAsync(JiraJql.Build(query), Math.Clamp(limit, 1, MaxSearchLimit), Blank(cursor), ct).ConfigureAwait(false);
        if (result.Issues.Count == 0)
        {
            return "No issues.";
        }

        // One line per issue: key | type | status | assignee | updated | summary
        var sb = new StringBuilder();
        foreach (var i in result.Issues)
        {
            var f = i.Fields;
            sb.Append(i.Key).Append(" | ").Append(f?.Issuetype?.Name ?? "-").Append(" | ").Append(f?.Status?.Name ?? "-")
                .Append(" | ").Append(UserName(f?.Assignee) ?? "Unassigned").Append(" | ").Append(Date(f?.Updated) ?? "-")
                .Append(" | ").Append(f?.Summary).Append('\n');
        }

        if (NextCursor(result) is { } next)
        {
            sb.Append(result.Total is { } t ? $"({(result.StartAt ?? 0) + result.Issues.Count} of {t}; next cursor: {next})" : $"(more; next cursor: {next})");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The cursor for the next page (Cloud nextPageToken, Data Center startAt), or null on the last page.</summary>
    public static string? NextCursor(JiraSearchResponse page)
    {
        if (!string.IsNullOrEmpty(page.NextPageToken) && page.IsLast != true)
        {
            return page.NextPageToken;
        }

        var end = (page.StartAt ?? 0) + page.Issues.Count;
        return page.Total is { } total && end < total && page.Issues.Count > 0 ? end.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<string> GetIssueAsync(string issueKey, int comments, CancellationToken ct)
    {
        var key = ValidateKey(issueKey);
        var commentLimit = Math.Clamp(comments, 0, MaxComments);
        var issueTask = Api.GetIssueAsync(key, ct);
        var commentsTask = commentLimit > 0 ? Api.GetCommentsAsync(key, commentLimit, ct) : null;
        var issue = await issueTask.ConfigureAwait(false);
        var f = issue.Fields ?? new JiraIssueFields();

        var sb = new StringBuilder("---\n");
        Line(sb, "key", issue.Key ?? key);
        Line(sb, "summary", f.Summary);
        Line(sb, "type", f.Issuetype?.Name);
        Line(sb, "status", f.Status?.Name);
        Line(sb, "resolution", f.Resolution?.Name);
        Line(sb, "priority", f.Priority?.Name);
        Line(sb, "assignee", UserName(f.Assignee) ?? "Unassigned");
        Line(sb, "reporter", UserName(f.Reporter));
        Line(sb, "labels", f.Labels is { Count: > 0 } labels ? string.Join(", ", labels) : null);
        Line(sb, "parent", f.Parent?.Key is { } parent ? $"{parent} {f.Parent.Fields?.Summary}".TrimEnd() : null);
        Line(sb, "subtasks", f.Subtasks is { Count: > 0 } subs ? string.Join(", ", subs.Select(t => t.Key)) : null);
        Line(sb, "links", f.Issuelinks is { Count: > 0 } links ? string.Join(", ", links.Select(LinkText)) : null);
        Line(sb, "fix versions", f.FixVersions is { Count: > 0 } versions ? string.Join(", ", versions.Select(v => v.Name)) : null);
        Line(sb, "components", f.Components is { Count: > 0 } components ? string.Join(", ", components.Select(c => c.Name)) : null);
        Line(sb, "created", Date(f.Created));
        Line(sb, "updated", Date(f.Updated));
        Line(sb, "url", BrowseUrl(issue.Key ?? key));
        Line(sb, "attachments", f.Attachment is { Count: > 0 } files
            ? string.Join(", ", files.Select(a => $"{a.Filename} ({Size(a.Size)})"))
            : null);
        sb.Append("---\n").Append(JiraWikiConverter.ToMarkdown(f.Description));

        if (commentsTask is not null)
        {
            var page = await commentsTask.ConfigureAwait(false);
            if (page.Comments.Count > 0)
            {
                sb.Append("\n\n## Comments (").Append(page.Comments.Count < page.Total ? $"last {page.Comments.Count} of {page.Total}" : page.Total.ToString(CultureInfo.InvariantCulture)).Append(")\n");
                foreach (var c in Enumerable.Reverse(page.Comments)) // fetched newest first, shown oldest first
                {
                    sb.Append("\n**").Append(UserName(c.Author) ?? "?").Append("** ").Append(Date(c.Created)).Append(":\n")
                        .Append(JiraWikiConverter.ToMarkdown(c.Body)).Append('\n');
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> CreateIssueAsync(string projectKey, string summary, string? issueType, string? description,
        string? assignee, string[]? labels, string? priority, string? parentKey, CancellationToken ct)
    {
        var project = ValidateProjectKey(projectKey);
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("summary is required.");
        }

        var type = string.IsNullOrWhiteSpace(issueType) ? "Task" : issueType.Trim();
        var fields = new JiraCreateFields
        {
            Project = new JiraKeyRef { Key = project },
            Summary = summary.Trim(),
            Issuetype = new JiraNamed { Name = type },
            Description = string.IsNullOrWhiteSpace(description) ? null : JiraWikiConverter.ToWiki(description),
            Labels = ValidateLabels(labels) is { Count: > 0 } l ? l : null,
            Priority = string.IsNullOrWhiteSpace(priority) ? null : new JiraNamed { Name = priority.Trim() },
            Parent = string.IsNullOrWhiteSpace(parentKey) ? null : new JiraKeyRef { Key = ValidateKey(parentKey) },
        };

        // Resolve the assignee first so a typo fails before anything is created.
        ResolvedUser? user = string.IsNullOrWhiteSpace(assignee) ? null : await ResolveUserAsync(assignee, ct).ConfigureAwait(false);
        if (user?.User is { } assigneeUser)
        {
            fields.Assignee = Api.ToRef(assigneeUser);
        }

        JiraCreated created;
        try
        {
            created = await Api.CreateIssueAsync(fields, ct).ConfigureAwait(false);
        }
        catch (JiraApiException ex) when (ex.Status == HttpStatusCode.BadRequest && ex.Message.Contains("issuetype", StringComparison.OrdinalIgnoreCase))
        {
            throw new JiraApiException(ex.Status, ex.Message + await IssueTypeHintAsync(project, ct).ConfigureAwait(false));
        }

        return $"Created {created.Key} | {BrowseUrl(created.Key ?? string.Empty)}";
    }

    public async Task<string> UpdateIssueAsync(string issueKey, string? summary, string? description, string? priority,
        string[]? addLabels, string[]? removeLabels, string? assignee, CancellationToken ct)
    {
        var key = ValidateKey(issueKey);
        var changed = new List<string>();
        var fields = new JiraEditFields();
        if (!string.IsNullOrWhiteSpace(summary))
        {
            fields.Summary = summary.Trim();
            changed.Add("summary");
        }

        if (description is not null)
        {
            fields.Description = JiraWikiConverter.ToWiki(description); // "" clears the description
            changed.Add("description");
        }

        if (!string.IsNullOrWhiteSpace(priority))
        {
            fields.Priority = new JiraNamed { Name = priority.Trim() };
            changed.Add("priority");
        }

        var ops = ValidateLabels(addLabels).Select(l => new JiraLabelOperation { Add = l })
            .Concat(ValidateLabels(removeLabels).Select(l => new JiraLabelOperation { Remove = l }))
            .ToList();
        if (ops.Count > 0)
        {
            changed.Add("labels");
        }

        ResolvedUser? user = string.IsNullOrWhiteSpace(assignee) ? null : await ResolveUserAsync(assignee, ct).ConfigureAwait(false);
        if (changed.Count == 0 && user is null)
        {
            throw new ArgumentException("Nothing to update: pass summary, description, priority, add_labels, remove_labels or assignee.");
        }

        if (changed.Count > 0)
        {
            await Api.UpdateIssueAsync(key, new JiraEditRequest
            {
                Fields = fields.Summary is null && fields.Description is null && fields.Priority is null ? null : fields,
                Update = ops.Count > 0 ? new JiraEditUpdate { Labels = ops } : null,
            }, ct).ConfigureAwait(false);
        }

        if (user is { } u)
        {
            // The assignee endpoint only needs the Assign permission (the field may not be on the edit screen).
            await Api.AssignAsync(key, u.User, ct).ConfigureAwait(false);
            changed.Add($"assignee → {(u.User is null ? "Unassigned" : UserName(u.User))}");
        }

        return $"Updated {key}: {string.Join(", ", changed)}.";
    }

    public async Task<string> TransitionAsync(string issueKey, string? transition, string? comment, string? resolution, CancellationToken ct)
    {
        var key = ValidateKey(issueKey);
        var transitions = await Api.GetTransitionsAsync(key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(transition))
        {
            return transitions.Count == 0
                ? $"No transitions available for {key}."
                : $"Transitions for {key} (id | name → status):\n" + ListTransitions(transitions);
        }

        var wanted = transition.Trim();
        var match = transitions.FirstOrDefault(t => string.Equals(t.Id, wanted, StringComparison.Ordinal))
            ?? transitions.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? transitions.FirstOrDefault(t => string.Equals(t.To?.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No transition '{wanted}' for {key}. Available (id | name → status):\n{ListTransitions(transitions)}");

        await Api.TransitionAsync(key, match.Id!, string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim(), ct).ConfigureAwait(false);
        var result = $"{key} → {match.To?.Name ?? match.Name}.";
        if (!string.IsNullOrWhiteSpace(comment))
        {
            await Api.AddCommentAsync(key, JiraWikiConverter.ToWiki(comment), ct).ConfigureAwait(false);
            result += " Comment added.";
        }

        return result;
    }

    public async Task<string> AddCommentAsync(string issueKey, string body, CancellationToken ct)
    {
        var key = ValidateKey(issueKey);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("body is required.");
        }

        await Api.AddCommentAsync(key, JiraWikiConverter.ToWiki(body), ct).ConfigureAwait(false);
        return $"Commented on {key}.";
    }

    public async Task<string> AttachFileAsync(string issueKey, string filePath, CancellationToken ct)
    {
        var key = ValidateKey(issueKey);
        var file = ValidateAttachmentPath(filePath);
        await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var attached = await Api.AttachAsync(key, file.Name, stream, ct).ConfigureAwait(false);
        var a = attached.FirstOrDefault();
        return $"Attached {a?.Filename ?? file.Name} ({Size(a?.Size is > 0 ? a.Size : file.Length)}) to {key}.";
    }

    /// <summary>"me", "none"/"unassigned", an account id (Cloud), a username, an email or a display name.</summary>
    internal async Task<ResolvedUser> ResolveUserAsync(string who, CancellationToken ct)
    {
        var w = who.Trim();
        if (w.ToLowerInvariant() is "none" or "unassigned" or "-")
        {
            return new ResolvedUser(null);
        }

        if (w.ToLowerInvariant() is "me" or "myself" or "currentuser()")
        {
            return new ResolvedUser(await Api.GetMyselfAsync(ct).ConfigureAwait(false));
        }

        if (Api.DeploymentType == JiraDeploymentType.Cloud && AccountId().IsMatch(w))
        {
            return new ResolvedUser(new JiraUser { AccountId = w, DisplayName = w });
        }

        var users = (await Api.FindUsersAsync(w, ct).ConfigureAwait(false)).Where(u => u.Active != false).ToList();
        var exact = users.Where(u => Matches(u, w)).ToList();
        return (exact.Count == 1 ? exact : users) switch
        {
            [var only] => new ResolvedUser(only),
            [] => throw new ArgumentException($"No active Jira user matches '{w}'."),
            var many => throw new ArgumentException($"'{w}' matches several users, be more specific: " +
                string.Join(", ", many.Take(5).Select(u => u.EmailAddress is { Length: > 0 } e ? $"{UserName(u)} <{e}>" : $"{UserName(u)} ({u.Name ?? u.AccountId})"))),
        };

        static bool Matches(JiraUser u, string w) =>
            string.Equals(u.Name, w, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.Key, w, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.EmailAddress, w, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.DisplayName, w, StringComparison.OrdinalIgnoreCase)
            || string.Equals(u.AccountId, w, StringComparison.Ordinal);
    }

    /// <param name="User">null means unassigned.</param>
    internal readonly record struct ResolvedUser(JiraUser? User);

    private async Task<string> IssueTypeHintAsync(string projectKey, CancellationToken ct)
    {
        try
        {
            var project = await Api.GetProjectAsync(projectKey, ct).ConfigureAwait(false);
            var names = project.IssueTypes?.Select(t => t.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
            return names is { Count: > 0 } ? $" Valid issue types for {projectKey}: {string.Join(", ", names)}." : string.Empty;
        }
        catch (JiraApiException)
        {
            return string.Empty;
        }
    }

    /// <summary>"blocks OPS-9" / "is blocked by OPS-3".</summary>
    internal static string LinkText(JiraIssueLink link) => link.OutwardIssue is { } outward
        ? $"{link.Type?.Outward ?? link.Type?.Name ?? "relates to"} {outward.Key}"
        : $"{link.Type?.Inward ?? link.Type?.Name ?? "relates to"} {link.InwardIssue?.Key}";

    internal string BrowseUrl(string key) => new Uri(Api.SiteBase, "browse/" + key).ToString();

    private static string ListTransitions(IEnumerable<JiraTransition> transitions) =>
        string.Join('\n', transitions.Select(t => $"{t.Id} | {t.Name} → {t.To?.Name ?? "?"}"));

    private static void Line(StringBuilder sb, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.Append(name).Append(": ").Append(value).Append('\n');
        }
    }

    internal static string? UserName(JiraUser? u) => u is null ? null : u.DisplayName ?? u.Name ?? u.AccountId;

    private static string? Date(string? iso) => iso is { Length: >= 10 } ? iso[..10] : iso;

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };

    internal static string ValidateKey(string? key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        return IssueKey().IsMatch(trimmed)
            ? trimmed.ToUpperInvariant()
            : throw new ArgumentException($"Invalid issue key '{key}'. Expected e.g. ABC-123.");
    }

    internal static string ValidateProjectKey(string? key)
    {
        var trimmed = key?.Trim() ?? string.Empty;
        return ProjectKey().IsMatch(trimmed)
            ? trimmed.ToUpperInvariant()
            : throw new ArgumentException($"Invalid project key '{key}'.");
    }

    internal static List<string> ValidateLabels(string[]? labels)
    {
        var result = new List<string>();
        foreach (var label in labels ?? [])
        {
            var l = label?.Trim();
            if (string.IsNullOrEmpty(l))
            {
                continue;
            }

            if (l.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"Invalid label '{l}': Jira labels cannot contain spaces.");
            }

            result.Add(l);
        }

        return result;
    }

    /// <summary>Ordinary local files only, up to 100 MB (see <see cref="LocalFiles.ValidateUpload"/>).</summary>
    internal static FileInfo ValidateAttachmentPath(string? filePath) => LocalFiles.ValidateUpload(filePath, MaxAttachmentBytes, Size);

    [GeneratedRegex(@"^(?:[A-Za-z][A-Za-z0-9_]*-\d{1,10}|\d{1,18})$")]
    private static partial Regex IssueKey();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,49}$")]
    private static partial Regex ProjectKey();

    // Cloud account ids: 24 hex chars, or "<number>:<uuid>"
    [GeneratedRegex(@"^(?:[0-9a-f]{24}|\d+:[0-9a-f-]{36})$")]
    private static partial Regex AccountId();
}
