# Aspire: what it gives you

devtools-mcp runs fine on its own; [Aspire](https://aspire.dev) is optional. The AppHost in `src/DevToolsMcp.AppHost` is for **developing and observing** the server: you see every tool call, every downstream REST request and every payload size, and your coding agent can see them too.

Requires the Aspire 17 CLI (`dotnet tool install -g Aspire.Cli`) and the .NET 11 SDK.

```bash
aspire run --project src/DevToolsMcp.AppHost     # or just `aspire run` from the repo root (aspire.config.json)
```

## What the AppHost sets up

| | What happens |
| --- | --- |
| **Binary or project** | If `artifacts/publish/<rid>/devtools-mcp` exists, the published Native AOT binary runs (`AddExecutable`), so you observe exactly what you ship. Otherwise the project runs (`AddDotnetProject`), which is faster to iterate on. |
| **Fixed URL** | The endpoint stays `http://localhost:5180`, through Aspire's proxy, so client configs from [clients.md](clients.md) keep working. |
| **MCP server resource** | `.WithMcpServer(path: "/mcp")` declares the resource as an MCP server, so the Aspire MCP server can proxy its tools ([below](#let-your-coding-agent-use-it)). |
| **Health** | `/health` is the resource's health check; the dashboard shows it as healthy once the server answers. |
| **Settings link** | The dashboard's resource URL is labelled **Settings** and opens the settings page. The browser no longer opens by itself (`DevToolsMcp__OpenBrowser=false`). |
| **Telemetry** | OTLP is wired automatically (`OTEL_EXPORTER_OTLP_ENDPOINT`), so traces, metrics and logs show up in the dashboard. |

## Parameters instead of the settings page

The AppHost defines one set of parameters per provider and injects them as environment variables:

| Parameter | Environment variable | Required |
| --- | --- | --- |
| `confluence-url`, `confluence-token` (secret) | `Confluence__BaseUrl`, `Confluence__ApiToken` | yes |
| `confluence-email`, `confluence-deployment` (`Auto`) | `Confluence__Email`, `Confluence__DeploymentType` | no |
| `jira-url`, `jira-token`, `jira-email`, `jira-deployment` | `Jira__*` | no |
| `github-url`, `github-token` | `GitHub__BaseUrl`, `GitHub__ApiToken` | no |
| `archi-path` | `Archi__ExecutablePath` | no |

The dashboard asks for missing required values on start. To store them once, use user secrets:

```bash
dotnet user-secrets set Parameters:confluence-url   https://acme.atlassian.net --project src/DevToolsMcp.AppHost
dotnet user-secrets set Parameters:confluence-token <token>                   --project src/DevToolsMcp.AppHost
```

Optional parameters that you leave empty inject nothing, so the settings page still works for those modules. Environment variables win over the settings page, so a value set here can't be changed there while Aspire runs.

## What you see in the dashboard

**Traces.** Each MCP request is a trace: the incoming `POST /mcp`, the SDK's tool-call span (source `ModelContextProtocol`), and every outgoing HTTP call to Confluence, Jira or GitHub with URL, status and duration. This answers "why was that call slow?" and "how many REST calls does `jira_update_issue` really make?". `/health` probes are filtered out.

**Metrics.** Besides the standard ASP.NET Core, HttpClient and runtime metrics, two custom histograms (meter `DevToolsMcp`) measure token cost directly:

| Metric | Unit | Tags | Use |
| --- | --- | --- | --- |
| `devtools.mcp.tool.output.size` | characters | `mcp.tool.name` | What each tool returns to the model. Divide by ~4 for tokens. |
| `devtools.http.client.response.body.size` | bytes | `devtools.module`, `http.request.method`, `http.response.status_code` | What the downstream API sent. |

Put the two side by side to see how much each tool saves over the raw API. This is how the numbers in [token-savings.md](token-savings.md) can be checked.

**Structured logs and console.** Server logs (with scopes) are searchable per resource. Errors from the downstream APIs show up there with the request that caused them.

## Let your coding agent use it

Aspire ships its own MCP server that gives AI agents the dashboard's data: resource state, console and structured logs, traces, and resource commands. Set it up once in the repository:

```bash
aspire agent init       # choose "Install Aspire MCP server"
```

It writes the config for VS Code (`.vscode/mcp.json`), Claude Code (`.mcp.json`), Copilot CLI and OpenCode; each runs `aspire agent mcp` over stdio. Then you can ask, for example:

* "The devtools-mcp resource is unhealthy. Check its console logs and tell me why."
* "Show me the slowest trace for `confluence_get_page` in the last run and which downstream call took the time."
* "Restart devtools-mcp." (`execute_resource_command`)

Because the AppHost marks devtools-mcp with `.WithMcpServer`, the Aspire MCP server also **proxies devtools-mcp's own tools**. An agent connected only to Aspire can call `confluence_*`, `jira_*` and `github_*` tools without a separate entry for devtools-mcp. If you configured devtools-mcp directly as well, the tools appear twice; keep one of the two.

From the command line, the same proxy is handy for testing tools without any AI client:

```bash
aspire mcp tools                         # list tools of running resources (add --format Json for schemas)
aspire mcp call devtools-mcp jira_search --input '{"query":"assignee = currentUser()","limit":5}'
aspire mcp call devtools-mcp confluence_get_page --input '{"page_id":"123456"}'
```

`WithMcpServer` declares no authentication, so leave `McpAuth:ApiKey` unset while you use the proxy.

## Telemetry without Aspire

The server exports OTLP whenever `OTEL_EXPORTER_OTLP_ENDPOINT` is set, with or without the AppHost. To see the released binary's telemetry, run the standalone Aspire dashboard:

```bash
docker run --rm -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 ./devtools-mcp
```

Open `http://localhost:18888` (the container log prints the login link). Any other OTLP backend (Jaeger, Grafana, Seq, …) works the same way.

## Extending the AppHost

* **A new provider** needs one line: `Provider.Create(builder, "<prefix>", "<Section>", …)` gives `<prefix>-url`, `-email`, `-token` parameters and the `<Section>__*` variables. Pass `optional: true` so it never blocks startup, and `withEmail: false` if the service has no email login.
* **Hide a resource from agents** with `.ExcludeFromMcp()`; it then disappears from the Aspire MCP server, including its telemetry.
* **Measure** a change: publish the AOT binary, run `aspire run`, repeat the same prompts, and compare the two size metrics before and after.
