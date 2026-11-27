using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimpleArchitectureMcp.Server.Modules.Confluence;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SimpleArchitectureMcp.Tests.Confluence;

/// <summary>
/// Non-ASCII text (umlauts, ß, €, typographic quotes, emoji) must reach Confluence as the characters themselves:
/// no mojibake, no double-escaped entities, no HTML-only entities in the storage XHTML.
/// </summary>
public class EncodingTests
{
    private const string German = "Größe: 5 € – „Zitat“ für Müller & Söhne <Ä> 😀👍🏽 中文";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Markdown_to_storage_keeps_characters_and_escapes_only_xml()
    {
        var storage = ConfluenceMarkupConverter.ToStorage("# Überschrift\n\n" + German);

        Assert.Equal("<h1>Überschrift</h1><p>Größe: 5 € – „Zitat“ für Müller &amp; Söhne &lt;Ä&gt; 😀👍🏽 中文</p>", storage);
        Assert.DoesNotContain("&amp;amp;", storage);
    }

    [Theory]
    [InlineData("Gr&ouml;&szlig;e &euro;", "Größe €")]
    [InlineData("a&nbsp;b", "a b")]
    [InlineData("&#8222;Zitat&#x201C; &#x1F600;", "„Zitat“ 😀")]
    [InlineData("Tom &amp; Jerry", "Tom &amp; Jerry")] // decoded to '&', then escaped once for XML
    [InlineData("&unknown; &#0; & alone", "&amp;unknown; &amp;#0; &amp; alone")]
    public void Entities_written_in_markdown_are_decoded_not_double_escaped(string markdown, string expectedText)
    {
        Assert.Equal("<p>" + expectedText + "</p>", ConfluenceMarkupConverter.ToStorage(markdown));
    }

    [Fact]
    public void Entities_in_code_stay_literal()
    {
        Assert.Equal("<p><code>&amp;auml;</code></p>", ConfluenceMarkupConverter.ToStorage("`&auml;`"));
        Assert.Contains("<![CDATA[x = \"&auml;\";]]>", ConfluenceMarkupConverter.ToStorage("```\nx = \"&auml;\";\n```"));
    }

    [Fact]
    public void Html_named_entities_in_storage_become_characters()
    {
        var md = ConfluenceMarkupConverter.ToMarkdown("<p>Gr&ouml;&szlig;e &euro; &ndash; &hellip; &#128512;</p>");
        Assert.Equal("Größe € – … 😀", md);
    }

    [Fact]
    public void Literal_entity_text_round_trips()
    {
        // The page shows the text "&auml;" (written by someone documenting HTML); it must not turn into "ä".
        const string storage = "<p>Write &amp;auml; for ä</p>";
        var md = ConfluenceMarkupConverter.ToMarkdown(storage);
        Assert.Equal("Write \\&auml; for ä", md);
        Assert.Equal(storage, ConfluenceMarkupConverter.ToStorage(md));
    }

    [Fact]
    public void Non_ascii_storage_round_trips()
    {
        const string storage = "<h2>Übersicht</h2><p>Größe: 5 € für <strong>Müller &amp; Söhne</strong> 😀👍🏽</p>"
            + "<ul><li>Äpfel</li><li>Öl</li></ul><table><tbody><tr><th>Straße</th></tr><tr><td>中文</td></tr></tbody></table>";
        var md = ConfluenceMarkupConverter.ToMarkdown(storage);
        Assert.Contains("Größe: 5 € für **Müller & Söhne** 😀👍🏽", md);
        Assert.Equal(storage, ConfluenceMarkupConverter.ToStorage(md));
    }

    [Theory]
    [InlineData("https://acme.atlassian.net/wiki", "a@b.c")]
    [InlineData("https://wiki.corp/confluence", null)]
    public async Task Create_sends_utf8_json_and_reads_utf8_responses(string baseUrl, string? email)
    {
        var http = new RecordingHttp();
        var options = new ConfluenceOptions { BaseUrl = baseUrl, Email = email, ApiToken = "t" };
        var type = options.ResolveDeploymentType();
        http.BaseAddress = options.GetApiBaseAddress(type);
        var service = new ConfluenceService(ConfluenceModule.CreateAdapter(type, http, options));

        var result = await service.CreatePageAsync("OPS", null, "Übersicht 😀", German, Ct);

        var post = http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.Equal("application/json", post.ContentType?.MediaType);
        Assert.Equal("utf-8", post.ContentType?.CharSet);
        // Strict UTF-8 decoding throws on invalid bytes (e.g. Latin-1 or a lone surrogate).
        var json = JsonNode.Parse(new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(post.Body))!;
        Assert.Equal("Übersicht 😀", (string?)json["title"]);
        Assert.Equal("<p>Größe: 5 € – „Zitat“ für Müller &amp; Söhne &lt;Ä&gt; 😀👍🏽 中文</p>", (string?)json["body"]!["storage"]?["value"] ?? (string?)json["body"]!["value"]);
        Assert.Contains("Übersicht 😀", result); // title from the UTF-8 response
    }

    [Fact]
    public async Task Get_page_decodes_utf8_response()
    {
        var http = new RecordingHttp();
        var options = new ConfluenceOptions { BaseUrl = "https://wiki.corp/confluence", ApiToken = "t" };
        var type = options.ResolveDeploymentType();
        http.BaseAddress = options.GetApiBaseAddress(type);
        var service = new ConfluenceService(ConfluenceModule.CreateAdapter(type, http, options));

        var page = await service.GetPageAsync("42", "markdown", Ct);

        Assert.Contains("Übersicht 😀", page);
        Assert.Contains("Größe € für Müller & Söhne", page);
    }

    [Fact]
    public async Task Attachment_file_name_with_umlauts_is_sent_as_utf8()
    {
        var http = new RecordingHttp();
        var options = new ConfluenceOptions { BaseUrl = "https://wiki.corp/confluence", ApiToken = "t" };
        var type = options.ResolveDeploymentType();
        http.BaseAddress = options.GetApiBaseAddress(type);
        var api = ConfluenceModule.CreateAdapter(type, http, options);

        using var content = new MemoryStream([1, 2, 3]);
        var attachment = await api.PutAttachmentAsync("42", "Übersicht ß.drawio", "application/vnd.jgraph.mxfile", content, Ct);

        Assert.Equal("Übersicht ß.drawio", attachment.Title);
        var raw = Encoding.UTF8.GetString(http.Requests.Single().Body);
        // RFC 5987 parameter that servers prefer, percent-encoded UTF-8.
        Assert.Contains("filename*=utf-8''%C3%9Cbersicht%20%C3%9F.drawio", raw, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The stdio transport reads and writes raw UTF-8 streams (Console.OpenStandardInput/Output), so the Windows
    /// console code page (e.g. 850 or 1252) never touches MCP messages.
    /// </summary>
    [Fact]
    public async Task Stdio_stream_transport_reads_and_writes_utf8()
    {
        var line = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"confluence_create_page","arguments":{"title":"Übersicht 😀","markdown_body":"Größe € ß"}}}""" + "\n";
        // A pipe, not a MemoryStream: end of input would close the transport before the reply is written.
        var pipe = new System.IO.Pipelines.Pipe();
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(line), Ct);
        using var output = new MemoryStream();
        await using var transport = new StreamServerTransport(pipe.Reader.AsStream(), output, "test");

        var message = await transport.MessageReader.ReadAsync(Ct);
        var request = Assert.IsType<JsonRpcRequest>(message);
        var args = request.Params!["arguments"]!;
        Assert.Equal("Übersicht 😀", (string?)args["title"]);
        Assert.Equal("Größe € ß", (string?)args["markdown_body"]);

        await transport.SendMessageAsync(new JsonRpcResponse
        {
            Id = request.Id,
            Result = JsonSerializer.SerializeToNode(new CallToolResult { Content = [new TextContentBlock { Text = "Größe € 😀" }] }, McpJsonUtilities.DefaultOptions),
        }, Ct);

        var written = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(output.ToArray());
        var text = JsonNode.Parse(written)!["result"]!["content"]![0]!["text"];
        Assert.Equal("Größe € 😀", (string?)text);
    }

    /// <summary>Captures raw request bytes and the Content-Type header; answers with UTF-8 JSON.</summary>
    private sealed class RecordingHttp : HttpMessageHandler, IHttpClientFactory
    {
        public Uri BaseAddress { get; set; } = new("https://x/");

        public List<(HttpMethod Method, string Path, System.Net.Http.Headers.MediaTypeHeaderValue? ContentType, byte[] Body)> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = BaseAddress };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request.Method, path, request.Content?.Headers.ContentType, body));

            var json = path switch
            {
                _ when path.EndsWith("/spaces", StringComparison.Ordinal) => """{"results":[{"id":"777","key":"OPS"}]}""",
                _ when path.EndsWith("/child/attachment", StringComparison.Ordinal) => """{"results":[{"id":"att1","title":"Übersicht ß.drawio","version":{"number":1}}]}""",
                _ when request.Method == HttpMethod.Get => """{"id":"42","title":"Übersicht 😀","version":{"number":2},"body":{"storage":{"value":"<p>Größe &euro; für Müller &amp; Söhne</p>"}}}""",
                _ => """{"id":"50","title":"Übersicht 😀","_links":{"base":"https://x","webui":"/p/50"}}""",
            };

            // UTF-8 bytes without a charset parameter, as some proxies send them: JSON defaults to UTF-8.
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
