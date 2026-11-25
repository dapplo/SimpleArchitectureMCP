# SimpleMCP: devtools-mcp

A lean, locally running [Model Context Protocol](https://modelcontextprotocol.io) server for developer tools, built to keep LLM token usage low.

* **.NET 11**, `WebApplication.CreateSlimBuilder`, official `ModelContextProtocol.AspNetCore` SDK over **Streamable HTTP** (`/mcp`)
* Published as a **single-file, self-contained Native AOT** executable (~24 MB, about 60–80 ms from launch to healthy)
* **Double-click to run**: a built-in settings page (`http://localhost:5180/`) opens on first start; paste your Confluence URL and token and the tools switch on without a restart
* Optional **Aspire 17** AppHost for development, with OpenTelemetry traces, metrics and logs in the Aspire dashboard
* First integration: **Atlassian Confluence**, both **Cloud** and **Data Center / Server (on-prem)**
* Vertical feature slices, so **Jira, GitHub, …** plug in without touching the core

## Why it is cheap on tokens

| Technique | Effect |
| --- | --- |
| Tools of unconfigured modules are **hidden from `tools/list`** | Unused integrations add zero schema tokens |
| Terse tool descriptions, primitive parameters | All 6 Confluence tool schemas total ≈ 2.5 KB (~630 tokens) |
| Pages are returned as **Markdown**, not storage XHTML | Usually several times smaller than the raw body |
| Search/children return one `id \| title \| …` line per hit | No `_links`, `_expandable` or user metadata |
| `confluence_append_to_page` | Adds content without the model re-sending the whole page |
| Automatic version handling | No failed calls and retries for version conflicts |
| `devtools.mcp.tool.output.size` metric | See exactly how many characters each tool returns |

## Quick start (no SDK needed)

1. Download the archive for your OS from the latest [release](../../releases) and extract it.
2. Double-click `devtools-mcp` (`devtools-mcp.exe` on Windows). A console window shows the server log.
3. On first start the settings page opens in your browser (`http://localhost:5180/`). Enter the Confluence base URL, your email (Cloud only) and the API token, then **Save**. The page tests the connection, and the Confluence tools become available immediately.
4. Point your MCP client at `http://localhost:5180/mcp` (see [VS Code](#use-from-vs-code--github-copilot)). If the client was already connected, reload its MCP server so it fetches the new tool list.

Settings are stored per user in `%APPDATA%\devtools-mcp\settings.json` (Windows) or `~/.config/devtools-mcp/settings.json` (Linux/macOS), readable only by you on Linux/macOS. Environment variables and `appsettings.json` work too; environment variables win over the settings page.

## Repository layout

```
DevToolsMcp.slnx
src/
  DevToolsMcp.Server/            Native AOT MCP server (assembly: devtools-mcp)
    Program.cs                   Composition root: JSON contexts, MCP server, modules
    Core/
      Http/                      Named HttpClient per module: auth handler, payload metrics, resilience
      Json/                      DevToolsJson: combines module JsonSerializerContexts (TypeInfoResolverChain)
      Markdown/                  Shared Markdown parser (AST) used by provider emitters
      Modules/                   ModuleRegistry (enabled/disabled modules, /health)
      Security/                  Loopback-only Kestrel binding, optional API key
      Telemetry/                 OpenTelemetry setup, custom metrics
    Modules/Confluence/          The Confluence vertical slice
      ConfluenceModule.cs        AddConfluenceModule(): conditional registration
      ConfluenceOptions.cs       Options, deployment auto-detection, auth
      IConfluenceApiAdapter.cs   Deployment-specific REST surface
      CloudConfluenceAdapter.cs  REST v2 pages (+ v1 CQL search)
      DataCenterConfluenceAdapter.cs  REST v1 content API, context path respected
      ConfluenceJsonContext.cs   Source-generated DTOs
      ConfluenceMarkupConverter*.cs   Storage XHTML <-> Markdown
      ConfluenceService.cs       Versioning, validation, compact output
      ConfluenceTools.cs         The 6 MCP tools
  DevToolsMcp.AppHost/           Aspire 17 AppHost
tests/DevToolsMcp.Tests/         xUnit v3 tests, grouped by slice
.vscode/mcp.json                 VS Code / GitHub Copilot client config
```

## Confluence tools

| Tool | Inputs | Returns |
| --- | --- | --- |
| `confluence_search` | `query` (CQL or plain text), `space_key?`, `limit` (10, max 25) | `id \| title \| space \| yyyy-MM-dd` + excerpt per hit |
| `confluence_get_page` | `page_id`, `format` (`markdown` \| `storage`) | Front matter (`id`, `title`, `version`) + body |
| `confluence_get_page_children` | `page_id`, `limit` (25) | `id \| title` per child |
| `confluence_create_page` | `space_key`, `title`, `markdown_body`, `parent_id?` | `Created <id> \| <title> \| <url>` |
| `confluence_update_page` | `page_id`, `markdown_body`, `title?`, `format`, `version_message?` | `Updated <id> to version <n>.` |
| `confluence_append_to_page` | `page_id`, `markdown_snippet`, `version_message?` | `Appended to <id>; now version <n>.` |

Plain-text queries become `type = page AND text ~ "..."`; queries that already contain CQL operators pass through unchanged. `space_key` is validated and AND-ed in front, and any `ORDER BY` stays at the end.

Updates read the current version first and write `version + 1`. If someone else saves in between (HTTP 409), the server re-reads the version and retries once. Appends work on the raw storage body, so existing macros are never converted or lost.

### Cloud vs Data Center

`Confluence:DeploymentType` is `Auto` (default), `Cloud` or `DataCenter`. `Auto` picks **Cloud** when the host ends in `.atlassian.net` or `Confluence:Email` is set, otherwise **Data Center**.

| | Cloud | Data Center / Server |
| --- | --- | --- |
| Auth | `Basic email:apiToken` | `Bearer <PAT>` (or `Basic user:password` if `Email` is set with `DeploymentType=DataCenter`) |
| Pages, children, create, update | `/wiki/api/v2/pages…` (space key → `spaceId` resolved and cached) | `/rest/api/content…` (`space.key`, `ancestors`) |
| Search | `/wiki/rest/api/search?cql=…` | `/rest/api/search?cql=…` |
| Base URL | `https://site.atlassian.net` (with or without `/wiki`) | `https://host/context-path` (context path respected) |

Search uses the v1 `/rest/api/search` CQL endpoint rather than `/rest/api/content/search`, because it is the one that returns excerpts and `lastModified` in a single call on both Cloud and Data Center.

### Markdown fidelity

Storage XHTML → Markdown uses `System.Xml.Linq`. Markdown → storage uses the shared parser in `Core/Markdown` plus a `StringBuilder` emitter. Neither uses reflection or a third-party library.

* Headings, paragraphs, bold/italic/strike/inline code, links, images, line breaks, nested ordered/unordered lists, task lists, tables, block quotes and rules all map both ways.
* `code` / `noformat` macros ↔ fenced code blocks. Extra parameters ride on the fence line, e.g. ```` ```java title="Example" ````.
* Confluence links use small URL schemes: `page:Title?space=KEY`, `attachment:file.pdf`, `user:account-id=…`.
* Other macros become placeholders: `**[Confluence Macro: jira | key=ABC-1]**`. Macros with a rich-text body (`info`, `note`, `warning`, `expand`, …) become a block quote that starts with the placeholder. **Simple macros like these round-trip back to real macros.** Macros with nested resource parameters or non-code plain-text bodies do not round-trip. For those, the tool description tells the model to use `format=storage` with both `confluence_get_page` and `confluence_update_page`.

## Configuration

| Key (env var) | Meaning |
| --- | --- |
| `Confluence:BaseUrl` (`Confluence__BaseUrl`) | Required to enable the module |
| `Confluence:ApiToken` (`Confluence__ApiToken`) | Required: Cloud API token or Data Center PAT |
| `Confluence:Email` (`Confluence__Email`) | Cloud account email; leave empty for Data Center PATs |
| `Confluence:DeploymentType` (`Confluence__DeploymentType`) | `Auto` \| `Cloud` \| `DataCenter` |
| `Confluence:TimeoutSeconds` | Total request timeout (default 30) |
| `DevToolsMcp:Port`, else `PORT`, else 5180 | Listen port |
| `DevToolsMcp:OpenBrowser` | `false` stops the settings page from opening on first start |
| `DevToolsMcp:SettingsFile` | Override the per-user settings file path |
| `DevToolsMcp:AllowRemote` | `true` binds all interfaces. Default: loopback only (`127.0.0.1` and `::1`) |
| `McpAuth:ApiKey` (`McpAuth__ApiKey`) | If set, `/mcp` requires `Authorization: Bearer <key>` or `X-API-Key` (`/health` stays open) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Enables OTLP export (Aspire sets it automatically) |

The server binds to loopback only by default, so the unauthenticated endpoint is never exposed on external interfaces. The settings API additionally rejects non-loopback `Host` headers (DNS rebinding) and cross-origin requests, never returns stored tokens, and requires the token to be re-entered when the URL changes, so a malicious web page cannot redirect your credentials.

## Build, test, publish

Requires the .NET 11 SDK (`global.json`). Native AOT also needs the platform toolchain (clang + zlib on Linux, Xcode command-line tools on macOS, the "Desktop development with C++" workload on Windows).

```bash
dotnet build DevToolsMcp.slnx
dotnet test --project tests/DevToolsMcp.Tests     # Microsoft.Testing.Platform runner (see global.json)

# Single-file, self-contained Native AOT executable. Publish into artifacts/publish/<rid> so the AppHost finds it:
dotnet publish src/DevToolsMcp.Server -c Release -r linux-x64 -o artifacts/publish/linux-x64
dotnet publish src/DevToolsMcp.Server -c Release -r win-x64   -o artifacts/publish/win-x64
dotnet publish src/DevToolsMcp.Server -c Release -r osx-arm64 -o artifacts/publish/osx-arm64
```

Native AOT cannot cross-compile between operating systems, so publish each RID on its own OS. Trim/AOT warnings (`IL2026`, `IL3050`, …) are configured as errors, so any reflection regression fails the build.

CI (`.github/workflows/build.yml`) builds, tests, publishes and smoke-tests the AOT binary on `linux-x64`, `linux-arm64`, `win-x64`, `osx-arm64` and `osx-x64`. Each run uploads `devtools-mcp-<rid>.tar.gz` / `.zip` as an artifact. Every push to `main` also publishes them as an automatic GitHub **pre-release** (`v0.1.<run>-preview`). Pushing a `v*` tag publishes a regular release.

Run the binary directly:

```bash
Confluence__BaseUrl=https://acme.atlassian.net Confluence__Email=me@acme.com Confluence__ApiToken=... \
  ./artifacts/publish/linux-x64/devtools-mcp
curl http://localhost:5180/health    # {"status":"ok","modules":["confluence:Cloud"]}
```

## Run with Aspire (optional, for development)

Aspire is not needed to use the server. It is useful while developing modules: the dashboard shows every tool call, downstream request, latency and payload size.

```bash
dotnet tool install -g Aspire.Cli    # Aspire 17 runs AppHosts through the CLI bundle
aspire run --project src/DevToolsMcp.AppHost
```

The AppHost:

* defines the parameters `confluence-url`, `confluence-email` (optional), `confluence-token` (secret) and `confluence-deployment`, and injects them as `Confluence__BaseUrl`, `Confluence__Email`, `Confluence__ApiToken` and `Confluence__DeploymentType`. Fill them in when the dashboard prompts, or with `dotnet user-secrets set Parameters:confluence-token … --project src/DevToolsMcp.AppHost`;
* launches the **published AOT binary** through `AddExecutable` when `artifacts/publish/<current-rid>/devtools-mcp` exists. Otherwise it falls back to the project (`AddDotnetProject`);
* exposes `http://localhost:5180` (Aspire proxy → `PORT`), marks it with `.WithMcpServer(path: "/mcp")`, adds a `/health` check, and wires OTLP so tool calls, downstream Confluence calls, latencies and payload sizes (`devtools.http.client.response.body.size`) show up in the dashboard.

To check that Aspire uses the AOT binary, publish it first and run `aspire run`. The `devtools-mcp` resource then shows `artifacts/publish/<rid>/devtools-mcp` as its command, and `/health` answers through port 5180.

## Use from VS Code / GitHub Copilot

`.vscode/mcp.json`:

```jsonc
{
  "servers": {
    "devtools-mcp": { "type": "http", "url": "http://localhost:5180/mcp" }
  }
}
```

## Adding a provider (Jira, GitHub, …)

Create `Modules/Jira/` with options, DTOs, an adapter and tools, the same way as `Modules/Confluence/` (`ConfluenceConnection` shows how to rebuild the adapter when settings change). Then wire it up in three steps:

```csharp
// Modules/Jira/JiraModule.cs
public static class JiraModule
{
    public static IMcpServerBuilder AddJiraModule(this IMcpServerBuilder mcp, IConfiguration config, JsonSerializerOptions? json = null)
    {
        mcp.Services.AddModuleHttpClient("jira", TimeSpan.FromSeconds(30));
        mcp.Services.AddSingleton(sp => new JiraConnection(config, sp.GetRequiredService<IHttpClientFactory>())); // 1) options, rebuilt on reload
        mcp.WithTools<JiraTools>(json);                                  // 3) tools, named jira_*

        ModuleRegistry.From(mcp.Services).Add(new ModuleDescriptor       // settings page + tool gate
        {
            Name = "jira", Title = "Jira", Section = "Jira", ToolPrefix = "jira_",
            Settings = [new("BaseUrl", "Base URL", "url", Required: true), new("ApiToken", "API token", "secret", Required: true)],
            Status = sp => sp.GetRequiredService<JiraConnection>().Status,  // inactive => tools hidden, zero tokens
        });
        return mcp;
    }
}

[JsonSerializable(typeof(JiraIssueDto))]                                  // 2) the module's own JSON context
internal sealed partial class JiraJsonContext : JsonSerializerContext;
```

```csharp
// Program.cs
var json = DevToolsJson.Compose(ConfluenceJsonContext.Default, JiraJsonContext.Default);   // 2) add to TypeInfoResolverChain
mcp.AddConfluenceModule(builder.Configuration, json.McpOptions);
mcp.AddJiraModule(builder.Configuration, json.McpOptions);                                  // 3)
```

The settings page renders the new module's form automatically. In the AppHost, uncomment `Provider.Create(builder, "jira", "Jira", …)` to get the `jira-url`, `jira-email` and `jira-token` parameters and the `Jira__*` environment variables. Prefix the tool names (`jira_*`, `github_*`) so they never collide.

## License

MIT
