using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SimpleArchitectureMcp.Server.Core.Security;

namespace SimpleArchitectureMcp.Server.Modules.Confluence;

/// <summary>
/// Deployment-agnostic tool logic: input validation, Markdown conversion, automatic version increments,
/// and compact, token-lean text output.
/// </summary>
public sealed partial class ConfluenceService
{
    private readonly Func<IConfluenceApiAdapter> _adapter;

    /// <param name="adapter">Resolves the current adapter (settings can change at runtime).</param>
    public ConfluenceService(Func<IConfluenceApiAdapter> adapter) => _adapter = adapter;

    public ConfluenceService(IConfluenceApiAdapter adapter) : this(() => adapter)
    {
    }

    private IConfluenceApiAdapter Api => _adapter();

    public const int MaxSearchLimit = 25;
    public const int MaxChildrenLimit = 100;

    public async Task<string> SearchAsync(string query, string? spaceKey, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("query is required.");
        }

        var hits = await Api.SearchAsync(ConfluenceCql.Build(query, spaceKey), Math.Clamp(limit, 1, MaxSearchLimit), ct).ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return "No results.";
        }

        // One line per hit: id | title | space | date, then the excerpt.
        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            sb.Append(h.Id).Append(" | ").Append(h.Title).Append(" | ").Append(h.SpaceKey ?? "-").Append(" | ").Append(h.LastUpdated ?? "-").Append('\n');
            if (!string.IsNullOrEmpty(h.Excerpt))
            {
                sb.Append("  ").Append(h.Excerpt).Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }

    public async Task<string> GetPageAsync(string pageId, string? format, CancellationToken ct)
    {
        var storage = IsStorage(format);
        var page = await Api.GetPageAsync(ValidateId(pageId), includeBody: true, ct).ConfigureAwait(false);
        return RenderPage(page, storage).ToString();
    }

    /// <summary>
    /// Writes the page (same content as <see cref="GetPageAsync"/>) to a local file and returns only a one-line
    /// confirmation, so the body never enters the conversation.
    /// </summary>
    public async Task<string> ExportPageAsync(string pageId, string? saveTo, string? format, CancellationToken ct) =>
        await ExportPageAsync(pageId, saveTo, format, overwrite: false, ct).ConfigureAwait(false);

    /// <param name="overwrite">Replace an existing file that is not an earlier export.</param>
    public async Task<string> ExportPageAsync(string pageId, string? saveTo, string? format, bool overwrite, CancellationToken ct)
    {
        var storage = IsStorage(format);
        if (string.IsNullOrWhiteSpace(saveTo))
        {
            throw new ArgumentException("save_to is required.");
        }

        var page = await Api.GetPageAsync(ValidateId(pageId), includeBody: true, ct).ConfigureAwait(false);
        var extensions = storage ? StorageExtensions : MarkdownExtensions;
        // Validated after fetching: the default file name comes from the title.
        var file = LocalSavePath.Validate(saveTo, LocalSavePath.FileName(page.Title, extensions[0]), extensions);
        // An earlier export (of any page) may be replaced; other files only when asked.
        LocalSavePath.EnsureMayReplace(file.FullName, overwrite, head => head.StartsWith("---\nid: ", StringComparison.Ordinal));
        var content = RenderPage(page, storage);
        try
        {
            file.Directory?.Create();
            await using (var writer = new StreamWriter(file.FullName, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(content, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Could not write {file.FullName}: {ex.Message}", ex);
        }

        file.Refresh();
        return $"Saved {page.Id} '{page.Title}' v{page.Version} to {file.FullName} ({Math.Max(1, file.Length / 1024)} KB).";
    }

    private static StringBuilder RenderPage(ConfluencePage page, bool storage)
    {
        var header = new StringBuilder($"---\nid: {page.Id}\ntitle: {page.Title}\nversion: {page.Version}\n---\n");
        // The Markdown is appended to the header directly: large pages are not copied once more.
        return storage ? header.Append(page.StorageBody) : ConfluenceMarkupConverter.ToMarkdown(page.StorageBody ?? string.Empty, header);
    }

    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".txt"];
    private static readonly string[] StorageExtensions = [".xhtml", ".html", ".xml", ".txt"];

    public async Task<string> GetChildrenAsync(string pageId, int limit, CancellationToken ct)
    {
        var children = await Api.GetChildrenAsync(ValidateId(pageId), Math.Clamp(limit, 1, MaxChildrenLimit), ct).ConfigureAwait(false);
        return children.Count == 0
            ? "No child pages."
            : string.Join('\n', children.Select(c => $"{c.Id} | {c.Title}"));
    }

    public async Task<string> CreatePageAsync(string spaceKey, string? parentId, string title, string markdownBody, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spaceKey) || string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("space_key and title are required.");
        }

        var created = await Api.CreatePageAsync(spaceKey.Trim(), string.IsNullOrWhiteSpace(parentId) ? null : ValidateId(parentId),
            title.Trim(), ConfluenceMarkupConverter.ToStorage(markdownBody ?? string.Empty), ct).ConfigureAwait(false);
        return $"Created {created.Id} | {created.Title} | {created.WebUrl}";
    }

    /// <summary>Replaces the body; fetches the current version first and writes version + 1 (one retry on conflict).</summary>
    public async Task<string> UpdatePageAsync(string pageId, string? title, string body, string? format, string? versionMessage, CancellationToken ct)
    {
        var id = ValidateId(pageId);
        var storage = IsStorage(format) ? body : ConfluenceMarkupConverter.ToStorage(body ?? string.Empty);
        var version = await WriteWithNextVersionAsync(id, title, _ => storage, includeBody: false, versionMessage, ct).ConfigureAwait(false);
        return $"Updated {id} to version {version}.";
    }

    /// <summary>Appends converted Markdown to the existing storage body (existing macros stay untouched).</summary>
    public async Task<string> AppendToPageAsync(string pageId, string markdownSnippet, string? versionMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(markdownSnippet))
        {
            throw new ArgumentException("body is required.");
        }

        var id = ValidateId(pageId);
        var snippet = ConfluenceMarkupConverter.ToStorage(markdownSnippet);
        var version = await WriteWithNextVersionAsync(id, null, current => (current.StorageBody ?? string.Empty) + snippet,
            includeBody: true, versionMessage, ct).ConfigureAwait(false);
        return $"Appended to {id}; now version {version}.";
    }

    /// <summary>
    /// Puts a draw.io diagram on a page the way the draw.io app for Confluence stores it: the .drawio XML as an
    /// attachment named after the diagram (no extension), an optional PNG preview as &lt;name&gt;.png, and a
    /// <c>drawio</c> macro pointing at it. A page that already shows the diagram only gets its macro revision bumped.
    /// </summary>
    public async Task<string> AddDrawioAsync(string pageId, string filePath, string? name, string? pngPath, string? versionMessage, CancellationToken ct)
    {
        var id = ValidateId(pageId);
        var file = LocalFiles.ValidateUpload(filePath, MaxDiagramBytes, Size, "file");
        if (!file.Extension.Equals(".drawio", StringComparison.OrdinalIgnoreCase) && !file.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{file.Name}: expected a .drawio file.");
        }

        var png = string.IsNullOrWhiteSpace(pngPath) ? null : LocalFiles.ValidateUpload(pngPath, MaxDiagramBytes, Size, "png");
        if (png is not null && !png.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{png.Name}: expected a .png file.");
        }

        var diagram = DiagramName().Replace(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(file.Name) : name.Trim(), "_");
        if (diagram.Length == 0 || diagram.Length > 200)
        {
            throw new ArgumentException("name must be 1-200 characters.");
        }

        ConfluenceAttachment attachment;
        await using (var stream = file.OpenRead())
        {
            attachment = await Api.PutAttachmentAsync(id, diagram, "application/vnd.jgraph.mxfile", stream, ct).ConfigureAwait(false);
        }

        if (png is not null)
        {
            await using var stream = png.OpenRead();
            await Api.PutAttachmentAsync(id, diagram + ".png", "image/png", stream, ct).ConfigureAwait(false);
        }

        var added = false;
        var version = await WriteWithNextVersionAsync(id, null, current =>
        {
            var body = current.StorageBody ?? string.Empty;
            var updated = DrawioMacro.SetRevision(body, diagram, attachment.Version);
            added = updated is null;
            return updated ?? body + DrawioMacro.Create(diagram, id, attachment.Version);
        }, includeBody: true, versionMessage ?? $"draw.io diagram {diagram}", ct).ConfigureAwait(false);

        return $"{(added ? "Added" : "Updated")} draw.io diagram '{diagram}' (attachment v{attachment.Version}{(png is null ? "" : " + PNG preview")}) on {id}; page version {version}.";
    }

    private async Task<int> WriteWithNextVersionAsync(string id, string? title, Func<ConfluencePage, string> body,
        bool includeBody, string? versionMessage, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await Api.GetPageAsync(id, includeBody, ct).ConfigureAwait(false);
            var next = current.Version + 1;
            try
            {
                await Api.UpdatePageAsync(id, string.IsNullOrWhiteSpace(title) ? current.Title : title.Trim(), body(current), next,
                    string.IsNullOrWhiteSpace(versionMessage) ? null : versionMessage, ct).ConfigureAwait(false);
                return next;
            }
            catch (ConfluenceApiException ex) when (ex.Status == HttpStatusCode.Conflict && attempt == 0)
            {
                // Someone else saved in between: re-read the version and try once more.
            }
        }
    }

    private static bool IsStorage(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        null or "" or "markdown" or "md" => false,
        "storage" or "xhtml" => true,
        _ => throw new ArgumentException("format must be 'markdown' or 'storage'."),
    };

    internal static string ValidateId(string? id)
    {
        var trimmed = id?.Trim() ?? string.Empty;
        return PageId().IsMatch(trimmed) ? trimmed : throw new ArgumentException($"Invalid page id '{id}'. Expected a numeric id.");
    }

    [GeneratedRegex(@"^\d{1,20}$")]
    private static partial Regex PageId();

    // Characters Confluence attachment names and the draw.io macro parameter can't carry.
    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]")]
    private static partial Regex DiagramName();

    private const long MaxDiagramBytes = 50L * 1024 * 1024;

    private static string Size(long bytes) => bytes < 1024 * 1024 ? $"{bytes / 1024} KB" : $"{bytes / (1024 * 1024)} MB";
}
