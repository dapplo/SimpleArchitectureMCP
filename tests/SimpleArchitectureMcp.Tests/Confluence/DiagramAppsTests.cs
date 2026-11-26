using SimpleArchitectureMcp.Server.Core.Json;
using SimpleArchitectureMcp.Server.Core.Modules;
using SimpleArchitectureMcp.Server.Modules.Confluence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace SimpleArchitectureMcp.Tests.Confluence;

public class DiagramAppsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, null)]
    [InlineData("Auto", null)]
    [InlineData("none", "")]
    [InlineData("draw.io, PlantUML;mermaid bogus", "drawio,mermaid,plantuml")]
    public void Parses_the_setting(string? setting, string? expected)
    {
        var parsed = ConfluenceDiagramApps.Parse(setting);
        Assert.Equal(expected, parsed is null ? null : string.Join(",", parsed.Order()));
    }

    [Fact]
    public async Task Detects_apps_by_comparing_with_an_unknown_macro()
    {
        // Data Center style: unknown macros render an error naming the macro; draw.io and the Cloud PlantUML macro exist.
        var fake = new ConfluenceServiceTests.FakeAdapter
        {
            Render = storage => storage.Contains("\"drawio\"", StringComparison.Ordinal) ? "<div class=\"drawio-macro\"></div>"
                : storage.Contains("\"plantumlcloud\"", StringComparison.Ordinal) ? "<iframe data-app=\"plantuml\"></iframe>"
                : "<div class=\"error\">" + storage.Split('"')[1] + "</div>",
        };

        var apps = await new ConfluenceDiagramApps("auto", () => fake).GetAsync(Ct);

        Assert.Equal(["drawio", "plantuml"], apps.Order().ToArray());
    }

    [Fact]
    public async Task Unknown_macro_text_means_missing()
    {
        var fake = new ConfluenceServiceTests.FakeAdapter { Render = s => s.Contains("drawio", StringComparison.Ordinal) ? "<p>Unknown macro: 'drawio'</p>" : "<p>x" + s.Length + "</p>" };
        var apps = await new ConfluenceDiagramApps(null, () => fake).GetAsync(Ct);
        Assert.DoesNotContain("drawio", apps);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("plantuml", false)]
    [InlineData("drawio", true)]
    public async Task Drawio_tool_is_hidden_without_the_app(string setting, bool visible)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Confluence:BaseUrl"] = "https://wiki.corp/confluence",
            ["Confluence:ApiToken"] = "pat",
            ["Confluence:DiagramApps"] = setting,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMcpServer().WithModuleToolGate().AddConfluenceModule(config, DevToolsJson.Compose(ConfluenceJsonContext.Default).McpOptions);
        var sp = services.BuildServiceProvider();
        var registry = ModuleRegistry.From(services);

        var reason = await registry.ToolUnavailableAsync(ConfluenceModule.DrawioTool, sp, Ct);
        Assert.Equal(visible, reason is null);
        Assert.Null(await registry.ToolUnavailableAsync("confluence_search", sp, Ct));
        Assert.Contains($"diagram apps: {setting}", sp.GetRequiredService<ConfluenceConnection>().Current.Message);
        Assert.Contains(sp.GetServices<McpServerTool>(), t => t.ProtocolTool.Name == ConfluenceModule.DrawioTool);
    }
}
