using System.Net;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>Deployment-specific REST calls. Business rules (versioning, conversion, output shaping) live in <see cref="ConfluenceService"/>.</summary>
public interface IConfluenceApiAdapter
{
    ConfluenceDeploymentType DeploymentType { get; }

    Task<IReadOnlyList<ConfluenceSearchHit>> SearchAsync(string cql, int limit, CancellationToken ct);

    Task<ConfluencePage> GetPageAsync(string pageId, bool includeBody, CancellationToken ct);

    Task<IReadOnlyList<ConfluencePageRef>> GetChildrenAsync(string pageId, int limit, CancellationToken ct);

    Task<ConfluencePageRef> CreatePageAsync(string spaceKey, string? parentId, string title, string storageBody, CancellationToken ct);

    Task UpdatePageAsync(string pageId, string title, string storageBody, int newVersion, string? versionMessage, CancellationToken ct);

    /// <summary>Renders storage-format markup to view HTML (used to detect which macros/apps exist).</summary>
    Task<string> RenderStorageAsync(string storage, CancellationToken ct);

    /// <summary>Creates the page attachment, or adds a new version when one with that file name exists.</summary>
    Task<ConfluenceAttachment> PutAttachmentAsync(string pageId, string fileName, string contentType, Stream content, CancellationToken ct);
}

public sealed record ConfluenceSearchHit(string Id, string Title, string? SpaceKey, string? LastUpdated, string? Excerpt);

public sealed record ConfluencePage(string Id, string Title, int Version, string? StorageBody);

public sealed record ConfluenceAttachment(string Id, string Title, int Version);

public sealed record ConfluencePageRef(string Id, string Title, string? WebUrl = null);

public sealed class ConfluenceApiException(HttpStatusCode status, string message) : Exception(message), Core.Tools.IToolError
{
    public HttpStatusCode Status { get; } = status;
}
