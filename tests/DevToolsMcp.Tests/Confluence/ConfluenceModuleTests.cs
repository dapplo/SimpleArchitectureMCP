using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Core.Settings;
using DevToolsMcp.Server.Modules.Confluence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Tests.Confluence;

public class ConfluenceModuleTests
{
    private static (ServiceProvider Provider, ModuleRegistry Registry, IConfigurationRoot Config) Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithModuleToolGate().AddConfluenceModule(config);
        return (services.BuildServiceProvider(), ModuleRegistry.From(services), config);
    }

    [Fact]
    public void Registers_six_prefixed_tools()
    {
        var (sp, _, _) = Build([]);
        var names = sp.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).Order().ToArray();
        Assert.Equal(["confluence_append_to_page", "confluence_create_page", "confluence_get_page", "confluence_get_page_children",
            "confluence_search", "confluence_update_page"], names);
    }

    [Fact]
    public void Unconfigured_module_is_inactive_and_its_tools_are_gated()
    {
        var (sp, registry, _) = Build([]);
        Assert.Empty(registry.ActiveModules(sp));
        Assert.False(registry.IsToolActive("confluence_search", sp));
        Assert.True(registry.IsToolActive("other_tool", sp));
        Assert.Throws<ConfluenceNotConfiguredException>(() => sp.GetRequiredService<ConfluenceConnection>().Adapter);
    }

    [Fact]
    public void Data_center_configuration_activates_module()
    {
        var (sp, registry, _) = Build(new() { ["Confluence:BaseUrl"] = "https://wiki.corp/confluence", ["Confluence:ApiToken"] = "pat" });
        Assert.Equal(["confluence"], registry.ActiveModules(sp));
        Assert.True(registry.IsToolActive("confluence_search", sp));
        Assert.IsType<DataCenterConfluenceAdapter>(sp.GetRequiredService<ConfluenceConnection>().Adapter);
    }

    [Fact]
    public void Cloud_configuration_binds_enum_and_selects_cloud_adapter()
    {
        var (sp, _, _) = Build(new()
        {
            ["Confluence:BaseUrl"] = "https://wiki.corp",
            ["Confluence:Email"] = "me@corp",
            ["Confluence:ApiToken"] = "tok",
            ["Confluence:DeploymentType"] = "Cloud",
        });
        Assert.IsType<CloudConfluenceAdapter>(sp.GetRequiredService<ConfluenceConnection>().Adapter);
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
            services.AddMcpServer().AddConfluenceModule(config);
            using var sp = services.BuildServiceProvider();
            var registry = ModuleRegistry.From(services);
            Assert.Empty(registry.ActiveModules(sp));

            new SettingsStore(file, config).Save("Confluence", new Dictionary<string, string?>
            {
                ["BaseUrl"] = "https://acme.atlassian.net",
                ["Email"] = "me@acme.com",
                ["ApiToken"] = "tok",
            });

            Assert.Equal(["confluence"], registry.ActiveModules(sp));
            Assert.IsType<CloudConfluenceAdapter>(sp.GetRequiredService<ConfluenceConnection>().Adapter);
            Assert.Contains("\"ApiToken\": \"tok\"", File.ReadAllText(file));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
