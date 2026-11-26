using System.Text.Json;
using SimpleArchitectureMcp.Server.Modules.Confluence;

namespace SimpleArchitectureMcp.Tests.Confluence;

public class AdapterRoutingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (IConfluenceApiAdapter Adapter, FakeHttp Http) Create(string baseUrl, string? email)
    {
        var options = new ConfluenceOptions { BaseUrl = baseUrl, Email = email, ApiToken = "t" };
        var type = options.ResolveDeploymentType();
        var http = new FakeHttp(options.GetApiBaseAddress(type));
        return (ConfluenceModule.CreateAdapter(type, http, options), http);
    }

    [Fact]
    public void Factory_selects_adapter_by_deployment()
    {
        Assert.IsType<CloudConfluenceAdapter>(Create("https://acme.atlassian.net/wiki", "a@b.c").Adapter);
        Assert.IsType<DataCenterConfluenceAdapter>(Create("https://wiki.corp/confluence", null).Adapter);
    }

    [Fact]
    public async Task Cloud_uses_v2_for_pages_and_v1_for_search()
    {
        var (api, http) = Create("https://acme.atlassian.net/wiki", "a@b.c");
        http.On("GET", "/wiki/api/v2/pages/42?body-format=storage",
                """{"id":"42","title":"T","version":{"number":3},"body":{"storage":{"value":"<p>x</p>","representation":"storage"}},"_links":{"webui":"/x"},"_expandable":{"a":"b"}}""")
            .On("GET", "/wiki/api/v2/pages/42/children?limit=5", """{"results":[{"id":"43","title":"C"}]}""")
            .On("GET", "/wiki/api/v2/spaces?keys=OPS&limit=1", """{"results":[{"id":"777","key":"OPS"}]}""")
            .On("POST", "/wiki/api/v2/pages", """{"id":"50","title":"New","_links":{"base":"https://acme.atlassian.net/wiki","webui":"/spaces/OPS/pages/50/New"}}""")
            .On("GET", "/wiki/rest/api/search?cql=text%20~%20%22x%22&limit=3&excerpt=indexed&expand=content.space",
                """{"results":[{"content":{"id":"42","title":"T","space":{"key":"OPS"}},"excerpt":"a @@@hl@@@b@@@endhl@@@ &amp; c","lastModified":"2026-01-02T03:04:05.000Z"},{"title":"a space","excerpt":""}]}""");

        var page = await api.GetPageAsync("42", includeBody: true, Ct);
        Assert.Equal(new ConfluencePage("42", "T", 3, "<p>x</p>"), page);

        var children = await api.GetChildrenAsync("42", 5, Ct);
        Assert.Equal([new ConfluencePageRef("43", "C")], children);

        var created = await api.CreatePageAsync("OPS", "42", "New", "<p>b</p>", Ct);
        Assert.Equal("https://acme.atlassian.net/wiki/spaces/OPS/pages/50/New", created.WebUrl);
        var post = JsonDocument.Parse(http.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement;
        Assert.Equal("777", post.GetProperty("spaceId").GetString());
        Assert.Equal("42", post.GetProperty("parentId").GetString());
        Assert.Equal("storage", post.GetProperty("body").GetProperty("representation").GetString());

        var hits = await api.SearchAsync("text ~ \"x\"", 3, Ct);
        Assert.Equal([new ConfluenceSearchHit("42", "T", "OPS", "2026-01-02", "a b & c")], hits);
    }

    [Fact]
    public async Task Cloud_update_puts_version_to_v2()
    {
        var (api, http) = Create("https://acme.atlassian.net", "a@b.c");
        http.On("PUT", "/wiki/api/v2/pages/42", """{"id":"42"}""");

        await api.UpdatePageAsync("42", "T", "<p>y</p>", 8, "msg", Ct);

        var put = JsonDocument.Parse(http.Requests.Single().Body!).RootElement;
        Assert.Equal(8, put.GetProperty("version").GetProperty("number").GetInt32());
        Assert.Equal("msg", put.GetProperty("version").GetProperty("message").GetString());
        Assert.Equal("current", put.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DataCenter_uses_content_api_under_context_path()
    {
        var (api, http) = Create("https://wiki.corp/confluence", null);
        http.On("GET", "/confluence/rest/api/content/42?expand=version,body.storage",
                """{"id":"42","title":"T","version":{"number":9},"body":{"storage":{"value":"<p>x</p>"}}}""")
            .On("GET", "/confluence/rest/api/content/42/child/page?limit=2", """{"results":[{"id":"43","title":"C"}]}""")
            .On("POST", "/confluence/rest/api/content", """{"id":"60","title":"New","_links":{"base":"https://wiki.corp/confluence","webui":"/display/OPS/New"}}""")
            .On("PUT", "/confluence/rest/api/content/42", """{"id":"42"}""")
            .On("GET", "/confluence/rest/api/search?cql=x&limit=1&excerpt=indexed&expand=content.space", """{"results":[]}""");

        Assert.Equal(9, (await api.GetPageAsync("42", includeBody: true, Ct)).Version);
        Assert.Single(await api.GetChildrenAsync("42", 2, Ct));
        Assert.Empty(await api.SearchAsync("x", 1, Ct));

        var created = await api.CreatePageAsync("OPS", "42", "New", "<p>b</p>", Ct);
        Assert.Equal("https://wiki.corp/confluence/display/OPS/New", created.WebUrl);
        var post = JsonDocument.Parse(http.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement;
        Assert.Equal("OPS", post.GetProperty("space").GetProperty("key").GetString());
        Assert.Equal("42", post.GetProperty("ancestors")[0].GetProperty("id").GetString());
        Assert.Equal("page", post.GetProperty("type").GetString());

        await api.UpdatePageAsync("42", "T", "<p>y</p>", 10, null, Ct);
        var put = JsonDocument.Parse(http.Requests.Single(r => r.Method == HttpMethod.Put).Body!).RootElement;
        Assert.Equal(10, put.GetProperty("version").GetProperty("number").GetInt32());
        Assert.False(put.GetProperty("version").TryGetProperty("message", out _)); // nulls are not sent
    }

    [Fact]
    public async Task Errors_surface_confluence_message()
    {
        var (api, http) = Create("https://acme.atlassian.net", "a@b.c");
        http.On("GET", "/wiki/api/v2/pages/1", """{"errors":[{"status":404,"title":"Not Found","detail":"Page 1 does not exist"}]}""", System.Net.HttpStatusCode.NotFound);

        var ex = await Assert.ThrowsAsync<ConfluenceApiException>(() => api.GetPageAsync("1", includeBody: false, Ct));
        Assert.Equal("Confluence API 404: Page 1 does not exist", ex.Message);
    }
    [Theory]
    [InlineData("https://acme.atlassian.net/wiki", "a@b.c", "/wiki/rest/api/content/42/child/attachment")]
    [InlineData("https://wiki.corp/confluence", null, "/confluence/rest/api/content/42/child/attachment")]
    public async Task Attachments_use_the_v1_create_or_update_endpoint(string baseUrl, string? email, string path)
    {
        var (api, http) = Create(baseUrl, email);
        http.On("PUT", path, """{"results":[{"id":"att9","title":"Diagram","version":{"number":4}}]}""");

        var attachment = await api.PutAttachmentAsync("42", "Diagram", "application/vnd.jgraph.mxfile", new MemoryStream("<mxfile/>"u8.ToArray()), Ct);

        Assert.Equal(new ConfluenceAttachment("att9", "Diagram", 4), attachment);
        var request = Assert.Single(http.Requests);
        Assert.Contains("<mxfile/>", request.Body);
        Assert.Contains("name=\"minorEdit\"", request.Body);
    }
}
