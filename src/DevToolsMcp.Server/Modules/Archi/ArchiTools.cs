using System.ComponentModel;
using System.Text;
using DevToolsMcp.Server.Core.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Server.Modules.Archi;

/// <summary>
/// The three Archi tools. Images are written to files and only their paths are returned, unless <c>inline</c> asks for
/// the picture itself: an image costs far more tokens than a path.
/// </summary>
[McpServerToolType]
public sealed class ArchiTools(ArchiService archi)
{
    private const string Service = "Archi";
    private const int MaxInlineImages = 4;

    [McpServerTool(Name = "archi_render", Destructive = false, Idempotent = true)] // writes PNG files, so not read-only
    [Description("Render ArchiMate views of a .archimate or Open Exchange .xml file to PNG with Archi. Lines: view | png path | WxH.")]
    public Task<CallToolResult> Render(
        [Description("Path to .archimate or exchange .xml")] string file,
        [Description("View names or ids; default all")] string[]? views = null,
        [Description("Output folder; default the configured one")] string? out_dir = null,
        [Description("Also return the images (costly)")] bool inline = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("archi_render", Service, async () =>
        {
            var rendered = await archi.RenderAsync(file, views, out_dir, cancellationToken).ConfigureAwait(false);
            return await Result(new StringBuilder(), rendered, inline, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    [McpServerTool(Name = "archi_create_model")]
    [Description("Write an ArchiMate Open Exchange .xml from JSON; layout is automatic unless x/y are given. Optionally renders it to PNG.")]
    public Task<CallToolResult> CreateModel(
        ArchiModelSpec model,
        [Description(".xml path; default the output folder")] string? path = null,
        [Description("Render the views to PNG")] bool render = true,
        [Description("Also return the images (costly)")] bool inline = false,
        [Description("Replace an existing file that is not an exchange model")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("archi_create_model", Service, async () =>
        {
            var (file, summary) = await archi.CreateModelAsync(model, path, overwrite, cancellationToken).ConfigureAwait(false);
            var text = new StringBuilder().Append(file).Append(" (").Append(summary).Append(')').AppendLine();
            if (!render)
            {
                return new CallToolResult { Content = [new TextContentBlock { Text = text.ToString().TrimEnd() }] };
            }

            var rendered = await archi.RenderAsync(file, null, null, cancellationToken).ConfigureAwait(false);
            return await Result(text, rendered, inline, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    [McpServerTool(Name = "archi_to_drawio")]
    [Description("Convert ArchiMate views to a draw.io file (one page per view, ArchiMate 3 shapes), e.g. for Confluence's draw.io app.")]
    public Task<CallToolResult> ToDrawio(
        [Description("Path to exchange .xml or .archimate")] string file,
        [Description("View names or ids; default all")] string[]? views = null,
        [Description(".drawio path; default the output folder")] string? path = null,
        [Description("Replace an existing file that is not a draw.io file")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.RunAsync("archi_to_drawio", Service, async () =>
        {
            var (drawio, pages) = await archi.ToDrawioAsync(file, views, path, overwrite, cancellationToken).ConfigureAwait(false);
            return new CallToolResult { Content = [new TextContentBlock { Text = $"{drawio} (pages: {string.Join(", ", pages)})" }] };
        }, cancellationToken);

    private static async Task<CallToolResult> Result(StringBuilder text, IReadOnlyList<ArchiService.RenderedView> rendered, bool inline,
        CancellationToken cancellationToken)
    {
        foreach (var v in rendered)
        {
            text.Append(v.Name).Append(" | ").Append(v.Path).Append(" | ").Append(v.Width).Append('x').Append(v.Height).AppendLine();
        }

        var content = new List<ContentBlock> { new TextContentBlock { Text = text.ToString().TrimEnd() } };
        if (inline)
        {
            foreach (var v in rendered.Take(MaxInlineImages))
            {
                var bytes = await File.ReadAllBytesAsync(v.Path, cancellationToken).ConfigureAwait(false);
                content.Add(ImageContentBlock.FromBytes(bytes, "image/png"));
            }
        }

        return new CallToolResult { Content = content };
    }
}
