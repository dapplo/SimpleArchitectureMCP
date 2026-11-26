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

        public List<(string Name, string ContentType, int Length)> Attachments { get; } = [];

        public Task<ConfluenceAttachment> PutAttachmentAsync(string pageId, string fileName, string contentType, Stream content, CancellationToken ct)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            Attachments.Add((fileName, contentType, (int)copy.Length));
            return Task.FromResult(new ConfluenceAttachment("att" + Attachments.Count, fileName, Attachments.Count(a => a.Name == fileName) + 2));
        }

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
    [Fact]
    public async Task Add_drawio_uploads_attachments_and_appends_the_macro()
    {
        var dir = Directory.CreateTempSubdirectory("drawio-").FullName;
        try
        {
            var file = Path.Combine(dir, "Web shop.drawio");
            File.WriteAllText(file, "<mxfile/>");
            File.WriteAllBytes(Path.Combine(dir, "p.png"), [1, 2, 3]);
            var fake = new FakeAdapter();
            var service = new ConfluenceService(() => fake);

            var result = await service.AddDrawioAsync("7", file, null, Path.Combine(dir, "p.png"), null, Ct);

            Assert.Equal("Added draw.io diagram 'Web shop' (attachment v3 + PNG preview) on 7; page version 5.", result);
            Assert.Equal([("Web shop", "application/vnd.jgraph.mxfile", 9), ("Web shop.png", "image/png", 3)], fake.Attachments);
            var body = Assert.Single(fake.Updates).Body;
            Assert.StartsWith(fake.Page.StorageBody!, body); // existing content and macros stay
            Assert.Contains("""<ac:structured-macro ac:name="drawio" ac:schema-version="1" ac:macro-id=""", body);
            Assert.Contains("""<ac:parameter ac:name="diagramName">Web shop</ac:parameter>""", body);
            Assert.Contains("""<ac:parameter ac:name="revision">3</ac:parameter>""", body);
            Assert.Contains("""<ac:parameter ac:name="pageId">7</ac:parameter>""", body);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Add_drawio_again_only_bumps_the_revision()
    {
        var dir = Directory.CreateTempSubdirectory("drawio-").FullName;
        try
        {
            var file = Path.Combine(dir, "a.drawio");
            File.WriteAllText(file, "<mxfile/>");
            var fake = new FakeAdapter();
            fake.Page = fake.Page with { StorageBody = "<p>x</p>" + DrawioMacro.Create("Arch & co", "7", 1) };
            var service = new ConfluenceService(() => fake);

            var result = await service.AddDrawioAsync("7", file, "Arch & co", null, null, Ct);

            Assert.StartsWith("Updated draw.io diagram 'Arch & co' (attachment v3)", result);
            var body = Assert.Single(fake.Updates).Body;
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "ac:name=\"drawio\""));
            Assert.Contains("""<ac:parameter ac:name="diagramName">Arch &amp; co</ac:parameter>""", body);
            Assert.Contains("""<ac:parameter ac:name="revision">3</ac:parameter>""", body);
            Assert.DoesNotContain("""<ac:parameter ac:name="revision">1</ac:parameter>""", body);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Add_drawio_validates_inputs()
    {
        var service = new ConfluenceService(() => new FakeAdapter());
        var file = Path.GetTempFileName();
        try
        {
            Assert.Contains("expected a .drawio", (await Assert.ThrowsAsync<ArgumentException>(() => service.AddDrawioAsync("7", file, null, null, null, Ct))).Message);
            Assert.Contains("not found", (await Assert.ThrowsAsync<ArgumentException>(() => service.AddDrawioAsync("7", file + ".drawio", null, null, null, Ct))).Message);
            await Assert.ThrowsAsync<ArgumentException>(() => service.AddDrawioAsync("x", file, null, null, null, Ct));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
