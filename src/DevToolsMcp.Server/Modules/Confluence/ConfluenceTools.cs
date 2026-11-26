using System.ComponentModel;
using DevToolsMcp.Server.Core.Telemetry;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>
/// The eight Confluence tools. Descriptions are intentionally terse: every word here is paid for in every prompt.
/// </summary>
[McpServerToolType]
public sealed class ConfluenceTools(ConfluenceService confluence)
{
    [McpServerTool(Name = "confluence_search", ReadOnly = true, Idempotent = true)]
    [Description("Search Confluence. Returns lines: id | title | space | updated, plus excerpt.")]
    public Task<string> Search(
        [Description("CQL, or plain text")] string query,
        [Description("Space key filter")] string? space_key = null,
        [Description("Max 25")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        Run("confluence_search", () => confluence.SearchAsync(query, space_key, limit, cancellationToken));

    [McpServerTool(Name = "confluence_get_page", ReadOnly = true, Idempotent = true)]
    [Description("Get a page (id, title, version, body). Macros appear as **[Confluence Macro: name]**; to edit pages with complex macros, use format=storage here and in confluence_update_page.")]
    public Task<string> GetPage(
        string page_id,
        [Description("markdown or storage")] string format = "markdown",
        CancellationToken cancellationToken = default) =>
        Run("confluence_get_page", () => confluence.GetPageAsync(page_id, format, cancellationToken));

    [McpServerTool(Name = "confluence_export_page", Destructive = false, Idempotent = true)]
    [Description("Save a page as a local Markdown file (like confluence_get_page) without returning its body.")]
    public Task<string> ExportPage(
        string page_id,
        [Description(".md file, or folder (file named after the title)")] string save_to,
        [Description("markdown or storage")] string format = "markdown",
        [Description("Replace an existing file that is not an earlier export")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        Run("confluence_export_page", () => confluence.ExportPageAsync(page_id, save_to, format, overwrite, cancellationToken));

    [McpServerTool(Name = "confluence_get_page_children", ReadOnly = true, Idempotent = true)]
    [Description("List child pages as id | title.")]
    public Task<string> GetPageChildren(
        string page_id,
        int limit = 25,
        CancellationToken cancellationToken = default) =>
        Run("confluence_get_page_children", () => confluence.GetChildrenAsync(page_id, limit, cancellationToken));

    [McpServerTool(Name = "confluence_create_page")]
    [Description("Create a page from Markdown. Returns id and URL.")]
    public Task<string> CreatePage(
        string space_key,
        string title,
        string markdown_body,
        [Description("Parent page id")] string? parent_id = null,
        CancellationToken cancellationToken = default) =>
        Run("confluence_create_page", () => confluence.CreatePageAsync(space_key, parent_id, title, markdown_body, cancellationToken));

    [McpServerTool(Name = "confluence_update_page", Idempotent = true)]
    [Description("Replace a page body; version is incremented automatically. Omit title to keep it.")]
    public Task<string> UpdatePage(
        string page_id,
        [Description("Markdown, or storage XHTML if format=storage")] string markdown_body,
        string? title = null,
        [Description("markdown or storage")] string format = "markdown",
        string? version_message = null,
        CancellationToken cancellationToken = default) =>
        Run("confluence_update_page", () => confluence.UpdatePageAsync(page_id, title, markdown_body, format, version_message, cancellationToken));

    [McpServerTool(Name = "confluence_append_to_page")]
    [Description("Append Markdown to the end of a page without resending its body. Existing macros are preserved.")]
    public Task<string> AppendToPage(
        string page_id,
        string markdown_snippet,
        string? version_message = null,
        CancellationToken cancellationToken = default) =>
        Run("confluence_append_to_page", () => confluence.AppendToPageAsync(page_id, markdown_snippet, version_message, cancellationToken));

    [McpServerTool(Name = "confluence_add_drawio")]
    [Description("Upload a .drawio file to a page and show it with the draw.io macro (draw.io app required). Re-running updates the diagram in place.")]
    public Task<string> AddDrawio(
        string page_id,
        [Description("Local .drawio file")] string file,
        [Description("Diagram name; default the file name")] string? name = null,
        [Description("Optional PNG preview")] string? png = null,
        CancellationToken cancellationToken = default) =>
        Run("confluence_add_drawio", () => confluence.AddDrawioAsync(page_id, file, name, png, null, cancellationToken));

    /// <summary>Maps failures to concise MCP tool errors and records the output size.</summary>
    private static async Task<string> Run(string tool, Func<Task<string>> action)
    {
        try
        {
            return DevToolsTelemetry.Output(tool, await action().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is ConfluenceApiException or ConfluenceNotConfiguredException or ArgumentException or IOException or HttpRequestException or TimeoutException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
