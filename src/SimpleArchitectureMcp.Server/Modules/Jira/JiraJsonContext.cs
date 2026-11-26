using System.Text.Json.Serialization;

namespace SimpleArchitectureMcp.Server.Modules.Jira;

// Minimal DTOs for Jira REST API v2 (Cloud and Data Center). v2 is used on both because it reads and writes
// descriptions and comments as wiki markup strings (Cloud v3 would need Atlassian Document Format).
// Only the fields the tools need are mapped; everything else (avatars, self links, schemas, ...) is skipped.

public sealed class JiraNamed
{
    public string? Id { get; set; }
    public string? Name { get; set; }

    /// <summary>On statuses: key new, indeterminate or done.</summary>
    public JiraKeyRef? StatusCategory { get; set; }
}

public sealed class JiraKeyRef
{
    public string? Key { get; set; }
}

public sealed class JiraUser
{
    /// <summary>Cloud identifier.</summary>
    public string? AccountId { get; set; }

    /// <summary>Data Center username.</summary>
    public string? Name { get; set; }

    public string? Key { get; set; }
    public string? DisplayName { get; set; }
    public string? EmailAddress { get; set; }
    public bool? Active { get; set; }
}

public sealed class JiraAttachment
{
    public string? Id { get; set; }
    public string? Filename { get; set; }
    public long Size { get; set; }
    public string? MimeType { get; set; }

    /// <summary>Download URL (needs the same credentials).</summary>
    public string? Content { get; set; }
}

public sealed class JiraIssueLinkType
{
    public string? Name { get; set; }
    public string? Inward { get; set; }
    public string? Outward { get; set; }
}

/// <summary>One of <c>inwardIssue</c>/<c>outwardIssue</c> is set: the other end of the link.</summary>
public sealed class JiraIssueLink
{
    public JiraIssueLinkType? Type { get; set; }
    public JiraParent? InwardIssue { get; set; }
    public JiraParent? OutwardIssue { get; set; }
}

public sealed class JiraParent
{
    public string? Key { get; set; }
    public JiraParentFields? Fields { get; set; }
}

public sealed class JiraParentFields
{
    public string? Summary { get; set; }
}

public sealed class JiraIssueFields
{
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public JiraNamed? Status { get; set; }
    public JiraNamed? Issuetype { get; set; }
    public JiraNamed? Priority { get; set; }
    public JiraNamed? Resolution { get; set; }
    public JiraUser? Assignee { get; set; }
    public JiraUser? Reporter { get; set; }
    public List<string>? Labels { get; set; }
    public JiraParent? Parent { get; set; }
    public List<JiraAttachment>? Attachment { get; set; }
    public List<JiraParent>? Subtasks { get; set; }
    public List<JiraIssueLink>? Issuelinks { get; set; }
    public List<JiraNamed>? FixVersions { get; set; }
    public List<JiraNamed>? Components { get; set; }
    public string? Created { get; set; }
    public string? Updated { get; set; }
}

public sealed class JiraIssue
{
    public string? Id { get; set; }
    public string? Key { get; set; }
    public JiraIssueFields? Fields { get; set; }
}

/// <summary>Cloud <c>/search/jql</c> (nextPageToken, isLast) and Data Center <c>/search</c> (total).</summary>
public sealed class JiraSearchResponse
{
    public List<JiraIssue> Issues { get; set; } = [];
    public int? Total { get; set; }
    public int? StartAt { get; set; }
    public bool? IsLast { get; set; }
    public string? NextPageToken { get; set; }
}

public sealed class JiraComment
{
    public string? Id { get; set; }
    public JiraUser? Author { get; set; }
    public string? Body { get; set; }
    public string? Created { get; set; }
}

public sealed class JiraCommentPage
{
    public List<JiraComment> Comments { get; set; } = [];
    public int Total { get; set; }
    public int StartAt { get; set; }
}

public sealed class JiraTransition
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JiraNamed? To { get; set; }
}

public sealed class JiraTransitionList
{
    public List<JiraTransition> Transitions { get; set; } = [];
}

public sealed class JiraProject
{
    public string? Key { get; set; }
    public List<JiraNamed>? IssueTypes { get; set; }
    public List<JiraNamed>? Versions { get; set; }
}

public sealed class JiraCreated
{
    public string? Id { get; set; }
    public string? Key { get; set; }
}

// ---- Requests -----------------------------------------------------------------------------------------------

/// <summary>A user reference: <c>accountId</c> on Cloud, <c>name</c> on Data Center.</summary>
public sealed class JiraUserRef
{
    public string? AccountId { get; set; }
    public string? Name { get; set; }
}

public sealed class JiraCreateFields
{
    public required JiraKeyRef Project { get; set; }
    public required string Summary { get; set; }
    public required JiraNamed Issuetype { get; set; }
    public string? Description { get; set; }
    public List<string>? Labels { get; set; }
    public JiraNamed? Priority { get; set; }
    public JiraKeyRef? Parent { get; set; }
    public JiraUserRef? Assignee { get; set; }
    public List<JiraNamed>? FixVersions { get; set; }
}

public sealed class JiraCreateRequest
{
    public required JiraCreateFields Fields { get; set; }
}

public sealed class JiraEditFields
{
    public string? Summary { get; set; }
    public string? Description { get; set; }
    public JiraNamed? Priority { get; set; }
}

public sealed class JiraLabelOperation
{
    public string? Add { get; set; }
    public string? Remove { get; set; }
}

public sealed class JiraEditUpdate
{
    public List<JiraLabelOperation>? Labels { get; set; }
}

public sealed class JiraEditRequest
{
    public JiraEditFields? Fields { get; set; }
    public JiraEditUpdate? Update { get; set; }
}

/// <summary>Cloud assignee body; a null <c>accountId</c> must be sent explicitly to unassign.</summary>
public sealed class JiraCloudAssignRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AccountId { get; set; }
}

/// <summary>Data Center assignee body; a null <c>name</c> must be sent explicitly to unassign.</summary>
public sealed class JiraDataCenterAssignRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Name { get; set; }
}

public sealed class JiraTransitionFields
{
    public JiraNamed? Resolution { get; set; }
}

public sealed class JiraTransitionRequest
{
    public required JiraNamed Transition { get; set; }
    public JiraTransitionFields? Fields { get; set; }
}

public sealed class JiraCommentRequest
{
    public required string Body { get; set; }
}

// ---- Errors: {"errorMessages":["..."],"errors":{"field":"message"}} -------------------------------------------

public sealed class JiraErrorResponse
{
    public List<string>? ErrorMessages { get; set; }
    public Dictionary<string, string>? Errors { get; set; }
    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JiraIssue))]
[JsonSerializable(typeof(JiraSearchResponse))]
[JsonSerializable(typeof(JiraCommentPage))]
[JsonSerializable(typeof(JiraTransitionList))]
[JsonSerializable(typeof(JiraProject))]
[JsonSerializable(typeof(JiraCreated))]
[JsonSerializable(typeof(JiraUser))]
[JsonSerializable(typeof(List<JiraUser>))]
[JsonSerializable(typeof(List<JiraAttachment>))]
[JsonSerializable(typeof(JiraComment))]
[JsonSerializable(typeof(JiraCreateRequest))]
[JsonSerializable(typeof(JiraEditRequest))]
[JsonSerializable(typeof(JiraCloudAssignRequest))]
[JsonSerializable(typeof(JiraDataCenterAssignRequest))]
[JsonSerializable(typeof(JiraTransitionRequest))]
[JsonSerializable(typeof(JiraCommentRequest))]
[JsonSerializable(typeof(JiraErrorResponse))]
[JsonSerializable(typeof(string[]))]
public sealed partial class JiraJsonContext : JsonSerializerContext;
