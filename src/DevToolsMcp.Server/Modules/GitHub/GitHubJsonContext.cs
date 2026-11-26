using System.Text.Json.Serialization;

namespace DevToolsMcp.Server.Modules.GitHub;

// Minimal DTOs: only the fields the tools print are mapped. Everything else in GitHub's (large) issue payloads,
// such as reactions, node ids, *_url templates and full user objects, is skipped by the deserializer.

public sealed class GhUser
{
    public string? Login { get; set; }
}

public sealed class GhLabel
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}

public sealed class GhMilestone
{
    public int Number { get; set; }
    public string? Title { get; set; }
}

public sealed class GhIssue
{
    /// <summary>Database id (sub-issue links use it, not the number).</summary>
    public long Id { get; set; }

    public int Number { get; set; }
    public string? Title { get; set; }
    public string? State { get; set; }
    public string? StateReason { get; set; }
    public string? Body { get; set; }
    public GhUser? User { get; set; }
    public List<GhLabel> Labels { get; set; } = [];
    public List<GhUser> Assignees { get; set; } = [];
    public GhMilestone? Milestone { get; set; }
    public int Comments { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? HtmlUrl { get; set; }

    /// <summary>https://api.github.com/repos/owner/repo (search results span repositories).</summary>
    public string? RepositoryUrl { get; set; }

    /// <summary>Present when the "issue" is a pull request.</summary>
    public GhPullRef? PullRequest { get; set; }
}

public sealed class GhPullRef
{
    public string? Url { get; set; }
}

public sealed class GhSearchResult
{
    public int TotalCount { get; set; }
    public List<GhIssue> Items { get; set; } = [];
}

public sealed class GhComment
{
    public long Id { get; set; }
    public GhUser? User { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public string? HtmlUrl { get; set; }
}

public sealed class GhRepo
{
    public string? DefaultBranch { get; set; }

    /// <summary>What the token's user may do in the repository (only present on authenticated requests).</summary>
    public GhRepoPermissions? Permissions { get; set; }
}

public sealed class GhRepoPermissions
{
    public bool Push { get; set; }
}

public sealed class GhGitObject
{
    public string? Sha { get; set; }
}

public sealed class GhRef
{
    public GhGitObject? Object { get; set; }
}

public sealed class GhContentFile
{
    public string? Path { get; set; }
    public string? HtmlUrl { get; set; }
    public string? DownloadUrl { get; set; }
}

public sealed class GhContentResult
{
    public GhContentFile? Content { get; set; }
}

public sealed class GhErrorResponse
{
    public string? Message { get; set; }
    public List<GhErrorDetail>? Errors { get; set; }
}

public sealed class GhErrorDetail
{
    public string? Field { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GhUser))]
[JsonSerializable(typeof(GhIssue))]
[JsonSerializable(typeof(GhSearchResult))]
[JsonSerializable(typeof(List<GhComment>))]
[JsonSerializable(typeof(GhComment))]
[JsonSerializable(typeof(List<GhLabel>))]
[JsonSerializable(typeof(List<GhIssue>))]
[JsonSerializable(typeof(GhMilestone))]
[JsonSerializable(typeof(List<GhMilestone>))]
[JsonSerializable(typeof(GhRepo))]
[JsonSerializable(typeof(GhRef))]
[JsonSerializable(typeof(GhContentResult))]
[JsonSerializable(typeof(GhErrorResponse))]
[JsonSerializable(typeof(string[]))] // array tool parameters (labels, assignees)
public sealed partial class GitHubJsonContext : JsonSerializerContext;
