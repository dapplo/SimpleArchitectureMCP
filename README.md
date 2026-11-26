# SimpleMCP: devtools-mcp

A lean, locally running [Model Context Protocol](https://modelcontextprotocol.io) server for developer tools, built to keep LLM token usage low.

* **.NET 11**, `WebApplication.CreateSlimBuilder`, official `ModelContextProtocol.AspNetCore` SDK over **Streamable HTTP** (`/mcp`), or **stdio** with `--stdio` so clients can start it themselves
* Published as a **single-file, self-contained Native AOT** executable (~24 MB, about 60–80 ms from launch to healthy)
* **Double-click to run**: a built-in settings page (`http://localhost:5180/`) opens on first start; paste your URL and token and the tools switch on without a restart
* Optional **Aspire 17** AppHost for development, with OpenTelemetry traces, metrics and logs in the Aspire dashboard
* Integrations: **Atlassian Confluence** and **Jira** (Cloud and Data Center / Server), **GitHub issues** (github.com and GitHub Enterprise) and **ArchiMate diagrams** (PNG export with a local [Archi](https://www.archimatetool.com) install, Open Exchange Format from compact JSON, draw.io diagrams on Confluence pages)
* Vertical feature slices, so further providers plug in without touching the core

## Documentation

* [Connect your client](docs/clients.md): VS Code / GitHub Copilot, Copilot CLI, Claude Code, Claude Desktop, Cursor, Windsurf, JetBrains AI Assistant and Codex CLI, over HTTP or stdio, with and without an API key
* [What you can do with it](docs/examples.md): example prompts and cross-tool workflows
* [Aspire](docs/aspire.md): dashboard, telemetry, parameters, and the Aspire MCP server for coding agents
* [Token savings](docs/token-savings.md): measurements and why they matter

## Why it is cheap on tokens

See [docs/token-savings.md](docs/token-savings.md) for what this brings, why it matters, and measurements against raw APIs and the official GitHub MCP server.

| Technique | Effect |
| --- | --- |
| Tools of unconfigured modules are **hidden from `tools/list`** | Unused integrations add zero schema tokens |
| Terse tool descriptions, primitive parameters | All 7 Confluence tool schemas total ≈ 3.2 KB (~800 tokens); the 7 Jira tools ≈ 3.1 KB (~780 tokens); the 6 GitHub tools ≈ 3.2 KB (~800 tokens); the 3 Archi tools ≈ 4 KB (~1000 tokens) |
| Jira issues are returned as front matter + **Markdown**, with only the fields that matter | No custom fields, avatars, `self` links or changelog |
| Pages are returned as **Markdown**, not storage XHTML | Usually several times smaller than the raw body |
| Search/children return one `id \| title \| …` line per hit | No `_links`, `_expandable`, reactions, node ids or user objects |
| One `github_update_issue` for edit, close/reopen, labels and assignees | One schema instead of five; a single call changes several things |
| Archi tools return **PNG paths**, not pictures, unless `inline=true` | A path costs a few tokens; an image costs hundreds or more |
| `archi_create_model` takes ids + types + names; layout is automatic | No coordinates, styles or XML in the prompt |
| `confluence_append_to_page` | Adds content without the model re-sending the whole page |
| Automatic version handling | No failed calls and retries for version conflicts |
| `devtools.mcp.tool.output.size` metric | See exactly how many characters each tool returns |

## Quick start (no SDK needed)

1. Download the archive for your OS from the latest [release](../../releases) and extract it.
2. Double-click `devtools-mcp` (`devtools-mcp.exe` on Windows). A console window shows the server log.
3. On first start the settings page opens in your browser (`http://localhost:5180/`). Fill in the modules you use (Confluence and Jira: base URL, email for Cloud, API token; GitHub: a personal access token, plus the URL only for GitHub Enterprise), then **Save**. The page tests the connection, and that module's tools become available immediately.
4. Point your MCP client at `http://localhost:5180/mcp`, or let the client start `devtools-mcp --stdio` itself. [docs/clients.md](docs/clients.md) has the setup for VS Code / GitHub Copilot, Claude Code, Claude Desktop, Cursor, Windsurf, JetBrains and Codex. If the client was already connected, reload its MCP server so it fetches the new tool list.
5. Ask away: [docs/examples.md](docs/examples.md) has example prompts and workflows across Confluence, Jira and GitHub.

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
      ConfluenceTools.cs         The 7 MCP tools
      DrawioMacro.cs             The draw.io app's macro in storage format
    Modules/Jira/                The Jira vertical slice (same shape: options, connection, adapters, service, tools)
      JiraAdapterBase.cs         REST API v2 on Cloud and Data Center
      CloudJiraAdapter.cs        /search/jql, users by accountId
      DataCenterJiraAdapter.cs   /search, users by name, context path respected
      JiraWikiConverter.cs       Jira wiki markup <-> Markdown
      JiraService.cs             Validation, user resolution, compact output
      JiraTools.cs               The 7 MCP tools
    Modules/GitHub/              The GitHub issues slice (github.com, ghe.com, Enterprise Server)
      GitHubModule.cs            AddGitHubModule(): conditional registration, settings form
      GitHubOptions.cs           Token, Enterprise URL -> API root, default repository
      GitHubApi.cs               REST calls (Utf8JsonWriter request bodies)
      GitHubJsonContext.cs       Source-generated DTOs
      GitHubService.cs           Validation, repo resolution, compact output
      GitHubTools.cs             The 6 MCP tools
    Modules/Archi/               ArchiMate diagrams through a local Archi install
      ArchiModule.cs             AddArchiModule(): conditional registration, settings form
      ArchiOptions.cs            Executable, renderer, output folder, scale, timeout
      ArchiModelSpec.cs          The compact JSON model (source-generated context)
      ExchangeModelBuilder.cs    JSON -> Open Exchange Format XML with automatic layout
      ArchiRunner.cs             Runs Archi's command line (xvfb-run on headless Linux)
      ArchiService.cs            Render via HTML report or jArchi script, collect PNGs
      DrawioConverter.cs         Exchange XML -> draw.io file (ArchiMate 3 shapes)
      ArchiTools.cs              The 3 MCP tools
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
| `confluence_add_drawio` | `page_id`, `file` (`.drawio`), `name?` (default the file name), `png?` (preview) | `Added\|Updated draw.io diagram '<name>' (attachment v<n>) on <id>; page version <n>.` |

Plain-text queries become `type = page AND text ~ "..."`; queries that already contain CQL operators pass through unchanged. `space_key` is validated and AND-ed in front, and any `ORDER BY` stays at the end.

`confluence_add_drawio` stores the diagram the way the draw.io app for Confluence does: the `.drawio` XML as a page attachment named after the diagram (no extension), the optional PNG as `<name>.png`, and a `drawio` macro (`diagramName`, `revision` = attachment version) appended to the page. Running it again with the same name uploads a new attachment version and only bumps the existing macro's revision, so the diagram updates in place. The draw.io app must be installed; the upload uses the v1 `content/{id}/child/attachment` endpoint on both Cloud and Data Center. The file must be an ordinary local file (same hidden-folder guard as Jira attachments); devtools-mcp's own output folder `%TEMP%/devtools-mcp`, where the Archi tools write by default, is always allowed.

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

## Jira tools

Basic issue work with your own Jira permissions. There are no admin tools (no projects, workflows, schemes or user management).

| Tool | Inputs | Returns |
| --- | --- | --- |
| `jira_search` | `jql` (JQL, an issue key, or plain text), `limit` (20, max 50) | `key \| type \| status \| assignee \| yyyy-MM-dd \| summary` per issue |
| `jira_get_issue` | `issue_key`, `comments` (5, max 50) | Front matter (key, summary, type, status, priority, assignee, labels, parent, url, attachments, …) + Markdown description + latest comments |
| `jira_create_issue` | `project_key`, `summary`, `issue_type` (`Task`), `description?`, `assignee?`, `labels?`, `priority?`, `parent_key?` | `Created <key> \| <url>` |
| `jira_update_issue` | `issue_key`, `summary?`, `description?`, `priority?`, `add_labels?`, `remove_labels?`, `assignee?` | `Updated <key>: <changed fields>.` |
| `jira_transition_issue` | `issue_key`, `transition?`, `comment?`, `resolution?` | Without `transition`: `id \| name → status` per available transition. With it: `<key> → <status>.` |
| `jira_add_comment` | `issue_key`, `body` | `Commented on <key>.` |
| `jira_attach_file` | `issue_key`, `file_path` | `Attached <name> (<size>) to <key>.` |

* **Markdown in, Markdown out.** Descriptions and comments are written and read as Markdown and converted to and from Jira wiki markup. Both Cloud and Data Center use REST API v2, which stores wiki markup, so there is no Atlassian Document Format JSON in the prompt.
* **Close an issue** with `jira_transition_issue` and `transition=Done` (or the transition's name or id, or the target status). Pass `resolution` if your workflow asks for one. A wrong name returns the list of valid transitions.
* **Assignees** can be `me`, `none` (unassign), a username, an email, a display name or (Cloud) an account id. Ambiguous names fail with a short list of candidates, before anything is written. Assigning uses the assignee endpoint, which needs only the Assign permission.
* **Labels** are added and removed individually, so existing labels are never overwritten.
* **Plain-text search** becomes `text ~ "..." ORDER BY updated DESC`; a bare key like `abc-12` becomes `key = ABC-12`. Cloud uses the enhanced `/rest/api/2/search/jql` endpoint; Data Center uses `/rest/api/2/search`.
* An unknown `issue_type` on create returns the project's valid issue types in the error.
* **Attachments** are read from the local disk with your permissions, up to 100 MB. Files in hidden files or folders (`~/.ssh`, `~/.aws`, `.env`, `~/.config/devtools-mcp`, …) or in Windows `AppData` are refused, also through symbolic links, so a prompt-injected issue cannot talk the model into uploading credentials.

Jira configuration mirrors Confluence: `Jira:BaseUrl`, `Jira:Email` (Cloud only), `Jira:ApiToken` and `Jira:DeploymentType` (`Auto` picks Cloud for `*.atlassian.net` or when an email is set). On Cloud, any path in the URL (`/jira/software/...`) is ignored; on Data Center the context path (`https://host/jira`) is kept.

## GitHub tools

| Tool | Inputs | Returns |
| --- | --- | --- |
| `github_search_issues` | `query` (GitHub search syntax), `repo?`, `limit` (20, max 50) | `#n \| state \| title \| labels \| assignees \| yyyy-MM-dd` per hit |
| `github_get_issue` | `number`, `repo?`, `comments` (latest 20, 0–100) | Front matter (state, author, labels, assignees, milestone, url) + body + `[comment_id] @author date` comments |
| `github_create_issue` | `title`, `body?`, `repo?`, `labels?`, `assignees?`, `milestone?` | `Created owner/repo#n \| <url>` |
| `github_update_issue` | `number`, `repo?`, `title?`, `body?`, `state?`, `state_reason?`, `milestone?` (0 clears), `add_labels?`, `remove_labels?`, `add_assignees?`, `remove_assignees?` | `Updated owner/repo#n: <what changed>.` |
| `github_comment` | `number`, `body`, `repo?`, `comment_id?` (edit that comment instead) | `Commented <id> \| <url>` |
| `github_list_labels` | `repo?` | `name \| description` per label |

* Everything runs with **your token's permissions**; there are no admin tools (no repository, team, webhook or branch-protection settings). A fine-grained token needs **Issues: read and write** on the repositories you pick; a classic token needs `repo` (or `public_repo`).
* `repo` is `owner/repo` (a pasted `https://github.com/owner/repo` URL works too). It is optional everywhere when `GitHub:DefaultRepo` is set.
* Search adds `is:issue` unless the query already says `is:pr` / `is:issue`, and adds `repo:<repo>` (or the default repository) unless the query has its own `repo:`, `org:` or `user:` qualifier. Inside one repository, refs are printed as `#n` to save tokens.
* `github_get_issue` works for pull requests too (they share the issue number space) and marks them `(pull request)`. Bodies are already Markdown, so they pass through unchanged.
* Closing: `state=closed` with `state_reason` `completed` (default), `not_planned` or `duplicate`. `state_reason` alone also closes (or reopens, for `reopened`).
* **Attachments are not supported**: GitHub's REST and GraphQL APIs have no endpoint for uploading issue attachments (the web editor uses an internal upload flow). Link to files instead, e.g. a file committed to the repository or a release asset.

| Key (env var) | Meaning |
| --- | --- |
| `GitHub:ApiToken` (`GitHub__ApiToken`) | Required to enable the module: personal access token (classic or fine-grained) |
| `GitHub:BaseUrl` (`GitHub__BaseUrl`) | Empty for github.com. GitHub Enterprise Server: `https://github.example.com` (`/api/v3` is added). Enterprise Cloud with data residency: `https://SUBDOMAIN.ghe.com` |
| `GitHub:DefaultRepo` (`GitHub__DefaultRepo`) | Optional `owner/repo` used when a call omits `repo` |
| `GitHub:TimeoutSeconds` | Total request timeout (default 30) |

## Archi tools

Renders ArchiMate views to PNG with your local [Archi](https://www.archimatetool.com) (5.x), headless through its command line, and writes [Open Exchange Format](https://www.opengroup.org/open-group-archimate-model-exchange-file-format) files from a compact JSON model.

| Tool | Inputs | Returns |
| --- | --- | --- |
| `archi_render` | `file` (`.archimate` or exchange `.xml`), `views?` (names or ids, default all), `out_dir?`, `inline` (false) | `view \| png path \| WxH` per view, plus the images when `inline` |
| `archi_create_model` | `model` (JSON below), `path?` (`.xml`), `render` (true), `inline` (false) | `<xml path> (n elements, n relationships, views: …)` then the `archi_render` lines |
| `archi_to_drawio` | `file` (exchange `.xml` or `.archimate`), `views?`, `path?` (`.drawio`) | `<drawio path> (pages: …)` |

```json
{
  "name": "Web shop",
  "elements": [
    { "id": "cust", "type": "BusinessActor", "name": "Customer" },
    { "id": "order", "type": "BusinessProcess", "name": "Place order" },
    { "id": "shop", "type": "ApplicationComponent", "name": "Shop" },
    { "id": "db", "type": "DataObject", "name": "Orders" },
    { "id": "srv", "type": "Node", "name": "Web server" }
  ],
  "relationships": [
    { "type": "Assignment", "source": "cust", "target": "order" },
    { "type": "Serving", "source": "shop", "target": "order" },
    { "type": "Access", "source": "shop", "target": "db", "access": "ReadWrite" },
    { "type": "Realization", "source": "srv", "target": "shop" }
  ],
  "views": [
    { "name": "Overview" },
    { "name": "Deployment", "nodes": [ { "ref": "srv", "nodes": [ { "ref": "shop" } ] } ] }
  ]
}
```

* `type` is any ArchiMate 3.1 element type (`BusinessActor`, `ApplicationComponent`, `Node`, `Goal`, `WorkPackage`, `Grouping`, `Location`, `AndJunction`/`OrJunction`, …; case and spaces don't matter) and relationship type (`Composition`, `Aggregation`, `Assignment`, `Realization`, `Serving`, `Access`, `Influence`, `Triggering`, `Flow`, `Specialization`, `Association`).
* Without `views` there is one view with every element. A view lists `elements` (auto layout) and/or `nodes` (nesting; optional `x`/`y`/`w`/`h`, relative to the parent). Auto layout puts each ArchiMate layer in its own row band, top to bottom: motivation, strategy, business, application, technology, physical, implementation, other. Parents grow to fit nested children.
* Every relationship whose ends are both on a view is drawn (`"connections": false` turns that off), except structural ones between a parent and its nested child, which the nesting already shows.
* Errors are collected into one message (unknown types, duplicate ids, dangling references), so a model can fix them in one go. The output validates against the Open Group `archimate3_Diagram.xsd`.
* PNGs are named after their views and overwrite older ones. Tools only write `.png`/`.xml` files and refuse hidden or `AppData` folders (except the configured output folder).

**draw.io.** `archi_to_drawio` writes one draw.io page per view with draw.io's ArchiMate 3 shapes, Archi's default layer colours, the same positions, nesting and bendpoints, and the ArchiMate line and arrow styles (access read/write direction, directed association, …). Exchange `.xml` is converted without starting Archi; an `.archimate` model is first exported by Archi (`--xmlexchange.export`). Together with `confluence_add_drawio` this puts an editable diagram on a Confluence page in two calls: `archi_create_model` (or an existing model) → `archi_to_drawio` → `confluence_add_drawio`.

**Renderer.** `HtmlReport` (default) works with plain Archi: it imports the model, creates Archi's HTML report in a temporary folder and takes the view PNGs from it (100% scale). `jArchi` runs a generated script with the [jArchi scripting plugin](https://www.archimatetool.com/plugins/) instead: it renders only the requested views, faster, and honours `Archi:Scale` and `Archi:Margin`. **Test connection** on the settings page tells you whether jArchi is installed.

**Headless Linux.** Archi is an SWT application and needs a display even on the command line. Without `DISPLAY`/`WAYLAND_DISPLAY`, devtools-mcp runs it through `xvfb-run -a` (install `xvfb`). One Archi process runs at a time; a run usually takes a few seconds for the JVM start.

| Key (env var) | Meaning |
| --- | --- |
| `Archi:ExecutablePath` (`Archi__ExecutablePath`) | Required to enable the module: `Archi.exe` (Windows), the `Archi` launcher (Linux), `Archi.app` (macOS) or the folder that contains it |
| `Archi:Renderer` | `HtmlReport` (default) or `jArchi` |
| `Archi:OutputDirectory` | Where PNG and `.xml` files go when a call names no path (default `<temp>/devtools-mcp/archi`) |
| `Archi:Scale`, `Archi:Margin` | jArchi only: scale in percent (50–400, default 100) and border in pixels (default 10) |
| `Archi:TimeoutSeconds` | Per Archi run (default 120) |

The settings page shows the first three; scale, margin and timeout are set in `appsettings.json` or environment variables.

## Configuration

| Key (env var) | Meaning |
| --- | --- |
| `Confluence:BaseUrl` (`Confluence__BaseUrl`) | Required to enable the module |
| `Confluence:ApiToken` (`Confluence__ApiToken`) | Required: Cloud API token or Data Center PAT |
| `Confluence:Email` (`Confluence__Email`) | Cloud account email; leave empty for Data Center PATs |
| `Confluence:DeploymentType` (`Confluence__DeploymentType`) | `Auto` \| `Cloud` \| `DataCenter` |
| `Confluence:TimeoutSeconds` | Total request timeout (default 30) |
| `Jira:BaseUrl`, `Jira:ApiToken`, `Jira:Email`, `Jira:DeploymentType`, `Jira:TimeoutSeconds` (`Jira__…`) | Same meaning, for Jira |
| `--stdio` (command-line switch) | MCP over stdin/stdout for clients that start the server themselves. The settings page and `/health` stay on HTTP; logs go to stderr. If the port is taken, the instance keeps serving stdio and watches `settings.json` for changes made on the other instance's page |
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

Aspire is not needed to use the server. It is useful while developing modules: the dashboard shows every tool call, downstream request, latency and payload size. [docs/aspire.md](docs/aspire.md) describes everything it offers, including the Aspire MCP server for coding agents.

```bash
dotnet tool install -g Aspire.Cli    # Aspire 17 runs AppHosts through the CLI bundle
aspire run --project src/DevToolsMcp.AppHost
```

The AppHost:

* defines the parameters `confluence-url`, `confluence-email` (optional), `confluence-token` (secret) and `confluence-deployment`, and injects them as `Confluence__BaseUrl`, `Confluence__Email`, `Confluence__ApiToken` and `Confluence__DeploymentType`. The `jira-*` parameters work the same way for `Jira__*`, but are optional so they never block startup. Fill them in when the dashboard prompts, or with `dotnet user-secrets set Parameters:confluence-token … --project src/DevToolsMcp.AppHost`;
* defines the optional parameters `github-url` and `github-token` (secret), injected as `GitHub__BaseUrl` and `GitHub__ApiToken`. They never block startup; leave them empty to configure GitHub on the settings page instead. The optional `archi-path` becomes `Archi__ExecutablePath`;
* launches the **published AOT binary** through `AddExecutable` when `artifacts/publish/<current-rid>/devtools-mcp` exists. Otherwise it falls back to the project (`AddDotnetProject`);
* exposes `http://localhost:5180` (Aspire proxy → `PORT`), marks it with `.WithMcpServer(path: "/mcp")`, adds a `/health` check, and wires OTLP so tool calls, downstream Confluence calls, latencies and payload sizes (`devtools.http.client.response.body.size`) show up in the dashboard.

To check that Aspire uses the AOT binary, publish it first and run `aspire run`. The `devtools-mcp` resource then shows `artifacts/publish/<rid>/devtools-mcp` as its command, and `/health` answers through port 5180.

## Use from VS Code / GitHub Copilot

Other clients and the API-key variant: see [docs/clients.md](docs/clients.md).

`.vscode/mcp.json`:

```jsonc
{
  "servers": {
    "devtools-mcp": { "type": "http", "url": "http://localhost:5180/mcp" }
  }
}
```

## Adding a provider

Create `Modules/<Name>/` with options, DTOs, an adapter and tools, the same way as `Modules/Confluence/` and `Modules/Jira/` (`JiraConnection` shows how to rebuild the adapter when settings change). The wiring, using Jira as the example:

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

The settings page renders the new module's form automatically. In the AppHost, one `Provider.Create(builder, "jira", "Jira", …)` line gives the `jira-url`, `jira-email` and `jira-token` parameters and the `Jira__*` environment variables (`Modules/GitHub` and its `Provider.Create(builder, "github", "GitHub", withEmail: false, optional: true)` line are a second, smaller example). Prefix the tool names (`jira_*`, `github_*`) so they never collide.

## License

MIT
