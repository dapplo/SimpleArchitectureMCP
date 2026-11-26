# Connect your client

There are two ways to connect a client:

| | **HTTP** (default) | **stdio** (`--stdio`) |
| --- | --- | --- |
| Who starts devtools-mcp | You (double-click, autostart, Aspire) | The client, whenever it needs it |
| Address | `http://localhost:5180/mcp` | `<path>/devtools-mcp --stdio` |
| Several clients at once | Share one running instance | Each client starts its own process |
| New tools after a settings change | Reload the MCP server in the client | Announced to the client automatically (`tools/list_changed`), where the client supports it |
| API key | Optional (`McpAuth:ApiKey`) | Not needed: nothing listens for MCP on the network |

Every client below supports both, so pick what you prefer. HTTP suits you if you keep devtools-mcp running anyway; stdio means nothing to start by hand.

All examples name the server `devtools-mcp`, the same name the settings page uses in its **Connect your client** snippet.

* The client only sees the tools of modules you configured on the settings page (`http://localhost:5180/`).
* With HTTP, start devtools-mcp first, and **reload the MCP server in your client** after you save new settings so it fetches the new tool list.
* The port is 5180 unless you set `DevToolsMcp:Port` or `PORT`. Change the URL accordingly.

| Client | Config |
| --- | --- |
| [VS Code / GitHub Copilot](#vs-code--github-copilot) | `.vscode/mcp.json` or user `mcp.json` |
| [GitHub Copilot CLI](#github-copilot-cli) | `~/.copilot/mcp-config.json` |
| [Claude Code](#claude-code) | `claude mcp add` |
| [Claude Desktop](#claude-desktop) | `claude_desktop_config.json` (stdio only) |
| [Cursor](#cursor) | `~/.cursor/mcp.json` or `.cursor/mcp.json` |
| [Windsurf](#windsurf) | `~/.codeium/windsurf/mcp_config.json` |
| [JetBrains AI Assistant](#jetbrains-ai-assistant-and-junie) | Settings dialog, JSON |
| [OpenAI Codex CLI](#openai-codex-cli) | `~/.codex/config.toml` |
| [Anything else](#other-clients) | |

## Stdio mode

Start the binary with `--stdio` and the client talks MCP to it over stdin/stdout. Use the **full path** to the binary you extracted from the release, for example:

* Windows: `C:\Tools\devtools-mcp\devtools-mcp.exe`, written `C:\\Tools\\devtools-mcp\\devtools-mcp.exe` inside JSON
* macOS / Linux: `/Users/me/tools/devtools-mcp/devtools-mcp` or `/home/me/tools/devtools-mcp/devtools-mcp`

What changes, and what stays the same:

* **Settings** come from the same per-user `settings.json`, environment variables and `appsettings.json` as in HTTP mode, so a module you configured once works in both modes.
* **The settings page** stays available at `http://localhost:5180/`. On first start, when nothing is configured yet, it still opens in your browser. Save there and the tools appear in the client without a restart.
* **Already running?** If another devtools-mcp (HTTP or stdio) holds port 5180, the stdio instance still serves its client, and you edit settings on the page of the instance that runs already. The stdio instance watches `settings.json` and picks the change up.
* **Logs** go to stderr, which most clients show in their MCP log/output panel. stdout carries only MCP messages.
* **Lifetime**: the process ends when the client closes the connection.

Examples below use `/path/to/devtools-mcp`; replace it with your path.

## With an API key

By default the server listens on loopback only (`127.0.0.1` / `::1`) and needs no key. If you set `McpAuth:ApiKey` (environment variable `McpAuth__ApiKey`), `/mcp` requires one of these headers:

```
Authorization: Bearer <key>
X-API-Key: <key>
```

This applies to HTTP only; stdio mode never needs a key. Each section below shows where the header goes. Keep the key out of files you commit: use the client's input prompt or an environment variable (`DEVTOOLS_MCP_KEY` in the examples).

## VS Code / GitHub Copilot

This repository already contains [`.vscode/mcp.json`](../.vscode/mcp.json). For any other workspace, create `.vscode/mcp.json`, or run **MCP: Open User Configuration** to make it available in every workspace. **MCP: Add Server** → *HTTP* does the same through a guided dialog.

```jsonc
{
  "servers": {
    "devtools-mcp": { "type": "http", "url": "http://localhost:5180/mcp" }
  }
}
```

With an API key, VS Code prompts for it once and stores it securely:

```jsonc
{
  "servers": {
    "devtools-mcp": {
      "type": "http",
      "url": "http://localhost:5180/mcp",
      "headers": { "Authorization": "Bearer ${input:devtools-mcp-key}" }
    }
  },
  "inputs": [
    { "id": "devtools-mcp-key", "type": "promptString", "description": "devtools-mcp API key", "password": true }
  ]
}
```

`${env:DEVTOOLS_MCP_KEY}` works instead of `${input:…}` if you prefer an environment variable.

Stdio:

```jsonc
{
  "servers": {
    "devtools-mcp": { "type": "stdio", "command": "/path/to/devtools-mcp", "args": ["--stdio"] }
  }
}
```
 Use **MCP: List Servers** to restart the server after you change settings, and pick the tools in Copilot Chat's *agent* mode tool picker.

## GitHub Copilot CLI

`~/.copilot/mcp-config.json` (or `$COPILOT_HOME/mcp-config.json`):

```json
{
  "mcpServers": {
    "devtools-mcp": {
      "type": "http",
      "url": "http://localhost:5180/mcp",
      "tools": ["*"]
    }
  }
}
```

With an API key, add `"headers": { "Authorization": "Bearer ${DEVTOOLS_MCP_KEY}" }`.

Stdio: `{ "type": "local", "command": "/path/to/devtools-mcp", "args": ["--stdio"], "tools": ["*"] }` as the `devtools-mcp` entry.

## Claude Code

```bash
claude mcp add --transport http --scope user devtools-mcp http://localhost:5180/mcp
```

* `--scope user` makes it available in all your projects (stored in `~/.claude.json`). Leave it out to add it only for the current project, or use `--scope project` to write a shared `.mcp.json` into the repository.
* With an API key: add `--header "Authorization: Bearer <key>"`.
* Check with `claude mcp list`, or `/mcp` inside Claude Code (which also reconnects after you change settings).

A project `.mcp.json` that reads the key from the environment:

```json
{
  "mcpServers": {
    "devtools-mcp": {
      "type": "http",
      "url": "http://localhost:5180/mcp",
      "headers": { "Authorization": "Bearer ${DEVTOOLS_MCP_KEY}" }
    }
  }
}
```

`"type": "http"` is required; without it Claude Code treats the entry as a stdio server and skips it.

Stdio (everything after `--` is the command Claude Code runs):

```bash
claude mcp add --scope user devtools-mcp -- /path/to/devtools-mcp --stdio
```

## Claude Desktop

Claude Desktop starts local MCP servers over stdio from `claude_desktop_config.json` (Settings → Developer → Edit Config). Its **custom connectors** expect a public **HTTPS** URL, so they cannot reach `http://localhost`.

* macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
* Windows: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "devtools-mcp": {
      "command": "/path/to/devtools-mcp",
      "args": ["--stdio"]
    }
  }
}
```

On Windows: `"command": "C:\\Tools\\devtools-mcp\\devtools-mcp.exe"`. Restart Claude Desktop after editing the file.

<details>
<summary>Alternative: share a running HTTP instance through <code>mcp-remote</code></summary>

If you keep devtools-mcp running in HTTP mode and want Claude Desktop to use that same instance, the [`mcp-remote`](https://github.com/geelen/mcp-remote) bridge (needs [Node.js](https://nodejs.org)) translates between stdio and HTTP:

```json
{
  "mcpServers": {
    "devtools-mcp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:5180/mcp", "--allow-http",
               "--header", "Authorization:${AUTH_HEADER}"],
      "env": { "AUTH_HEADER": "Bearer <key>" }
    }
  }
}
```

Leave out the `--header` pair and `env` if you don't use an API key. `--allow-http` lets `mcp-remote` use a plain `http://` URL, which is fine because the traffic never leaves your machine. The value lives in `env` with no space after the colon on purpose: Claude Desktop on Windows does not escape spaces inside `args`.

</details>

## Cursor

`~/.cursor/mcp.json` for all projects, or `.cursor/mcp.json` in a project:

```json
{
  "mcpServers": {
    "devtools-mcp": { "url": "http://localhost:5180/mcp" }
  }
}
```

With an API key from the environment:

```json
{
  "mcpServers": {
    "devtools-mcp": {
      "url": "http://localhost:5180/mcp",
      "headers": { "Authorization": "Bearer ${env:DEVTOOLS_MCP_KEY}" }
    }
  }
}
```

Stdio: `"devtools-mcp": { "command": "/path/to/devtools-mcp", "args": ["--stdio"] }`.

Toggle or refresh the server under Cursor Settings → MCP.

## Windsurf

`~/.codeium/windsurf/mcp_config.json` (Cascade → MCP → *Open MCP config file*, or Windsurf Settings → Add Server):

```json
{
  "mcpServers": {
    "devtools-mcp": { "serverUrl": "http://localhost:5180/mcp" }
  }
}
```

With an API key: add `"headers": { "Authorization": "Bearer ${env:DEVTOOLS_MCP_KEY}" }`. Press refresh in the MCP panel after changes.

Stdio: `"devtools-mcp": { "command": "/path/to/devtools-mcp", "args": ["--stdio"] }`.

## JetBrains AI Assistant and Junie

Settings → Tools → AI Assistant → Model Context Protocol (MCP) → **Add** → *HTTP*, paste the JSON, choose global or project level, then **OK** and **Apply**:

```json
{
  "mcpServers": {
    "devtools-mcp": { "url": "http://localhost:5180/mcp" }
  }
}
```

JetBrains documents only `url` for HTTP servers, no request headers. If you set an API key, use stdio instead (choose the stdio option in the same dialog); it needs no key:

```json
{
  "mcpServers": {
    "devtools-mcp": { "command": "/path/to/devtools-mcp", "args": ["--stdio"] }
  }
}
```

JetBrains' AI Assistant documentation does not cover Junie's own MCP settings. Where your Junie version has an MCP server list, the same `mcpServers` JSON is the format to use.

## OpenAI Codex CLI

`~/.codex/config.toml` (or `.codex/config.toml` in a trusted project):

```toml
[mcp_servers.devtools-mcp]
url = "http://localhost:5180/mcp"
```

With an API key, Codex reads it from an environment variable and sends it as a bearer token:

```toml
[mcp_servers.devtools-mcp]
url = "http://localhost:5180/mcp"
bearer_token_env_var = "DEVTOOLS_MCP_KEY"
```

`codex mcp add devtools-mcp --url http://localhost:5180/mcp` writes the first form for you.

Stdio:

```toml
[mcp_servers.devtools-mcp]
command = "/path/to/devtools-mcp"
args = ["--stdio"]
```

or `codex mcp add devtools-mcp -- /path/to/devtools-mcp --stdio`. `/mcp` inside Codex lists the connected servers.

## Other clients

* **Supports Streamable HTTP?** Use `http://localhost:5180/mcp`. Pick "HTTP" or "Streamable HTTP", not the older "SSE" transport.
* **stdio only?** Use `/path/to/devtools-mcp` as the command with the argument `--stdio`.
* **Check that the server is up:** `curl http://localhost:5180/health` returns `{"status":"ok","modules":[…]}` with the active modules. `/health` never needs the API key.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Client shows no tools | No module is configured yet. Open `http://localhost:5180/`, save a module, then reload the MCP server in the client. |
| Only some providers' tools appear | Tools of unconfigured modules are hidden on purpose (zero schema tokens). Configure the missing module. |
| `401 Unauthorized` | `McpAuth:ApiKey` is set; add the header shown above. |
| stdio server fails to start | Use the full path to the binary. If macOS blocks the downloaded binary, open it once with right-click → Open, or run `xattr -d com.apple.quarantine /path/to/devtools-mcp`. Check the client's MCP log for the stderr output. |
| Connection refused | devtools-mcp isn't running, or runs on another port. A second start exits and leaves the first instance running. |
| Works on the same machine, not from another one | By design: the server binds to loopback. `DevToolsMcp:AllowRemote=true` changes that and requires `McpAuth:ApiKey`; without a key the server refuses to start. |

Next: [example prompts and workflows](examples.md).
