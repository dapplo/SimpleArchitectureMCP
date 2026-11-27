using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SimpleArchitectureMcp.Server.Core.Settings;

/// <summary>Where a downloaded config goes. <paramref name="Exists"/>: merge the entry instead of replacing the file.</summary>
public sealed record ClientLocation(string Label, string Path, bool Exists);

/// <summary>One client's ready-to-save config for the running instance.</summary>
public sealed record ClientConfigView(string Key, string Title, IReadOnlyList<string> Transports, string Transport,
    string FileName, string Content, string MergeInto, IReadOnlyList<ClientLocation> Locations, string Steps, string? Command);

public sealed record ClientsView(string ServerName, string Os, bool ApiKey, string? KeyVariable, IReadOnlyList<ClientConfigView> Clients);

/// <summary>Operating system of the machine the server (and therefore the settings page's browser) runs on.</summary>
public enum ClientOs { Windows, MacOs, Linux }

/// <summary>Everything the generated configs depend on; <see cref="ForThisProcess"/> fills it from the running instance.</summary>
public sealed record ClientContext(string ServerName, string McpUrl, string Command, IReadOnlyList<string> CommandArgs,
    bool ApiKey, ClientOs Os, string Home, string AppData)
{
    public static ClientContext ForThisProcess(string serverName, string mcpUrl, bool apiKey)
    {
        var os = OperatingSystem.IsWindows() ? ClientOs.Windows : OperatingSystem.IsMacOS() ? ClientOs.MacOs : ClientOs.Linux;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = os switch
        {
            ClientOs.Windows => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ClientOs.MacOs => System.IO.Path.Combine(home, "Library", "Application Support"),
            _ => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : System.IO.Path.Combine(home, ".config"),
        };

        // Native AOT / apphost: the process is the server. `dotnet simple-architecture-mcp.dll` (dev builds): pass the dll along.
        var command = Environment.ProcessPath ?? serverName;
        IReadOnlyList<string> args = [];
        if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(command), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            args = [System.IO.Path.Combine(AppContext.BaseDirectory, typeof(ClientContext).Assembly.GetName().Name + ".dll")];
        }

        return new ClientContext(serverName, mcpUrl, command, args, apiKey, os, home, appData);
    }
}

/// <summary>
/// Generates the per-client MCP configs that docs/clients.md describes, with this instance's real URL, binary path and
/// tool scope, so the settings page can offer them as downloads. API keys are never written into a file: HTTP configs
/// reference an environment variable (or VS Code's secure prompt).
/// </summary>
internal static partial class ClientConfigs
{
    public const string Http = "http";
    public const string Stdio = "stdio";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Module and tool names, <c>-name</c>, <c>readonly</c>; separated by commas or <c>+</c> (see ToolScope).</summary>
    [GeneratedRegex("^[A-Za-z0-9_,+-]{0,200}$")]
    private static partial Regex ToolsSpec();

    public static bool IsValidTools(string? tools) => tools is null || ToolsSpec().IsMatch(tools);

    /// <summary>The environment variable HTTP configs read the API key from, e.g. <c>SIMPLE_ARCHITECTURE_MCP_KEY</c>.</summary>
    public static string KeyVariable(string serverName) => serverName.ToUpperInvariant().Replace('-', '_') + "_KEY";

    public static ClientsView Build(ClientContext ctx, string transport, string? tools)
    {
        tools = string.IsNullOrWhiteSpace(tools) ? null : tools.Trim().Trim(',');
        var url = tools is null ? ctx.McpUrl : ctx.McpUrl.TrimEnd('/') + "/" + tools;
        List<string> args = [.. ctx.CommandArgs, "--stdio"];
        if (tools is not null)
        {
            args.AddRange(["--tools", tools]);
        }

        var s = new Spec(ctx, url, args, transport == Stdio ? Stdio : Http);
        ClientConfigView[] clients =
        [
            VsCode(s), CopilotCli(s), ClaudeCode(s), ClaudeDesktop(s), Cursor(s), Windsurf(s), JetBrains(s), Codex(s),
        ];
        var os = ctx.Os switch { ClientOs.Windows => "windows", ClientOs.MacOs => "macos", _ => "linux" };
        return new ClientsView(ctx.ServerName, os, ctx.ApiKey, ctx.ApiKey ? KeyVariable(ctx.ServerName) : null, clients);
    }

    private sealed record Spec(ClientContext Ctx, string Url, List<string> Args, string Wanted)
    {
        public string Name => Ctx.ServerName;
        public string Key => KeyVariable(Ctx.ServerName);

        /// <summary>The requested transport if the client supports it, otherwise its first one.</summary>
        public string Pick(string[] supported) => supported.Contains(Wanted) ? Wanted : supported[0];

        public string Home(params string[] parts) => Join(Ctx.Home, parts);
        public string AppData(params string[] parts) => Join(Ctx.AppData, parts);

        private string Join(string root, string[] parts)
        {
            var sep = Ctx.Os == ClientOs.Windows ? '\\' : '/';
            var path = new StringBuilder(root.TrimEnd('/', '\\'));
            foreach (var p in parts)
            {
                path.Append(sep).Append(p);
            }

            return path.ToString();
        }

        public ClientLocation At(string label, string path) => new(label, path, File.Exists(path));

        /// <summary>A path relative to a project (folder unknown to the server).</summary>
        public ClientLocation InProject(string relative) =>
            new("This project only", Ctx.Os == ClientOs.Windows ? relative.Replace('/', '\\') : relative, false);

        public JsonArray ArgsJson() => [.. Args.Select(a => (JsonNode)a)];

        public JsonObject StdioEntry(string? type = null)
        {
            var entry = new JsonObject();
            if (type is not null)
            {
                entry["type"] = type;
            }

            entry["command"] = Ctx.Command;
            entry["args"] = ArgsJson();
            return entry;
        }

        public JsonObject Headers(string valueReference) => new() { ["Authorization"] = "Bearer " + valueReference };

        /// <summary>The command line of the stdio server, quoted for a shell where needed.</summary>
        public string CommandLine() => string.Join(' ', new[] { Ctx.Command }.Concat(Args).Select(Quote));

        private static string Quote(string v) => v.Length > 0 && v.All(c => char.IsLetterOrDigit(c) || "/\\:._,+-=".Contains(c)) ? v : "\"" + v + "\"";

        public string KeyNote => Ctx.ApiKey ? $" Set the environment variable {Key} to the API key (McpAuth:ApiKey)." : "";
    }

    private static string Json(string rootKey, string name, JsonObject entry, JsonObject? extra = null)
    {
        var root = new JsonObject { [rootKey] = new JsonObject { [name] = entry } };
        if (extra is not null)
        {
            foreach (var (k, v) in extra.ToList())
            {
                extra.Remove(k);
                root[k] = v;
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            root.WriteTo(writer);
        }

        // Short string arrays ("args", "tools") read better on one line.
        return StringArray().Replace(Encoding.UTF8.GetString(buffer.ToArray()),
            m => "[" + string.Join(", ", m.Groups[1].Captures.Select(c => c.Value)) + "]") + "\n";
    }

    [GeneratedRegex("""\[\s*(?:("(?:[^"\\]|\\.)*")\s*,?\s*)+\]""")]
    private static partial Regex StringArray();

    private static ClientConfigView VsCode(Spec s)
    {
        string[] supported = [Http, Stdio];
        var t = s.Pick(supported);
        string content;
        if (t == Stdio)
        {
            content = Json("servers", s.Name, s.StdioEntry("stdio"));
        }
        else
        {
            var entry = new JsonObject { ["type"] = "http", ["url"] = s.Url };
            JsonObject? extra = null;
            if (s.Ctx.ApiKey)
            {
                // VS Code asks once and keeps the key in its secret storage.
                entry["headers"] = s.Headers("${input:" + s.Name + "-key}");
                extra = new JsonObject
                {
                    ["inputs"] = new JsonArray(new JsonObject
                    {
                        ["id"] = s.Name + "-key", ["type"] = "promptString", ["description"] = s.Name + " API key", ["password"] = true,
                    }),
                };
            }

            content = Json("servers", s.Name, entry, extra);
        }

        var user = s.AppData("Code", "User", "mcp.json");
        return new ClientConfigView("vscode", "VS Code / GitHub Copilot", supported, t, "mcp.json", content, "servers",
            [s.At("All workspaces (user)", user), s.InProject(".vscode/mcp.json")],
            "Or run \"MCP: Open User Configuration\" in VS Code and paste it there. Restart the server from \"MCP: List Servers\" after changing settings here; pick the tools in Copilot Chat's agent mode."
                + (s.Ctx.ApiKey && t == Http ? " VS Code asks for the API key once and stores it securely." : ""),
            null);
    }

    private static ClientConfigView CopilotCli(Spec s)
    {
        string[] supported = [Http, Stdio];
        var t = s.Pick(supported);
        var entry = t == Stdio ? s.StdioEntry("local") : new JsonObject { ["type"] = "http", ["url"] = s.Url };
        if (t == Http && s.Ctx.ApiKey)
        {
            entry["headers"] = s.Headers("${" + s.Key + "}");
        }

        entry["tools"] = new JsonArray("*");
        return new ClientConfigView("copilot-cli", "GitHub Copilot CLI", supported, t, "mcp-config.json",
            Json("mcpServers", s.Name, entry), "mcpServers",
            [s.At("All folders (user)", s.Home(".copilot", "mcp-config.json"))],
            "If you set COPILOT_HOME, the file goes in that folder instead." + (t == Http ? s.KeyNote : ""), null);
    }

    private static ClientConfigView ClaudeCode(Spec s)
    {
        string[] supported = [Http, Stdio];
        var t = s.Pick(supported);
        JsonObject entry;
        string command;
        if (t == Stdio)
        {
            entry = s.StdioEntry("stdio");
            command = $"claude mcp add --scope user {s.Name} -- {s.CommandLine()}";
        }
        else
        {
            // "type": "http" is required; without it Claude Code treats the entry as stdio and skips it.
            entry = new JsonObject { ["type"] = "http", ["url"] = s.Url };
            command = $"claude mcp add --transport http --scope user {s.Name} {s.Url}";
            if (s.Ctx.ApiKey)
            {
                entry["headers"] = s.Headers("${" + s.Key + "}");
                command += " --header \"Authorization: Bearer <your key>\"";
            }
        }

        return new ClientConfigView("claude-code", "Claude Code", supported, t, ".mcp.json", Json("mcpServers", s.Name, entry), "mcpServers",
            [s.InProject(".mcp.json")],
            "The command adds the server for all your projects (stored in ~/.claude.json); the file adds it to one project and can be committed. Use /mcp inside Claude Code to reconnect after changing settings here."
                + (t == Http ? s.KeyNote : ""),
            command);
    }

    private static ClientConfigView ClaudeDesktop(Spec s)
    {
        // Custom connectors need a public HTTPS URL, so local servers are stdio only.
        string[] supported = [Stdio];
        List<ClientLocation> where = s.Ctx.Os switch
        {
            ClientOs.Windows => [s.At("Windows", s.AppData("Claude", "claude_desktop_config.json"))],
            ClientOs.MacOs => [s.At("macOS", s.AppData("Claude", "claude_desktop_config.json"))],
            _ => [],
        };
        return new ClientConfigView("claude-desktop", "Claude Desktop", supported, Stdio, "claude_desktop_config.json",
            Json("mcpServers", s.Name, s.StdioEntry()), "mcpServers", where,
            (s.Ctx.Os == ClientOs.Linux ? "Claude Desktop is available for Windows and macOS. " : "")
                + "Settings → Developer → Edit Config opens the same file. Claude Desktop starts the server itself (stdio only: its custom connectors can't reach localhost). Restart Claude Desktop after saving.",
            null);
    }

    private static ClientConfigView Cursor(Spec s)
    {
        string[] supported = [Http, Stdio];
        var t = s.Pick(supported);
        var entry = t == Stdio ? s.StdioEntry() : new JsonObject { ["url"] = s.Url };
        if (t == Http && s.Ctx.ApiKey)
        {
            entry["headers"] = s.Headers("${env:" + s.Key + "}");
        }

        return new ClientConfigView("cursor", "Cursor", supported, t, "mcp.json", Json("mcpServers", s.Name, entry), "mcpServers",
            [s.At("All projects (user)", s.Home(".cursor", "mcp.json")), s.InProject(".cursor/mcp.json")],
            "Toggle or refresh the server under Cursor Settings → MCP." + (t == Http ? s.KeyNote : ""), null);
    }

    private static ClientConfigView Windsurf(Spec s)
    {
        string[] supported = [Http, Stdio];
        var t = s.Pick(supported);
        var entry = t == Stdio ? s.StdioEntry() : new JsonObject { ["serverUrl"] = s.Url };
        if (t == Http && s.Ctx.ApiKey)
        {
            entry["headers"] = s.Headers("${env:" + s.Key + "}");
        }

        return new ClientConfigView("windsurf", "Windsurf", supported, t, "mcp_config.json", Json("mcpServers", s.Name, entry), "mcpServers",
            [s.At("User", s.Home(".codeium", "windsurf", "mcp_config.json"))],
            "Cascade → MCP → Open MCP config file opens the same file. Press refresh in the MCP panel after saving." + (t == Http ? s.KeyNote : ""), null);
    }

    private static ClientConfigView JetBrains(Spec s)
    {
        // JetBrains documents no request headers for HTTP servers, so with an API key only stdio works.
        string[] supported = s.Ctx.ApiKey ? [Stdio] : [Http, Stdio];
        var t = s.Pick(supported);
        var entry = t == Stdio ? s.StdioEntry() : new JsonObject { ["url"] = s.Url };
        return new ClientConfigView("jetbrains", "JetBrains AI Assistant / Junie", supported, t, s.Name + "-jetbrains.json",
            Json("mcpServers", s.Name, entry), "mcpServers", [],
            "There is no file to place: open Settings → Tools → AI Assistant → Model Context Protocol (MCP) → Add → " + (t == Stdio ? "stdio" : "HTTP")
                + ", paste this JSON, choose global or project level, then OK and Apply."
                + (s.Ctx.ApiKey ? " HTTP is not offered because JetBrains can't send the API key; stdio needs no key." : ""),
            null);
    }

    private static ClientConfigView Codex(Spec s)
    {
        string[] supported = [Http, Stdio];
        var t = s.Pick(supported);
        var toml = new StringBuilder().Append("[mcp_servers.").Append(s.Name).Append("]\n");
        string? command;
        if (t == Stdio)
        {
            toml.Append("command = ").Append(TomlString(s.Ctx.Command)).Append('\n')
                .Append("args = [").Append(string.Join(", ", s.Args.Select(TomlString))).Append("]\n");
            command = $"codex mcp add {s.Name} -- {s.CommandLine()}";
        }
        else
        {
            toml.Append("url = ").Append(TomlString(s.Url)).Append('\n');
            // With a key, only the file form is shown: it names the variable Codex reads the bearer token from.
            command = s.Ctx.ApiKey ? null : $"codex mcp add {s.Name} --url {s.Url}";
            if (s.Ctx.ApiKey)
            {
                toml.Append("bearer_token_env_var = ").Append(TomlString(s.Key)).Append('\n');
            }
        }

        return new ClientConfigView("codex", "OpenAI Codex CLI", supported, t, "config.toml", toml.ToString(), $"[mcp_servers.{s.Name}]",
            [s.At("User", s.Home(".codex", "config.toml")), s.InProject(".codex/config.toml")],
            "config.toml holds all of Codex's settings, so add these lines to it rather than replacing it, or run the command. /mcp inside Codex lists the connected servers."
                + (t == Http ? s.KeyNote : ""),
            command);
    }

    private static string TomlString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            sb.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _ when char.IsControl(c) => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }

        return sb.Append('"').ToString();
    }
}
