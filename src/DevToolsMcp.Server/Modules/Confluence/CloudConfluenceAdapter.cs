using System.Collections.Concurrent;
using System.Net;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>Confluence Cloud: REST API v2 for pages, v1 for CQL search. Paths are relative to the site root.</summary>
public sealed class CloudConfluenceAdapter(IHttpClientFactory httpClientFactory, ConfluenceOptions options)
    : ConfluenceAdapterBase(httpClientFactory)
{
    private const string V2 = "wiki/api/v2/";
    private readonly ConcurrentDictionary<string, string> _spaceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Uri _wikiBase = new(options.GetApiBaseAddress(ConfluenceDeploymentType.Cloud), "wiki/");

    public override ConfluenceDeploymentType DeploymentType => ConfluenceDeploymentType.Cloud;

    protected override string RestApiPrefix => "wiki/rest/api/";

    public override async Task<ConfluencePage> GetPageAsync(string pageId, bool includeBody, CancellationToken ct)
    {
        var page = await GetAsync($"{V2}pages/{pageId}{(includeBody ? "?body-format=storage" : string.Empty)}",
            ConfluenceJsonContext.Default.V2Page, ct).ConfigureAwait(false);
        return new ConfluencePage(page.Id ?? pageId, page.Title ?? string.Empty, page.Version?.Number ?? 0, page.Body?.Storage?.Value);
    }

    public override async Task<IReadOnlyList<ConfluencePageRef>> GetChildrenAsync(string pageId, int limit, CancellationToken ct)
    {
        var list = await GetAsync($"{V2}pages/{pageId}/children?limit={limit}", ConfluenceJsonContext.Default.V2PageList, ct).ConfigureAwait(false);
        return list.Results.Where(r => r.Id is not null).Select(r => new ConfluencePageRef(r.Id!, r.Title ?? string.Empty)).ToList();
    }

    public override async Task<ConfluencePageRef> CreatePageAsync(string spaceKey, string? parentId, string title, string storageBody, CancellationToken ct)
    {
        var request = new V2CreatePageRequest
        {
            SpaceId = await ResolveSpaceIdAsync(spaceKey, ct).ConfigureAwait(false),
            Title = title,
            ParentId = parentId,
            Body = new StorageValue { Value = storageBody },
        };
        var page = await SendAsync(HttpMethod.Post, $"{V2}pages", request,
            ConfluenceJsonContext.Default.V2CreatePageRequest, ConfluenceJsonContext.Default.V2Page, ct).ConfigureAwait(false);
        return new ConfluencePageRef(page.Id ?? string.Empty, page.Title ?? title, WebUrl(page.Links, _wikiBase));
    }

    public override async Task UpdatePageAsync(string pageId, string title, string storageBody, int newVersion, string? versionMessage, CancellationToken ct)
    {
        var request = new V2UpdatePageRequest
        {
            Id = pageId,
            Title = title,
            Body = new StorageValue { Value = storageBody },
            Version = new VersionInfo { Number = newVersion, Message = versionMessage },
        };
        await SendAsync(HttpMethod.Put, $"{V2}pages/{pageId}", request,
            ConfluenceJsonContext.Default.V2UpdatePageRequest, ConfluenceJsonContext.Default.V2Page, ct).ConfigureAwait(false);
    }

    /// <summary>v2 needs the numeric space id; resolve (and cache) it from the key.</summary>
    private async Task<string> ResolveSpaceIdAsync(string spaceKey, CancellationToken ct)
    {
        if (_spaceIds.TryGetValue(spaceKey, out var cached))
        {
            return cached;
        }

        var spaces = await GetAsync($"{V2}spaces?keys={Uri.EscapeDataString(spaceKey)}&limit=1",
            ConfluenceJsonContext.Default.V2SpaceList, ct).ConfigureAwait(false);
        var id = spaces.Results.FirstOrDefault()?.Id
            ?? throw new ConfluenceApiException(HttpStatusCode.NotFound, $"Space '{spaceKey}' not found.");
        _spaceIds[spaceKey] = id;
        return id;
    }
}
