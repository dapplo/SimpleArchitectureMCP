using System.Text.Json;
using DevToolsMcp.Server.Core.Json;
using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Core.Settings;
using DevToolsMcp.Server.Modules.Jira;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Tests.Jira;

public class JiraModuleTests
{
    private static (ServiceProvider Provider, ModuleRegistry Registry) Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithModuleToolGate().AddJiraModule(config, DevToolsJson.Compose(JiraJsonContext.Default).McpOptions);
        return (services.BuildServiceProvider(), ModuleRegistry.From(services));
    }

    [Fact]
    public void Registers_seven_prefixed_tools()
    {
        var (sp, _) = Build([]);
        var names = sp.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).Order().ToArray();
        Assert.Equal(["jira_add_comment", "jira_attach_file", "jira_create_issue", "jira_get_issue", "jira_search",
            "jira_transition_issue", "jira_update_issue"], names);
    }

    [Fact]
    public void Tool_schemas_stay_small()
    {
        var (sp, _) = Build([]);
        var tools = sp.GetServices<McpServerTool>().Select(t => t.ProtocolTool).ToList();
        var bytes = tools.Sum(t => t.Name.Length + (t.Description?.Length ?? 0) + JsonSerializer.Serialize(t.InputSchema).Length);
        Assert.True(bytes < 4500, $"Jira tool schemas grew to {bytes} bytes");

        var labels = tools.Single(t => t.Name == "jira_update_issue").InputSchema.GetProperty("properties").GetProperty("add_labels");
        Assert.Contains("array", labels.GetProperty("type").ToString());
    }

    [Fact]
    public void Unconfigured_module_is_inactive_and_its_tools_are_gated()
    {
        var (sp, registry) = Build([]);
        Assert.Empty(registry.ActiveModules(sp));
        Assert.False(registry.IsToolActive("jira_search", sp));
        Assert.Throws<JiraNotConfiguredException>(() => sp.GetRequiredService<JiraConnection>().Adapter);
    }

    [Fact]
    public void Data_center_configuration_activates_module()
    {
        var (sp, registry) = Build(new() { ["Jira:BaseUrl"] = "https://jira.corp/jira", ["Jira:ApiToken"] = "pat" });
        Assert.Equal(["jira"], registry.ActiveModules(sp));
        Assert.True(registry.IsToolActive("jira_get_issue", sp));
        Assert.IsType<DataCenterJiraAdapter>(sp.GetRequiredService<JiraConnection>().Adapter);
    }

    [Fact]
    public void Cloud_without_email_stays_inactive()
    {
        var (sp, registry) = Build(new() { ["Jira:BaseUrl"] = "https://acme.atlassian.net", ["Jira:ApiToken"] = "tok" });
        Assert.Empty(registry.ActiveModules(sp));
        Assert.Contains("Email", sp.GetRequiredService<JiraConnection>().Current.Message);
    }

    [Fact]
    public void Saving_settings_activates_module_without_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"devtools-mcp-{Guid.NewGuid():N}.json");
        try
        {
            var config = new ConfigurationBuilder().AddJsonFile(file, optional: true, reloadOnChange: false).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMcpServer().AddJiraModule(config);
            using var sp = services.BuildServiceProvider();
            var registry = ModuleRegistry.From(services);
            Assert.Empty(registry.ActiveModules(sp));

            new SettingsStore(file, config).Save("Jira", new Dictionary<string, string?>
            {
                ["BaseUrl"] = "https://acme.atlassian.net",
                ["Email"] = "me@acme.com",
                ["ApiToken"] = "tok",
            });

            Assert.Equal(["jira"], registry.ActiveModules(sp));
            Assert.IsType<CloudJiraAdapter>(sp.GetRequiredService<JiraConnection>().Adapter);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("https://acme.atlassian.net/jira/software/projects/X", null, JiraDeploymentType.Cloud, "https://acme.atlassian.net/")]
    [InlineData("https://jira.corp/jira/", null, JiraDeploymentType.DataCenter, "https://jira.corp/jira/")]
    [InlineData("https://jira.corp", "me@corp", JiraDeploymentType.Cloud, "https://jira.corp/")]
    public void Options_resolve_deployment_and_base_address(string url, string? email, JiraDeploymentType expected, string baseAddress)
    {
        var options = new JiraOptions { BaseUrl = url, Email = email, ApiToken = "t" };
        Assert.Equal(expected, options.ResolveDeploymentType());
        Assert.Equal(baseAddress, options.GetApiBaseAddress(expected).ToString());
    }

    [Fact]
    public void Data_center_uses_bearer_pat_and_cloud_uses_basic()
    {
        Assert.Equal("Bearer", new JiraOptions { BaseUrl = "https://j", ApiToken = "t" }.CreateAuthorization(JiraDeploymentType.DataCenter).Scheme);
        Assert.Equal("Basic", new JiraOptions { BaseUrl = "https://j", Email = "e", ApiToken = "t" }.CreateAuthorization(JiraDeploymentType.Cloud).Scheme);
    }
}
