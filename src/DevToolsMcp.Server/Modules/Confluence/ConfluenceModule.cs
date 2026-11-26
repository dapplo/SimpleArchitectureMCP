using System.Text.Json;
using DevToolsMcp.Server.Core.Http;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Modules.Confluence;

public static class ConfluenceModule
{
    public const string Name = "confluence";
    public const string HttpClientName = "confluence";
    public const string ToolPrefix = "confluence_";
    public const string DrawioTool = "confluence_add_drawio";

    /// <summary>
    /// Registers the Confluence slice. Its tools are only listed while <c>Confluence:BaseUrl</c> and
    /// <c>Confluence:ApiToken</c> are configured (see <see cref="ModuleToolGate"/>), so an unconfigured module adds
    /// zero schema tokens. Settings can be changed at runtime through the settings page.
    /// </summary>
    public static IMcpServerBuilder AddConfluenceModule(this IMcpServerBuilder mcp, IConfiguration configuration, JsonSerializerOptions? jsonOptions = null)
    {
        var services = mcp.Services;
        var timeout = int.TryParse(configuration["Confluence:TimeoutSeconds"], out var t) ? t : 30;

        services.AddModuleHttpClient(HttpClientName, TimeSpan.FromSeconds(Math.Clamp(timeout, 5, 300)));
        services.AddSingleton(sp => new ConfluenceConnection(configuration, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<ConfluenceConnection>();
            return new ConfluenceService(() => connection.Adapter);
        });
        mcp.WithTools<ConfluenceTools>(jsonOptions);

        ModuleRegistry.From(services).Add(new ModuleDescriptor
        {
            Name = Name,
            Title = "Confluence",
            Section = ConfluenceOptions.SectionName,
            ToolPrefix = ToolPrefix,
            Settings =
            [
                new("BaseUrl", "Base URL", "url", Required: true,
                    Help: "Cloud: https://your-site.atlassian.net · Data Center: https://host/context-path"),
                new("Email", "Email", "email", Help: "Cloud only. Leave empty for a Data Center personal access token."),
                new("ApiToken", "API token", "secret", Required: true, Help: "Cloud API token or Data Center personal access token"),
                new("DeploymentType", "Deployment", "select", Options: ["Auto", "Cloud", "DataCenter"]),
                new("DiagramApps", "Diagram apps", "text",
                    Help: "auto (detect), none, or a list: drawio, plantuml, mermaid. Tools for missing apps are hidden."),
            ],
            Status = sp =>
            {
                var state = sp.GetRequiredService<ConfluenceConnection>().Current;
                return new ModuleStatus(state.Adapter is not null, state.Message);
            },
            ToolUnavailable = async (sp, tool, ct) =>
            {
                if (tool != DrawioTool || sp.GetRequiredService<ConfluenceConnection>().Current.DiagramApps is not { } diagrams)
                {
                    return null;
                }

                try
                {
                    var apps = await diagrams.GetAsync(ct).ConfigureAwait(false);
                    return apps.Contains(ConfluenceDiagramApps.Drawio)
                        ? null
                        : "The draw.io app is not available on this Confluence (Confluence:DiagramApps).";
                }
                catch (Exception ex) when (ex is ConfluenceApiException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    return null; // detection failed (offline, timeout): keep the tool; a real call reports the problem
                }
            },
            Test = async (sp, ct) =>
            {
                var state = sp.GetRequiredService<ConfluenceConnection>().Current;
                var api = sp.GetRequiredService<ConfluenceConnection>().Adapter;
                await api.SearchAsync("type = page", 1, ct).ConfigureAwait(false);
                IReadOnlySet<string> apps = state.DiagramApps is null ? new HashSet<string>() : await state.DiagramApps.GetAsync(ct).ConfigureAwait(false);
                var source = state.DiagramApps?.Describe() == "auto" ? "detected" : "configured";
                return $"Connected to Confluence {api.DeploymentType}. Diagram apps ({source}): {(apps.Count == 0 ? "none" : string.Join(", ", apps.Order()))}.";
            },
        });

        return mcp;
    }

    internal static IConfluenceApiAdapter CreateAdapter(ConfluenceDeploymentType deployment, IHttpClientFactory factory, ConfluenceOptions options) =>
        deployment == ConfluenceDeploymentType.Cloud
            ? new CloudConfluenceAdapter(factory, options)
            : new DataCenterConfluenceAdapter(factory, options);
}
