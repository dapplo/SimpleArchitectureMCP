using System.Net;
using DevToolsMcp.Server.Modules.Confluence;

namespace DevToolsMcp.Tests.Confluence;

public class ConfluenceServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeAdapter : IConfluenceApiAdapter
    {
        public ConfluencePage Page { get; set; } = new("7", "Existing title", 4,
            "<p>keep</p><ac:structured-macro ac:name=\"jira\"><ac:parameter ac:name=\"key\">X-1</ac:parameter></ac:structured-macro>");

        public List<(string Id, string Title, string Body, int Version, string? Message)> Updates { get; } = [];
        public int ConflictsToThrow { get; set; }
        public List<bool> BodyRequests { get; } = [];

        public ConfluenceDeploymentType DeploymentType => ConfluenceDeploymentType.DataCenter;

        public Task<IReadOnlyList<ConfluenceSearchHit>> SearchAsync(string cql, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConfluenceSearchHit>>([new("1", "A", "OPS", "2026-01-01", "ex"), new("2", "B", null, null, null)]);

        public Task<ConfluencePage> GetPageAsync(string pageId, bool includeBody, CancellationToken ct)
        {
            BodyRequests.Add(includeBody);
            return Task.FromResult(Page with { StorageBody = includeBody ? Page.StorageBody : null });
        }

        public Task<IReadOnlyList<ConfluencePageRef>> GetChildrenAsync(string pageId, int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ConfluencePageRef>>([new("8", "Child")]);

        public Task<ConfluencePageRef> CreatePageAsync(string spaceKey, string? parentId, string title, string storageBody, CancellationToken ct) =>
            Task.FromResult(new ConfluencePageRef("9", title, "https://w/9"));

        public Task UpdatePageAsync(string pageId, string title, string storageBody, int newVersion, string? versionMessage, CancellationToken ct)
        {
            if (ConflictsToThrow-- > 0)
            {
                Page = Page with { Version = Page.Version + 1 }; // a concurrent edit happened
                throw new ConfluenceApiException(HttpStatusCode.Conflict, "conflict");
            }

            Updates.Add((pageId, title, storageBody, newVersion, versionMessage));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Update_increments_version_and_keeps_title()
    {
        var api = new FakeAdapter();
        var result = await new ConfluenceService(api).UpdatePageAsync("7", null, "# New", "markdown", "via mcp", Ct);

        Assert.Equal("Updated 7 to version 5.", result);
        var update = Assert.Single(api.Updates);
        Assert.Equal(("7", "Existing title", "<h1>New</h1>", 5, "via mcp"), update);
        Assert.Equal([false], api.BodyRequests); // metadata only, no body round trip
    }

    [Fact]
    public async Task Update_with_storage_format_passes_xhtml_through()
    {
        var api = new FakeAdapter();
        await new ConfluenceService(api).UpdatePageAsync("7", "Renamed", "<p>raw</p>", "storage", null, Ct);
        Assert.Equal(("7", "Renamed", "<p>raw</p>", 5, null), api.Updates.Single());
    }

    [Fact]
    public async Task Update_retries_once_on_version_conflict()
    {
        var api = new FakeAdapter { ConflictsToThrow = 1 };
        var result = await new ConfluenceService(api).UpdatePageAsync("7", null, "x", null, null, Ct);
        Assert.Equal("Updated 7 to version 6.", result);
    }

    [Fact]
    public async Task Append_preserves_existing_storage_and_macros()
    {
        var api = new FakeAdapter();
        var result = await new ConfluenceService(api).AppendToPageAsync("7", "- added", null, Ct);

        Assert.Equal("Appended to 7; now version 5.", result);
        Assert.Equal(api.Page.StorageBody + "<ul><li>added</li></ul>", api.Updates.Single().Body);
    }

    [Fact]
    public async Task Get_page_returns_front_matter_and_markdown()
    {
        var md = await new ConfluenceService(new FakeAdapter()).GetPageAsync("7", null, Ct);
        Assert.Equal("---\nid: 7\ntitle: Existing title\nversion: 4\n---\nkeep\n\n**[Confluence Macro: jira | key=X-1]**", md);
    }

    [Fact]
    public async Task Search_and_children_are_compact()
    {
        var svc = new ConfluenceService(new FakeAdapter());
        Assert.Equal("1 | A | OPS | 2026-01-01\n  ex\n2 | B | - | -", await svc.SearchAsync("q", null, 100, Ct));
        Assert.Equal("8 | Child", await svc.GetChildrenAsync("7", 25, Ct));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1/../2")]
    [InlineData("")]
    public async Task Rejects_non_numeric_page_ids(string id) =>
        await Assert.ThrowsAsync<ArgumentException>(() => new ConfluenceService(new FakeAdapter()).GetPageAsync(id, null, Ct));

    [Fact]
    public async Task Rejects_unknown_format() =>
        await Assert.ThrowsAsync<ArgumentException>(() => new ConfluenceService(new FakeAdapter()).GetPageAsync("7", "html", Ct));

    // Not the temp folder: on Windows that lives under AppData, which the save guard refuses.
    private static string NewSaveDir() => Path.Combine(AppContext.BaseDirectory, "save-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Get_page_save_to_writes_markdown_and_returns_only_a_confirmation()
    {
        var dir = NewSaveDir();
        var path = Path.Combine(dir, "sub", "page.md");
        var result = await new ConfluenceService(new FakeAdapter()).GetPageAsync("7", null, path, Ct);

        Assert.Equal($"Saved 7 'Existing title' v4 to {Path.GetFullPath(path)} (1 KB).", result);
        Assert.Equal("---\nid: 7\ntitle: Existing title\nversion: 4\n---\nkeep\n\n**[Confluence Macro: jira | key=X-1]**", await File.ReadAllTextAsync(path, Ct));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Get_page_save_to_folder_names_the_file_after_the_title()
    {
        var dir = NewSaveDir();
        Directory.CreateDirectory(dir);
        var api = new FakeAdapter();
        api.Page = api.Page with { Title = "Plan: Q1/Q2?" };
        await new ConfluenceService(api).GetPageAsync("7", "storage", dir, Ct);

        var file = Assert.Single(Directory.GetFiles(dir));
        Assert.Equal("Plan_ Q1_Q2_.xhtml", Path.GetFileName(file));
        Assert.Contains("---\n<p>keep</p><ac:structured-macro", await File.ReadAllTextAsync(file, Ct));
        Directory.Delete(dir, recursive: true);
    }

    [Theory]
    [InlineData(".ssh/notes.md")]
    [InlineData("AppData/Roaming/x.md")]
    [InlineData("ok/.bashrc.md")]
    [InlineData("ok/run.sh")]
    [InlineData("ok/page.xhtml")] // markdown goes to .md, .markdown or .txt only
    public async Task Get_page_save_to_refuses_hidden_folders_and_other_extensions(string relative)
    {
        var dir = NewSaveDir();
        var path = Path.Combine(dir, relative);
        await Assert.ThrowsAsync<ArgumentException>(() => new ConfluenceService(new FakeAdapter()).GetPageAsync("7", null, path, Ct));
        Assert.False(File.Exists(path));
    }
}
