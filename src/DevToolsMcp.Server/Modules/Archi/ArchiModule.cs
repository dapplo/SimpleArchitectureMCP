using System.Text.Json;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Modules.Archi;

public static class ArchiModule
{
    public const string Name = "archi";
    public const string ToolPrefix = "archi_";

    /// <summary>
    /// Registers the ArchiMate slice: rendering views to PNG with a local Archi install and writing Open Exchange
    /// Format files from compact JSON. Its tools are only listed while <c>Archi:ExecutablePath</c> points at Archi.
    /// </summary>
    public static IMcpServerBuilder AddArchiModule(this IMcpServerBuilder mcp, IConfiguration configuration, JsonSerializerOptions? jsonOptions = null)
    {
        var services = mcp.Services;
        services.AddSingleton<ArchiRunner>();
        services.AddSingleton(sp => new ArchiService(configuration, sp.GetRequiredService<ArchiRunner>()));
        mcp.WithTools<ArchiTools>(jsonOptions);

        ModuleRegistry.From(services).Add(new ModuleDescriptor
        {
            Name = Name,
            Title = "Archi",
            Section = ArchiOptions.SectionName,
            ToolPrefix = ToolPrefix,
            // Three fields so the tab fits without scrolling; Scale, Margin and TimeoutSeconds are appsettings/env only.
            Settings =
            [
                new("ExecutablePath", "Archi executable", "text", Required: true,
                    Help: "Archi.exe (Windows), the Archi launcher (Linux) or Archi.app (macOS). Archi 5 or later."),
                new("Renderer", "Renderer", "select", Options: [ArchiOptions.HtmlReport, ArchiOptions.JArchi],
                    Help: "HtmlReport works with plain Archi (100% scale). jArchi needs the jArchi plugin and honours Archi:Scale."),
                new("OutputDirectory", "Output folder", "text", Help: "For PNG and .xml files when a tool call names no path. Default: temp/devtools-mcp/archi."),
            ],
            Status = sp =>
            {
                var state = sp.GetRequiredService<ArchiService>().Current;
                return new ModuleStatus(state.Executable is not null, state.Message);
            },
            Conversions =
            [
                new("png", "Model → PNG", "file", null, ".xml,.archimate",
                    (sp, input, ct) => sp.GetRequiredService<ArchiService>().DownloadPngAsync(input.FilePath!, ct)),
                new("drawio", "Model → draw.io (.drawio)", "file", null, ".xml,.archimate",
                    (sp, input, ct) => sp.GetRequiredService<ArchiService>().DownloadDrawioAsync(input.FilePath!, ct)),
                new("format", "Open Exchange .xml ⇄ .archimate", "file", null, ".xml,.archimate",
                    (sp, input, ct) => sp.GetRequiredService<ArchiService>().DownloadOtherFormatAsync(input.FilePath!, ct)),
            ],
            Test = (sp, ct) => sp.GetRequiredService<ArchiService>().TestAsync(ct),
        });

        return mcp;
    }
}
