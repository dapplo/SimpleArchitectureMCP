# Connect your client

devtools-mcp speaks **Streamable HTTP** at:

```
http://localhost:5180/mcp
```

Any MCP client that supports remote (HTTP) servers can use that URL directly. Clients that only start local **stdio** servers need a small bridge, [`mcp-remote`](https://github.com/geelen/mcp-remote) (Node.js), which is shown below where needed.

All examples name the server `devtools-mcp`, the same name the settings page uses in its **Connect your client** snippet.

* Start devtools-mcp first. The client only sees the tools of modules you configured on the settings page (`http://localhost:5180/`).
* After you save new settings, **reload the MCP server in your client** (or restart the client) so it fetches the new tool list.
* The port is 5180 unless you set `DevToolsMcp:Port` or `PORT`. Change the URL accordingly.

| Client | Transport | Config |
| --- | --- | --- |
| [VS Code / GitHub Copilot](#vs-code--github-copilot) | HTTP | `.vscode/mcp.json` or user `mcp.json` |
| [GitHub Copilot CLI](#github-copilot-cli) | HTTP | `~/.copilot/mcp-config.json` |
| [Claude Code](#claude-code) | HTTP | `claude mcp add --transport http` |
| [Claude Desktop](#claude-desktop) | stdio only for local servers | `claude_desktop_config.json` + `mcp-remote` |
| [Cursor](#cursor) | HTTP | `~/.cursor/mcp.json` or `.cursor/mcp.json` |
| [Windsurf](#windsurf) | HTTP | `~/.codeium/windsurf/mcp_config.json` |
| [JetBrains AI Assistant](#jetbrains-ai-assistant-and-junie) | HTTP (no headers documented) | Settings dialog, JSON |
| [OpenAI Codex CLI](#openai-codex-cli) | HTTP | `~/.codex/config.toml` |
| [Anything else](#other-clients) | HTTP or stdio bridge | |

## With an API key

By default the server listens on loopback only (`127.0.0.1` / `::1`) and needs no key. If you set `McpAuth:ApiKey` (environment variable `McpAuth__ApiKey`), `/mcp` requires one of these headers:

```
Authorization: Bearer <key>
X-API-Key: <key>
```

Each section below shows where the header goes. Keep the key out of files you commit: use the client's input prompt or an environment variable (`DEVTOOLS_MCP_KEY` in the examples).

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

`${env:DEVTOOLS_MCP_KEY}` works instead of `${input:…}` if you prefer an environment variable. Use **MCP: List Servers** to restart the server after you change settings, and pick the tools in Copilot Chat's *agent* mode tool picker.

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

## Claude Desktop

Claude Desktop's **custom connectors** expect a public **HTTPS** URL, so they cannot reach `http://localhost`. Use the `mcp-remote` bridge in `claude_desktop_config.json` instead (Settings → Developer → Edit Config):

* macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`
* Windows: `%APPDATA%\Claude\claude_desktop_config.json`

Requires [Node.js](https://nodejs.org) (for `npx`).

```json
{
  "mcpServers": {
    "devtools-mcp": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:5180/mcp", "--allow-http"]
    }
  }
}
```

`--allow-http` lets `mcp-remote` use a plain `http://` URL. That is fine here, because the traffic never leaves your machine.

With an API key:

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

The value lives in `env` and there is no space after the colon on purpose: Claude Desktop on Windows (like Cursor and Codex) does not escape spaces inside `args`.

Restart Claude Desktop after editing the file.

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

## JetBrains AI Assistant and Junie

Settings → Tools → AI Assistant → Model Context Protocol (MCP) → **Add** → *HTTP*, paste the JSON, choose global or project level, then **OK** and **Apply**:

```json
{
  "mcpServers": {
    "devtools-mcp": { "url": "http://localhost:5180/mcp" }
  }
}
```

JetBrains documents only `url` for HTTP servers, no request headers. If you use an API key, run the server through the stdio bridge instead (choose the stdio option in the same dialog):

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

`codex mcp add devtools-mcp --url http://localhost:5180/mcp` writes the first form for you. `/mcp` inside Codex lists the connected servers.

## Other clients

* **Supports Streamable HTTP?** Use `http://localhost:5180/mcp`. Pick "HTTP" or "Streamable HTTP", not the older "SSE" transport.
* **stdio only?** Use `npx -y mcp-remote http://localhost:5180/mcp --allow-http` as the command, as in the [Claude Desktop](#claude-desktop) section.
* **Check that the server is up:** `curl http://localhost:5180/health` returns `{"status":"ok","modules":[…]}` with the active modules. `/health` never needs the API key.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Client shows no tools | No module is configured yet. Open `http://localhost:5180/`, save a module, then reload the MCP server in the client. |
| Only some providers' tools appear | Tools of unconfigured modules are hidden on purpose (zero schema tokens). Configure the missing module. |
| `401 Unauthorized` | `McpAuth:ApiKey` is set; add the header shown above. |
| Connection refused | devtools-mcp isn't running, or runs on another port. A second start exits and leaves the first instance running. |
| Works on the same machine, not from another one | By design: the server binds to loopback. `DevToolsMcp:AllowRemote=true` changes that; set an API key if you do. |

Next: [example prompts and workflows](examples.md).
