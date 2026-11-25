using System.Text.Json;
using DevToolsMcp.Server.Core.Http;
using DevToolsMcp.Server.Core.Modules;

namespace DevToolsMcp.Server.Modules.Jira;

public static class JiraModule
{
    public const string Name = "jira";
    public const string HttpClientName = "jira";
    public const string ToolPrefix = "jira_";

    /// <summary>
    /// Registers the Jira slice. Its tools are only listed while <c>Jira:BaseUrl</c> and <c>Jira:ApiToken</c> are
    /// configured (see <see cref="ModuleToolGate"/>), so an unconfigured module adds zero schema tokens.
    /// Settings can be changed at runtime through the settings page.
    /// </summary>
    public static IMcpServerBuilder AddJiraModule(this IMcpServerBuilder mcp, IConfiguration configuration, JsonSerializerOptions? jsonOptions = null)
    {
        var services = mcp.Services;
        var timeout = int.TryParse(configuration["Jira:TimeoutSeconds"], out var t) ? t : 30;

        services.AddModuleHttpClient(HttpClientName, TimeSpan.FromSeconds(Math.Clamp(timeout, 5, 300)));
        services.AddSingleton(sp => new JiraConnection(configuration, sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton(sp =>
        {
            var connection = sp.GetRequiredService<JiraConnection>();
            return new JiraService(() => connection.Adapter);
        });
        mcp.WithTools<JiraTools>(jsonOptions);

        ModuleRegistry.From(services).Add(new ModuleDescriptor
        {
            Name = Name,
            Title = "Jira",
            Section = JiraOptions.SectionName,
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
                var state = sp.GetRequiredService<JiraConnection>().Current;
                return new ModuleStatus(state.Adapter is not null, state.Message);
            },
            Test = async (sp, ct) =>
            {
                var api = sp.GetRequiredService<JiraConnection>().Adapter;
                var me = await api.GetMyselfAsync(ct).ConfigureAwait(false);
                return $"Connected to Jira {api.DeploymentType} as {me.DisplayName ?? me.Name ?? me.AccountId}.";
            },
        });

        return mcp;
    }

    internal static IJiraApiAdapter CreateAdapter(JiraDeploymentType deployment, IHttpClientFactory factory, JiraOptions options) =>
        deployment == JiraDeploymentType.Cloud
            ? new CloudJiraAdapter(factory, options)
            : new DataCenterJiraAdapter(factory, options);
}
