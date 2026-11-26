using System.Text.RegularExpressions;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>Settings-page download: a page as Markdown, the same content <c>confluence_get_page</c> returns.</summary>
public sealed partial class ConfluenceService
{
    public async Task<ConversionFile> DownloadPageAsync(string reference, CancellationToken ct)
    {
        var page = await Api.GetPageAsync(ParsePageReference(reference), includeBody: true, ct).ConfigureAwait(false);
        return ConversionFile.FromMarkdown(page.Title, RenderPage(page, storage: false).ToString());
    }

    /// <summary>A page id, or a pasted page URL (…/pages/123/…, …?pageId=123).</summary>
    internal static string ParsePageReference(string? reference)
    {
        var text = reference?.Trim() ?? string.Empty;
        var m = PageIdInUrl().Match(text);
        return m.Success ? m.Groups[1].Value
            : PageId().IsMatch(text) ? text
            : throw new ArgumentException($"'{reference}' is not a page id or a page URL with an id (…/pages/123… or …?pageId=123).");
    }

    [GeneratedRegex(@"(?:/pages/|[?&]pageId=)(\d{1,20})(?!\d)")]
    private static partial Regex PageIdInUrl();
}
