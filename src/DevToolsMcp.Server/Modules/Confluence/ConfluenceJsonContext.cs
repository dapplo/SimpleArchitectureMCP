using System.Text.Json.Serialization;

namespace DevToolsMcp.Server.Modules.Confluence;

// Minimal DTOs: only the fields the tools need are mapped; everything else (_expandable, user metadata, ...)
// is skipped by the deserializer and never reaches the model.

public sealed class ConfluenceLinks
{
    public string? Base { get; set; }
    public string? Webui { get; set; }
}

public sealed class StorageValue
{
    public string? Value { get; set; }
    public string Representation { get; set; } = "storage";
}

public sealed class VersionInfo
{
    public int Number { get; set; }
    public string? Message { get; set; }
}

// ---- Cloud REST API v2 -------------------------------------------------------------------------------

public sealed class V2Page
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? SpaceId { get; set; }
    public VersionInfo? Version { get; set; }
    public V2Body? Body { get; set; }

    [JsonPropertyName("_links")]
    public ConfluenceLinks? Links { get; set; }
}

public sealed class V2Body
{
    public StorageValue? Storage { get; set; }
}

public sealed class V2PageRef
{
    public string? Id { get; set; }
    public string? Title { get; set; }
}

public sealed class V2PageList
{
    public List<V2PageRef> Results { get; set; } = [];
}

public sealed class V2Space
{
    public string? Id { get; set; }
    public string? Key { get; set; }
}

public sealed class V2SpaceList
{
    public List<V2Space> Results { get; set; } = [];
}

public sealed class V2CreatePageRequest
{
    public required string SpaceId { get; set; }
    public string Status { get; set; } = "current";
    public required string Title { get; set; }
    public string? ParentId { get; set; }
    public required StorageValue Body { get; set; }
}

public sealed class V2UpdatePageRequest
{
    public required string Id { get; set; }
    public string Status { get; set; } = "current";
    public required string Title { get; set; }
    public required StorageValue Body { get; set; }
    public required VersionInfo Version { get; set; }
}

// ---- REST API v1 (Data Center content API; search on both Cloud and Data Center) ----------------------

public sealed class V1Content
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public V1Space? Space { get; set; }
    public VersionInfo? Version { get; set; }
    public V1Body? Body { get; set; }

    [JsonPropertyName("_links")]
    public ConfluenceLinks? Links { get; set; }
}

public sealed class V1Space
{
    public string? Key { get; set; }
}

public sealed class V1Body
{
    public StorageValue? Storage { get; set; }
}

public sealed class V1ContentList
{
    public List<V1Content> Results { get; set; } = [];
}

public sealed class V1SearchResponse
{
    public List<V1SearchResult> Results { get; set; } = [];
}

public sealed class V1SearchResult
{
    public V1Content? Content { get; set; }
    public string? Title { get; set; }
    public string? Excerpt { get; set; }
    public string? LastModified { get; set; }
    public V1Container? ResultGlobalContainer { get; set; }
}

public sealed class V1Container
{
    public string? DisplayUrl { get; set; }
}

public sealed class V1Ancestor
{
    public required string Id { get; set; }
}

public sealed class V1WriteBody
{
    public required StorageValue Storage { get; set; }
}

public sealed class V1CreateRequest
{
    public string Type { get; set; } = "page";
    public required string Title { get; set; }
    public required V1Space Space { get; set; }
    public List<V1Ancestor>? Ancestors { get; set; }
    public required V1WriteBody Body { get; set; }
}

public sealed class V1UpdateRequest
{
    public required string Id { get; set; }
    public string Type { get; set; } = "page";
    public required string Title { get; set; }
    public required VersionInfo Version { get; set; }
    public required V1WriteBody Body { get; set; }
}

// ---- Errors (v1: message, v2: errors[].title/detail) ---------------------------------------------------

public sealed class ConfluenceErrorResponse
{
    public string? Message { get; set; }
    public List<ConfluenceError>? Errors { get; set; }
}

public sealed class ConfluenceError
{
    public string? Title { get; set; }
    public string? Detail { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(V2Page))]
[JsonSerializable(typeof(V2PageList))]
[JsonSerializable(typeof(V2SpaceList))]
[JsonSerializable(typeof(V2CreatePageRequest))]
[JsonSerializable(typeof(V2UpdatePageRequest))]
[JsonSerializable(typeof(V1Content))]
[JsonSerializable(typeof(V1ContentList))]
[JsonSerializable(typeof(V1SearchResponse))]
[JsonSerializable(typeof(V1CreateRequest))]
[JsonSerializable(typeof(V1UpdateRequest))]
[JsonSerializable(typeof(ConfluenceErrorResponse))]
public sealed partial class ConfluenceJsonContext : JsonSerializerContext;
