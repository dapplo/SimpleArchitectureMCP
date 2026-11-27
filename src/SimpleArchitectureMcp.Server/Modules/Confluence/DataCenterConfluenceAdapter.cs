namespace SimpleArchitectureMcp.Server.Modules.Confluence;

/// <summary>Confluence Data Center / Server: REST API v1 under the configured base URL (context path respected).</summary>
public sealed class DataCenterConfluenceAdapter(IHttpClientFactory httpClientFactory, ConfluenceOptions options)
    : ConfluenceAdapterBase(httpClientFactory, options.GetApiBaseAddress(ConfluenceDeploymentType.DataCenter),
        options.CreateAuthorization(ConfluenceDeploymentType.DataCenter))
{
    private const string Content = "rest/api/content";

    public override ConfluenceDeploymentType DeploymentType => ConfluenceDeploymentType.DataCenter;

    protected override string RestApiPrefix => "rest/api/";

    public override async Task<ConfluencePage> GetPageAsync(string pageId, bool includeBody, CancellationToken ct)
    {
        var content = await GetAsync($"{Content}/{pageId}?expand=version{(includeBody ? ",body.storage" : string.Empty)}",
            ConfluenceJsonContext.Default.V1Content, ct).ConfigureAwait(false);
        return new ConfluencePage(content.Id ?? pageId, content.Title ?? string.Empty, content.Version?.Number ?? 0, content.Body?.Storage?.Value);
    }

    public override async Task<IReadOnlyList<ConfluencePageRef>> GetChildrenAsync(string pageId, int limit, CancellationToken ct)
    {
        var list = await GetAsync($"{Content}/{pageId}/child/page?limit={limit}", ConfluenceJsonContext.Default.V1ContentList, ct).ConfigureAwait(false);
        return list.Results.Where(r => r.Id is not null).Select(r => new ConfluencePageRef(r.Id!, r.Title ?? string.Empty)).ToList();
    }

    public override async Task<ConfluencePageRef> CreatePageAsync(string spaceKey, string? parentId, string title, string storageBody, CancellationToken ct)
    {
        var request = new V1CreateRequest
        {
            Title = title,
            Space = new V1Space { Key = spaceKey },
            Ancestors = string.IsNullOrEmpty(parentId) ? null : [new V1Ancestor { Id = parentId }],
            Body = new V1WriteBody { Storage = new StorageValue { Value = storageBody } },
        };
        var content = await SendAsync(HttpMethod.Post, Content, request,
            ConfluenceJsonContext.Default.V1CreateRequest, ConfluenceJsonContext.Default.V1Content, ct).ConfigureAwait(false);
        return new ConfluencePageRef(content.Id ?? string.Empty, content.Title ?? title, WebUrl(content.Links, BaseAddress));
    }

    public override async Task UpdatePageAsync(string pageId, string title, string storageBody, int newVersion, string? versionMessage, CancellationToken ct)
    {
        var request = new V1UpdateRequest
        {
            Id = pageId,
            Title = title,
            Version = new VersionInfo { Number = newVersion, Message = versionMessage },
            Body = new V1WriteBody { Storage = new StorageValue { Value = storageBody } },
        };
        await SendAsync(HttpMethod.Put, $"{Content}/{pageId}", request,
            ConfluenceJsonContext.Default.V1UpdateRequest, ConfluenceJsonContext.Default.V1Content, ct).ConfigureAwait(false);
    }
}
