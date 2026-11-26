using System.ComponentModel;
using DevToolsMcp.Server.Core.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Modules.Confluence;

/// <summary>
/// The eight Confluence tools. Descriptions are intentionally terse: every word here is paid for in every prompt.
/// </summary>
[McpServerToolType]
public sealed class ConfluenceTools(ConfluenceService confluence)
{
    private const string Service = "Confluence";

    [McpServerTool(Name = "confluence_search", ReadOnly = true, Idempotent = true)]
    [Description("Search Confluence. Returns lines: id | title | space | updated, plus excerpt.")]
    public Task<string> Search(
        [Description("CQL, or plain text")] string query,
        [Description("Space key filter")] string? space_key = null,
        [Description("Max 25")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_search", Service, () => confluence.SearchAsync(query, space_key, limit, cancellationToken), cancellationToken);

    [McpServerTool(Name = "confluence_get_page", ReadOnly = true, Idempotent = true)]
    [Description("Get a page (id, title, version, body). Macros appear as **[Confluence Macro: name]**; to edit pages with complex macros, use format=storage here and in confluence_update_page.")]
    public Task<string> GetPage(
        string page_id,
        [Description("markdown or storage")] string format = "markdown",
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_get_page", Service, () => confluence.GetPageAsync(page_id, format, cancellationToken), cancellationToken);

    [McpServerTool(Name = "confluence_export_page", Destructive = false, Idempotent = true)]
    [Description("Save a page as a local Markdown file (like confluence_get_page) without returning its body.")]
    public Task<string> ExportPage(
        string page_id,
        [Description(".md file, or folder (file named after the title)")] string save_to,
        [Description("markdown or storage")] string format = "markdown",
        [Description("Replace an existing file that is not an earlier export")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_export_page", Service, () => confluence.ExportPageAsync(page_id, save_to, format, overwrite, cancellationToken), cancellationToken);

    [McpServerTool(Name = "confluence_get_page_children", ReadOnly = true, Idempotent = true)]
    [Description("List child pages as id | title.")]
    public Task<string> GetPageChildren(
        string page_id,
        int limit = 25,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_get_page_children", Service, () => confluence.GetChildrenAsync(page_id, limit, cancellationToken), cancellationToken);

    [McpServerTool(Name = "confluence_create_page", Destructive = false)]
    [Description("Create a page from Markdown. Returns id and URL.")]
    public Task<string> CreatePage(
        string space_key,
        string title,
        [Description("Markdown")] string body,
        [Description("Parent page id")] string? parent_id = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_create_page", Service, () => confluence.CreatePageAsync(space_key, parent_id, title, body, cancellationToken), cancellationToken);

    [McpServerTool(Name = "confluence_update_page", Idempotent = true)]
    [Description("Replace a page body; version is incremented automatically. Omit title to keep it.")]
    public Task<string> UpdatePage(
        string page_id,
        [Description("Markdown, or storage XHTML if format=storage")] string body,
        string? title = null,
        [Description("markdown or storage")] string format = "markdown",
        string? version_message = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_update_page", Service, () => confluence.UpdatePageAsync(page_id, title, body, format, version_message, cancellationToken), cancellationToken);

    [McpServerTool(Name = "confluence_append_to_page", Destructive = false)]
    [Description("Append Markdown to the end of a page without resending its body. Existing macros are preserved.")]
    public Task<string> AppendToPage(
        string page_id,
        [Description("Markdown")] string body,
        string? version_message = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_append_to_page", Service, () => confluence.AppendToPageAsync(page_id, body, version_message, cancellationToken), cancellationToken);

    [McpServerTool(Name = "confluence_add_drawio", Destructive = false)]
    [Description("Upload a .drawio file to a page and show it with the draw.io macro (draw.io app required). Re-running updates the diagram in place.")]
    public Task<string> AddDrawio(
        string page_id,
        [Description("Local .drawio file")] string file,
        [Description("Diagram name; default the file name")] string? name = null,
        [Description("Optional PNG preview")] string? png = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("confluence_add_drawio", Service, () => confluence.AddDrawioAsync(page_id, file, name, png, null, cancellationToken), cancellationToken);
}
