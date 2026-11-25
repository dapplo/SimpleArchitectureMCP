using System.Text.Json;
using DevToolsMcp.Server.Core.Http;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Modules.Confluence;

public static class ConfluenceModule
{
    public const string Name = "confluence";
    public const string HttpClientName = "confluence";
    public const string ToolPrefix = "confluence_";

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
            ],
            Status = sp =>
            {
                var state = sp.GetRequiredService<ConfluenceConnection>().Current;
                return new ModuleStatus(state.Adapter is not null, state.Message);
            },
            Test = async (sp, ct) =>
            {
                var api = sp.GetRequiredService<ConfluenceConnection>().Adapter;
                await api.SearchAsync("type = page", 1, ct).ConfigureAwait(false);
                return $"Connected to Confluence {api.DeploymentType}.";
            },
        });

        return mcp;
    }

    internal static IConfluenceApiAdapter CreateAdapter(ConfluenceDeploymentType deployment, IHttpClientFactory factory, ConfluenceOptions options) =>
        deployment == ConfluenceDeploymentType.Cloud
            ? new CloudConfluenceAdapter(factory, options)
            : new DataCenterConfluenceAdapter(factory, options);
}
