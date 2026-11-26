using DevToolsMcp.Server.Modules.Confluence;
using DevToolsMcp.Server.Modules.GitHub;
using DevToolsMcp.Server.Modules.Jira;

namespace DevToolsMcp.Tests.Core;

/// <summary>What people paste into the settings page's "Convert &amp; download" box.</summary>
public class ConversionReferenceTests
{
    [Theory]
    [InlineData("123456", "123456")]
    [InlineData("https://acme.atlassian.net/wiki/spaces/DOC/pages/98765/My+Page", "98765")]
    [InlineData("https://wiki.example.com/pages/viewpage.action?pageId=4242", "4242")]
    public void Confluence_page_id_or_url(string reference, string id) =>
        Assert.Equal(id, ConfluenceService.ParsePageReference(reference));

    [Theory]
    [InlineData("https://wiki.example.com/display/DOC/My+Page")]
    [InlineData("abc")]
    public void Confluence_reference_without_an_id_is_rejected(string reference) =>
        Assert.Throws<ArgumentException>(() => ConfluenceService.ParsePageReference(reference));

    [Theory]
    [InlineData("abc-12", "ABC-12")]
    [InlineData("https://acme.atlassian.net/browse/PROJ-7", "PROJ-7")]
    [InlineData("https://acme.atlassian.net/jira/software/projects/PROJ/boards/1?selectedIssue=PROJ-31", "PROJ-31")]
    public void Jira_key_or_url(string reference, string key) =>
        Assert.Equal(key, JiraService.ParseIssueReference(reference));

    [Theory]
    [InlineData("dapplo/SimpleArchitectureMCP#9", "dapplo/SimpleArchitectureMCP", 9)]
    [InlineData("#12", null, 12)]
    [InlineData("12", null, 12)]
    [InlineData("https://github.com/dapplo/SimpleArchitectureMCP/pull/8", "dapplo/SimpleArchitectureMCP", 8)]
    [InlineData("https://github.example.com/team/repo/issues/77#issuecomment-1", "team/repo", 77)]
    public void GitHub_shorthand_or_url(string reference, string? repo, int number) =>
        Assert.Equal((repo, number), GitHubService.ParseIssueReference(reference));

    [Fact]
    public void GitHub_reference_without_a_number_is_rejected() =>
        Assert.Throws<ArgumentException>(() => GitHubService.ParseIssueReference("https://github.com/dapplo/SimpleArchitectureMCP"));

    [Fact]
    public async Task Confluence_download_is_the_markdown_named_after_the_title()
    {
        var file = await new ConfluenceService(new Confluence.ConfluenceServiceTests.FakeAdapter())
            .DownloadPageAsync("https://x.atlassian.net/wiki/spaces/A/pages/7/T", TestContext.Current.CancellationToken);
        Assert.Equal(("Existing title.md", "text/markdown; charset=utf-8"), (file.FileName, file.ContentType));
        Assert.StartsWith("---\nid: 7\ntitle: Existing title", System.Text.Encoding.UTF8.GetString(file.Content));
    }
}
