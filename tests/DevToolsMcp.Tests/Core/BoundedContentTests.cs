using System.Net;
using DevToolsMcp.Server.Core.Http;
using DevToolsMcp.Server.Modules.Jira;
using DevToolsMcp.Tests.Jira;

namespace DevToolsMcp.Tests.Core;

public class BoundedContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Limit_holds_without_a_content_length()
    {
        // A chunked (or decompressed) body announces no length.
        var content = new StreamContent(new MemoryStream(new byte[300_000]));
        content.Headers.ContentLength = null;
        Assert.Null(content.Headers.ContentLength);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BoundedContent.ReadAsBytesAsync(content, 100_000, () => new InvalidDataException("too large"), Ct));
        Assert.Equal(5, (await BoundedContent.ReadAsBytesAsync(new ByteArrayContent(new byte[5]), 5, () => new InvalidDataException(), Ct)).Length);
    }

    [Theory]
    [InlineData("http://acme.atlassian.net/rest/api/2/attachment/content/9")]   // https site: never in clear text
    [InlineData("https://acme.atlassian.net:8443/rest/api/2/attachment/content/9")]
    [InlineData("https://evil.example/x")]
    public async Task Jira_credentials_only_go_to_the_site_itself(string url)
    {
        var options = new JiraOptions { BaseUrl = "https://acme.atlassian.net", Email = "me@acme.com", ApiToken = "t" };
        var http = new JiraFakeHttp();
        var adapter = JiraModule.CreateAdapter(options.ResolveDeploymentType(), http, options);

        var ex = await Assert.ThrowsAsync<JiraApiException>(() => adapter.DownloadAsync(url, 1000, Ct));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.Empty(http.Requests);
    }
}
