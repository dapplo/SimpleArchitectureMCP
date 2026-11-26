using System.Text.Json;
using DevToolsMcp.Server.Core.Json;
using DevToolsMcp.Server.Core.Modules;
using DevToolsMcp.Server.Modules.GitHub;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DevToolsMcp.Tests.GitHub;

public class GitHubModuleTests
{
    private static (ServiceProvider Provider, ModuleRegistry Registry) Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithModuleToolGate().AddGitHubModule(config, DevToolsJson.Compose(GitHubJsonContext.Default).McpOptions);
        return (services.BuildServiceProvider(), ModuleRegistry.From(services));
    }

    [Fact]
    public void Registers_six_prefixed_tools()
    {
        var (sp, _) = Build([]);
        var names = sp.GetServices<McpServerTool>().Select(t => t.ProtocolTool.Name).Order().ToArray();
        Assert.Equal(["github_comment", "github_create_issue", "github_get_issue", "github_list_labels", "github_search_issues",
            "github_update_issue"], names);
    }

    [Fact]
    public void Array_parameters_are_string_arrays_in_the_schema()
    {
        var (sp, _) = Build([]);
        var tool = sp.GetServices<McpServerTool>().Single(t => t.ProtocolTool.Name == "github_update_issue").ProtocolTool;
        var labels = tool.InputSchema.GetProperty("properties").GetProperty("add_labels");
        Assert.Contains("array", labels.GetProperty("type").ToString());
        Assert.Equal(["number"], tool.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void Tool_schemas_stay_small()
    {
        var (sp, _) = Build([]);
        var total = sp.GetServices<McpServerTool>().Sum(t => JsonSerializer.Serialize(t.ProtocolTool.InputSchema).Length
            + t.ProtocolTool.Name.Length + (t.ProtocolTool.Description?.Length ?? 0));
        Assert.True(total < 4000, $"GitHub tool schemas are {total} chars");
    }

    [Fact]
    public void Unconfigured_module_is_inactive_and_its_tools_are_gated()
    {
        var (sp, registry) = Build([]);
        Assert.Empty(registry.ActiveModules(sp));
        Assert.False(registry.IsToolActive("github_search_issues", sp));
        Assert.Throws<GitHubNotConfiguredException>(() => sp.GetRequiredService<GitHubConnection>().Api);
    }

    [Fact]
    public void Token_alone_activates_module_for_github_com()
    {
        var (sp, registry) = Build(new() { ["GitHub:ApiToken"] = "ghp_x", ["GitHub:DefaultRepo"] = "dapplo/SimpleArchitectureMCP" });
        Assert.Equal(["github"], registry.ActiveModules(sp));
        var state = sp.GetRequiredService<GitHubConnection>().Current;
        Assert.Equal("api.github.com · default dapplo/SimpleArchitectureMCP", state.Message);
        Assert.Equal("dapplo/SimpleArchitectureMCP", state.Api!.DefaultRepo);
    }
}
