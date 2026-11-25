using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Modules.Confluence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Tests.Confluence;

public class ConfluenceModuleTests
{
    private static (ServiceProvider Provider, ModuleRegistry Registry) Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().AddConfluenceModule(config);
        return (services.BuildServiceProvider(), ModuleRegistry.From(services));
    }

    [Fact]
    public void Unconfigured_module_registers_no_tools()
    {
        var (sp, registry) = Build([]);
        Assert.Empty(sp.GetServices<McpServerTool>());
        Assert.Null(sp.GetService<IConfluenceApiAdapter>());
        Assert.Empty(registry.Enabled);
        Assert.Single(registry.Disabled);
    }

    [Fact]
    public void Data_center_configuration_registers_six_prefixed_tools()
    {
        var (sp, registry) = Build(new() { ["Confluence:BaseUrl"] = "https://wiki.corp/confluence", ["Confluence:ApiToken"] = "pat" });

        var names = sp.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).Order().ToArray();
        Assert.Equal(["confluence_append_to_page", "confluence_create_page", "confluence_get_page", "confluence_get_page_children",
            "confluence_search", "confluence_update_page"], names);
        Assert.IsType<DataCenterConfluenceAdapter>(sp.GetRequiredService<IConfluenceApiAdapter>());
        Assert.Equal(["confluence:DataCenter"], registry.Enabled);
    }

    [Fact]
    public void Cloud_configuration_binds_enum_and_selects_cloud_adapter()
    {
        var (sp, _) = Build(new()
        {
            ["Confluence:BaseUrl"] = "https://wiki.corp",
            ["Confluence:Email"] = "me@corp",
            ["Confluence:ApiToken"] = "tok",
            ["Confluence:DeploymentType"] = "Cloud",
        });
        Assert.IsType<CloudConfluenceAdapter>(sp.GetRequiredService<IConfluenceApiAdapter>());
        Assert.Equal(ConfluenceDeploymentType.Cloud, sp.GetRequiredService<ConfluenceOptions>().DeploymentType);
    }
}
