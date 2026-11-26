using DevToolsMcp.Server.Modules.Jira;

namespace DevToolsMcp.Server.Modules.Migration;

/// <summary>Bound from the <c>Migration</c> configuration section. Everything is optional.</summary>
public sealed class MigrationOptions
{
    public const string SectionName = "Migration";
    public const string DefaultBranch = "migrated-attachments";

    /// <summary>
    /// Jira user = GitHub login pairs, separated by commas, semicolons or new lines. The Jira side may be a username,
    /// account id, email or display name. Unmapped users are named in the text but never assigned or @mentioned.
    /// </summary>
    public string? UserMap { get; set; }

    /// <summary>Where the source → target record is kept. Default: migration-map.json next to settings.json.</summary>
    public string? MapFile { get; set; }

    /// <summary>Jira → GitHub: <c>Link</c> (to the Jira attachment, default) or <c>Commit</c> (copy into the repository).</summary>
    public string? Attachments { get; set; }

    /// <summary>Branch for <c>Attachments=Commit</c>; created from the default branch when missing.</summary>
    public string? AttachmentBranch { get; set; }

    public bool CommitAttachments => string.Equals(Attachments?.Trim(), "Commit", StringComparison.OrdinalIgnoreCase);

    public string Branch => string.IsNullOrWhiteSpace(AttachmentBranch) ? DefaultBranch : AttachmentBranch.Trim();

    public UserMap Users() => Migration.UserMap.Parse(UserMap);
}

/// <summary>The Jira ↔ GitHub user table from <see cref="MigrationOptions.UserMap"/>.</summary>
public sealed class UserMap
{
    private readonly List<(string Jira, string GitHub)> _pairs;

    private UserMap(List<(string, string)> pairs) => _pairs = pairs;

    public int Count => _pairs.Count;

    public static UserMap Parse(string? text)
    {
        var pairs = new List<(string, string)>();
        foreach (var entry in (text ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.LastIndexOf('=');
            if (eq <= 0 || eq == entry.Length - 1)
            {
                throw new ArgumentException($"Migration:UserMap entry '{entry}' must look like jira-user=github-login.");
            }

            pairs.Add((entry[..eq].Trim(), entry[(eq + 1)..].Trim().TrimStart('@')));
        }

        return new UserMap(pairs);
    }

    /// <summary>The GitHub login for a Jira user (matched on username, key, account id, email or display name).</summary>
    public string? ToGitHub(JiraUser? user) =>
        user is null ? null : _pairs.FirstOrDefault(p => Matches(p.Jira, user)).GitHub;

    /// <summary>The GitHub login for a Jira username or account id as it appears in [~mention] markup.</summary>
    public string? ToGitHub(string jiraName) =>
        _pairs.FirstOrDefault(p => string.Equals(p.Jira, jiraName, StringComparison.OrdinalIgnoreCase)).GitHub;

    /// <summary>The Jira user (as written in the map) for a GitHub login.</summary>
    public string? ToJira(string? login) =>
        login is null ? null : _pairs.FirstOrDefault(p => string.Equals(p.GitHub, login.TrimStart('@'), StringComparison.OrdinalIgnoreCase)).Jira;

    private static bool Matches(string jira, JiraUser u) =>
        string.Equals(jira, u.Name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(jira, u.Key, StringComparison.OrdinalIgnoreCase)
        || string.Equals(jira, u.AccountId, StringComparison.Ordinal)
        || string.Equals(jira, u.EmailAddress, StringComparison.OrdinalIgnoreCase)
        || string.Equals(jira, u.DisplayName, StringComparison.OrdinalIgnoreCase);
}
