# SimpleMCP: devtools-mcp

A lean, locally running [Model Context Protocol](https://modelcontextprotocol.io) server for developer tools, built to keep LLM token usage low.

* **.NET 11**, `WebApplication.CreateSlimBuilder`, official `ModelContextProtocol.AspNetCore` SDK over **Streamable HTTP** (`/mcp`)
* Published as a **single-file, self-contained Native AOT** executable (~24 MB, about 60–80 ms from launch to healthy)
* Orchestrated by an **Aspire 17** AppHost, with OpenTelemetry traces, metrics and logs in the Aspire dashboard
* First integration: **Atlassian Confluence**, both **Cloud** and **Data Center / Server (on-prem)**
* Vertical feature slices, so **Jira, GitHub, …** plug in without touching the core

## Why it is cheap on tokens

| Technique | Effect |
| --- | --- |
| Modules without configuration register **no tools** | Unused integrations add zero schema tokens |
| Terse tool descriptions, primitive parameters | All 6 Confluence tool schemas total ≈ 2.5 KB (~630 tokens) |
| Pages are returned as **Markdown**, not storage XHTML | Usually several times smaller than the raw body |
| Search/children return one `id \| title \| …` line per hit | No `_links`, `_expandable` or user metadata |
| `confluence_append_to_page` | Adds content without the model re-sending the whole page |
| Automatic version handling | No failed calls and retries for version conflicts |
| `devtools.mcp.tool.output.size` metric | See exactly how many characters each tool returns |

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
| `DevToolsMcp:AllowRemote` | `true` binds all interfaces. Default: loopback only (`127.0.0.1` and `::1`) |
| `McpAuth:ApiKey` (`McpAuth__ApiKey`) | If set, `/mcp` requires `Authorization: Bearer <key>` or `X-API-Key` (`/health` stays open) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Enables OTLP export (Aspire sets it automatically) |

The server binds to loopback only by default, so the unauthenticated endpoint is never exposed on external interfaces.

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

## Run with Aspire

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

Create `Modules/Jira/` with options, DTOs, an adapter and tools, the same way as `Modules/Confluence/`. Then wire it up in three steps:

```csharp
// Modules/Jira/JiraModule.cs
public static class JiraModule
{
    public static IMcpServerBuilder AddJiraModule(this IMcpServerBuilder mcp, IConfiguration config, JsonSerializerOptions? json = null)
    {
        var registry = ModuleRegistry.From(mcp.Services);
        var options = new JiraOptions();
        config.GetSection("Jira").Bind(options);                       // 1) options (source-generated binder)
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || string.IsNullOrWhiteSpace(options.ApiToken))
        {
            registry.MarkDisabled("jira", "Jira:BaseUrl and Jira:ApiToken not set");
            return mcp;                                                  // no tools, zero tokens
        }

        mcp.Services.AddSingleton(options);
        mcp.Services.AddModuleHttpClient("jira", new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            StaticAuthHandler.Bearer(options.ApiToken), TimeSpan.FromSeconds(30));
        mcp.WithTools<JiraTools>(json);                                  // 3) conditional WithTools<T>()
        registry.MarkEnabled("jira");
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

In the AppHost, uncomment `Provider.Create(builder, "jira", "Jira", …)` to get the `jira-url`, `jira-email` and `jira-token` parameters and the `Jira__*` environment variables. Prefix the tool names (`jira_*`, `github_*`) so they never collide.

## License

MIT
