using System.Net;
using BenchmarkDotNet.Attributes;
using DevToolsMcp.Server.Core.Http;
using DevToolsMcp.Server.Modules.Confluence;
using DevToolsMcp.Server.Modules.Jira;
using Microsoft.Extensions.DependencyInjection;

namespace DevToolsMcp.Benchmarks;

/// <summary>
/// confluence_get_page end to end through the real module HttpClient pipeline (payload metrics, resilience),
/// with a canned response instead of the network. <c>Sections</c>: 20 ≈ 35 KB of storage, 300 ≈ 520 KB.
/// </summary>
[MemoryDiagnoser]
public class ConfluenceGetPageBenchmarks
{
    private ServiceProvider _services = null!;
    private ConfluenceService _service = null!;
    private CannedHandler _handler = null!;

    [Params(20, 300)]
    public int Sections { get; set; }

    /// <summary>false: no Content-Length, like a chunked or compressed response.</summary>
    [Params(true, false)]
    public bool KnownLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _handler = new CannedHandler(Samples.V1ContentJson(Samples.ConfluenceStorage(Sections))) { KnownLength = KnownLength };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddModuleHttpClient(ConfluenceModule.HttpClientName, TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => _handler);
        _services = services.BuildServiceProvider();

        var options = new ConfluenceOptions { BaseUrl = "https://wiki.example.com", ApiToken = "pat", DeploymentType = ConfluenceDeploymentType.DataCenter };
        var adapter = new DataCenterConfluenceAdapter(_services.GetRequiredService<IHttpClientFactory>(), options);
        _service = new ConfluenceService(adapter);
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark]
    public Task<string> GetPageMarkdown() => _service.GetPageAsync("123", "markdown", CancellationToken.None);

    [Benchmark]
    public Task<string> GetPageStorage() => _service.GetPageAsync("123", "storage", CancellationToken.None);

    private sealed class CannedHandler(byte[] body) : HttpMessageHandler
    {
        public bool KnownLength { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content = KnownLength
                ? new ByteArrayContent(body)
                : new StreamContent(new UnknownLengthStream(new MemoryStream(body, writable: false)));
            content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>A read-only, non-seekable view, so HttpContent cannot compute a Content-Length.</summary>
    private sealed class UnknownLengthStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } base.Dispose(disposing); }
    }
}

/// <summary>The markup converters on their own (no HTTP).</summary>
[MemoryDiagnoser]
public class ConverterBenchmarks
{
    private string _storage = null!;
    private string _markdown = null!;
    private string _wiki = null!;
    private string _wikiMarkdown = null!;

    [Params(20, 300)]
    public int Sections { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _storage = Samples.ConfluenceStorage(Sections);
        _markdown = ConfluenceMarkupConverter.ToMarkdown(_storage);
        _wiki = Samples.JiraWiki(Sections);
        _wikiMarkdown = JiraWikiConverter.ToMarkdown(_wiki);
    }

    [Benchmark]
    public string StorageToMarkdown() => ConfluenceMarkupConverter.ToMarkdown(_storage);

    [Benchmark]
    public string MarkdownToStorage() => ConfluenceMarkupConverter.ToStorage(_markdown);

    [Benchmark]
    public string WikiToMarkdown() => JiraWikiConverter.ToMarkdown(_wiki);

    [Benchmark]
    public string MarkdownToWiki() => JiraWikiConverter.ToWiki(_wikiMarkdown);
}
