using System.Text.Json;
using SimpleArchitectureMcp.Server.Core.Settings;

namespace SimpleArchitectureMcp.Tests.Core;

public class ClientConfigsTests
{
    private const string WindowsExe = @"C:\Program Files\simple-architecture-mcp\simple-architecture-mcp.exe";

    private static ClientContext Windows(bool apiKey = false, string appData = @"C:\Users\me\AppData\Roaming") =>
        new("simple-architecture-mcp", "http://localhost:5180/mcp", WindowsExe, [], apiKey, ClientOs.Windows, @"C:\Users\me", appData);

    private static ClientContext Mac(bool apiKey = false) =>
        new("simple-architecture-mcp", "http://localhost:5180/mcp", "/Applications/tools/simple-architecture-mcp", [], apiKey, ClientOs.MacOs,
            "/Users/me", "/Users/me/Library/Application Support");

    private static ClientConfigView Client(ClientsView view, string key) => view.Clients.Single(c => c.Key == key);

    private static JsonElement Entry(ClientConfigView c, string root = "mcpServers") =>
        JsonDocument.Parse(c.Content).RootElement.GetProperty(root).GetProperty("simple-architecture-mcp");

    [Fact]
    public void Every_json_config_is_valid_json_with_the_server_entry()
    {
        foreach (var transport in new[] { ClientConfigs.Http, ClientConfigs.Stdio })
        {
            foreach (var c in ClientConfigs.Build(Windows(), transport, "jira,github").Clients.Where(c => c.Key != "codex"))
            {
                Entry(c, c.MergeInto);
            }
        }
    }

    [Fact]
    public void Http_configs_use_the_scoped_endpoint()
    {
        var view = ClientConfigs.Build(Mac(), ClientConfigs.Http, "jira,readonly");
        Assert.Equal("http://localhost:5180/mcp/jira,readonly", Entry(Client(view, "vscode"), "servers").GetProperty("url").GetString());
        Assert.Equal("http://localhost:5180/mcp/jira,readonly", Entry(Client(view, "windsurf")).GetProperty("serverUrl").GetString());
        Assert.Equal("http", Entry(Client(view, "claude-code")).GetProperty("type").GetString());   // required by Claude Code
        Assert.Contains("url = \"http://localhost:5180/mcp/jira,readonly\"", Client(view, "codex").Content);
    }

    [Fact]
    public void Stdio_configs_start_this_binary_with_the_tool_scope()
    {
        var desktop = Client(ClientConfigs.Build(Windows(), ClientConfigs.Stdio, "jira"), "claude-desktop");
        var entry = Entry(desktop);
        Assert.Equal(WindowsExe, entry.GetProperty("command").GetString());
        Assert.Equal(["--stdio", "--tools", "jira"], entry.GetProperty("args").EnumerateArray().Select(a => a.GetString()!));
        Assert.Contains("\"args\": [\"--stdio\", \"--tools\", \"jira\"]", desktop.Content);   // short arrays on one line
    }

    [Fact]
    public void Claude_desktop_is_stdio_only_and_lives_in_app_data()
    {
        var desktop = Client(ClientConfigs.Build(Windows(), ClientConfigs.Http, null), "claude-desktop");
        Assert.Equal(ClientConfigs.Stdio, desktop.Transport);
        Assert.Equal(@"C:\Users\me\AppData\Roaming\Claude\claude_desktop_config.json", Assert.Single(desktop.Locations).Path);

        var mac = Client(ClientConfigs.Build(Mac(), ClientConfigs.Http, null), "claude-desktop");
        Assert.Equal("/Users/me/Library/Application Support/Claude/claude_desktop_config.json", Assert.Single(mac.Locations).Path);
    }

    [Fact]
    public void An_existing_config_file_is_flagged_so_the_user_merges_instead_of_replacing()
    {
        var appData = Directory.CreateTempSubdirectory("client-configs").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(appData, "Claude"));
            File.WriteAllText(Path.Combine(appData, "Claude", "claude_desktop_config.json"), "{}");
            var ctx = Windows(appData: appData) with { Os = OperatingSystem.IsWindows() ? ClientOs.Windows : ClientOs.MacOs };
            var desktop = Client(ClientConfigs.Build(ctx, ClientConfigs.Stdio, null), "claude-desktop");
            Assert.True(Assert.Single(desktop.Locations).Exists);
            Assert.Equal("mcpServers", desktop.MergeInto);
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public void Api_key_is_referenced_never_embedded()
    {
        var view = ClientConfigs.Build(Mac(apiKey: true), ClientConfigs.Http, null);
        Assert.Equal("SIMPLE_ARCHITECTURE_MCP_KEY", view.KeyVariable);
        Assert.Equal("Bearer ${input:simple-architecture-mcp-key}", Entry(Client(view, "vscode"), "servers").GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Equal("Bearer ${env:SIMPLE_ARCHITECTURE_MCP_KEY}", Entry(Client(view, "cursor")).GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.Contains("bearer_token_env_var = \"SIMPLE_ARCHITECTURE_MCP_KEY\"", Client(view, "codex").Content);

        // JetBrains can't send headers, so it gets stdio, which needs no key.
        var jetbrains = Client(view, "jetbrains");
        Assert.Equal([ClientConfigs.Stdio], jetbrains.Transports);
        Assert.Equal(ClientConfigs.Stdio, jetbrains.Transport);
    }

    [Fact]
    public void Codex_toml_escapes_windows_paths()
    {
        var codex = Client(ClientConfigs.Build(Windows(), ClientConfigs.Stdio, null), "codex");
        Assert.Contains("command = \"C:\\\\Program Files\\\\simple-architecture-mcp\\\\simple-architecture-mcp.exe\"", codex.Content);
        Assert.Contains("args = [\"--stdio\"]", codex.Content);
        Assert.Equal("codex mcp add simple-architecture-mcp -- \"C:\\Program Files\\simple-architecture-mcp\\simple-architecture-mcp.exe\" --stdio", codex.Command);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("jira,github", true)]
    [InlineData("confluence,-confluence_export_page", true)]
    [InlineData("jira+readonly", true)]
    [InlineData("jira\"; rm -rf /", false)]
    [InlineData("jira github", false)]
    [InlineData("../../x", false)]
    public void Tool_scope_is_restricted_to_names(string? tools, bool valid) =>
        Assert.Equal(valid, ClientConfigs.IsValidTools(tools));
}
