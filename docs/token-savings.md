# What devtools-mcp brings, and why

devtools-mcp is a small MCP server that runs on your own machine and connects an AI assistant to the developer tools you already use (Confluence, GitHub, …). It doesn't add features those services lack. Its purpose is to make each interaction **cheaper in tokens**, so you pay less, wait less and keep more of the context window for your actual work.

| | Without devtools-mcp | With devtools-mcp |
| --- | --- | --- |
| Tool definitions sent with every request | Every tool of every server you connect, used or not | Only tools of modules you configured: 6 per provider (Confluence ≈ 0.6 K tokens, GitHub ≈ 0.8 K) |
| A typical answer (search, get) | Raw API JSON: URLs, ids, user objects, reactions | One line per hit, or Markdown with a short front matter |
| Changing several things on an item | Several tool calls, each answered with a full object | One call, answered with one confirmation line |
| Credentials | Often a hosted service or a token in a config file per client | Your own token, stored per user on your machine, used only from `localhost` |

## Why tokens matter here

An MCP client sends the **definition of every available tool** (name, description, JSON schema) to the model on **every request**, before you've typed anything. It then adds every tool **result** to the conversation, where it stays until the context is compacted. So two things drive the cost:

1. **Fixed cost per request**: the tool list. A large tool list is paid again on every turn of every conversation, whether or not a tool is used.
2. **Variable cost per call**: the result payloads. Raw REST APIs are designed for programs, not for models. Most of a response consists of links, internal ids and nested objects the model never needs, but it still reads (and pays for) every byte.

Fewer tokens also mean faster answers, less pressure on the context window (so less forgetting in long sessions), and fewer limit or rate errors on metered plans.

## What every module gets

These come from the core, so each new provider gets them for free:

* **Unconfigured modules cost zero tokens.** Their tools are hidden from `tools/list` until the module has valid settings, and they appear the moment you save the settings page (no restart).
* **Few, terse tools.** Each provider has about six tools with short descriptions and flat, primitive parameters. Related operations are merged where it makes sense, for example one `github_update_issue` for edit, close, labels and assignees.
* **Lean results.** DTOs map only the fields that are printed, and everything else is dropped while deserializing. Lists come back as one `id | title | …` line per item, and documents as Markdown with a small front matter.
* **Short errors.** Failures become one actionable sentence ("GitHub API 404: Not Found (missing, or the token has no access)"), not an HTML error page or stack trace.
* **Measurable.** The `devtools.mcp.tool.output.size` metric records how many characters each tool call returned (see [Measure it yourself](#measure-it-yourself)).

Some benefits have nothing to do with tokens: it runs locally with your own permissions, binds to loopback only, keeps tokens in a per-user settings file, and ships as a single ~24 MB native executable that starts in well under 100 ms.

## Confluence

### Fixed cost: the tool list

| Setup | Tools | Size | ≈ Tokens |
| --- | --- | --- | --- |
| devtools-mcp Confluence module | 6 | 2.3 KB | ~575 |

Measured the same way as for GitHub (compact name, description and input schema). Only the six page tools exist: search, get, children, create, update and append. There are no space-admin, permission, attachment-admin or user tools to pay for.

### Variable cost: what each call returns

Confluence's REST API is especially wasteful for a model, for two reasons:

* **The body format.** Pages are stored as *storage format* XHTML: every paragraph, table cell and list item is wrapped in tags, and macros are verbose `<ac:structured-macro>` / `<ac:parameter>` / `<ri:…>` blocks. devtools-mcp converts that to **Markdown**, which carries the same headings, lists, tables, links, code blocks and task lists in a fraction of the characters. Simple macros become one-line placeholders (`**[Confluence Macro: jira | key=ABC-1]**`) that still round-trip back into real macros on update.
* **The envelope.** A content response also carries `_links`, `_expandable`, version and history objects, space objects and (on Cloud) user metadata. devtools-mcp returns a three-line front matter (`id`, `title`, `version`) and the body.

| Call | Raw REST response | devtools-mcp result |
| --- | --- | --- |
| `confluence_search` | Search results with nested `content`, `space`, `_links`, `_expandable` and highlight markers per hit | One line per hit: `id \| title \| space \| date`, plus a cleaned excerpt of at most 160 characters |
| `confluence_get_page` | JSON envelope plus storage XHTML | `id` / `title` / `version` front matter plus Markdown |
| `confluence_get_page_children` | Full content objects per child | `id \| title` per child |
| `confluence_create_page` / `confluence_update_page` | The full new page, body included | `Created <id> \| <title> \| <url>` / `Updated <id> to version <n>.` |

### Fewer calls, and less to send

* `confluence_append_to_page` adds a section **without the model re-sending the page**. Without it, appending means reading the whole page and writing the whole page back: the page body passes through the context twice, and macros risk being lost in the process.
* `confluence_update_page` handles versions itself: it reads the current version number (metadata only, no body), writes `version + 1`, and retries once on a conflict. The model never has to read a page just to learn its version, and never sees a failed call followed by a retry.
* Writing in Markdown is also cheaper than writing storage XHTML: the model produces fewer output tokens, which are the most expensive and slowest kind.

> **Not yet measured:** byte counts against a live Confluence. The cloud container that wrote this document can't reach a public Confluence, and the measurement through the browser wasn't permitted. To fill in real numbers, run any `confluence_*` call through the Aspire AppHost and compare `devtools.http.client.response.body.size` (bytes from Confluence) with `devtools.mcp.tool.output.size` (characters returned), as described in [Measure it yourself](#measure-it-yourself).

## GitHub

### Fixed cost: the tool list

Measured as the compact JSON of each tool's name, description and input schema (what the client forwards to the model):

| Setup | Tools | Size | ≈ Tokens |
| --- | --- | --- | --- |
| devtools-mcp GitHub module | 6 | 3.2 KB | ~800 |
| Official GitHub MCP server, `issues` toolset only | 10 | 14.1 KB | ~3,500 |
| Official GitHub MCP server, default toolsets (context, repos, issues, pull requests, users) | 45 | 46.4 KB | ~11,600 |

The official server's numbers come from its published tool snapshots (`github-mcp-server` v1.14.0). Its single `issue_write` schema (3.6 KB) is larger than all six devtools-mcp GitHub tools together. With the default setup, that adds roughly 11 K tokens to **every** request, even in conversations that never touch GitHub.

### Variable cost: what each call returns

Measured against the live API on 2026-11-25, comparing the raw REST response with the devtools-mcp tool result:

| Call | Raw GitHub API | devtools-mcp | Smaller by |
| --- | --- | --- | --- |
| Search, 20 open issues (`dotnet/runtime`, label filter) | 183 KB | 3.6 KB | ~50× |
| One issue with its comments (`dapplo/SimpleMCP#1`, 6 KB body) | 17.4 KB | 6.9 KB | 2.5× (overhead 11.4 KB → 0.9 KB) |
| Repository labels (9 labels) | 2.9 KB | 0.4 KB | 7× |

Where the savings come from:

* Search hits are one line each: `#n | state | title | labels | assignees | updated`. Inside one repository, refs are printed as `#n` instead of the full name.
* An issue is a short front matter (state, author, labels, assignees, milestone, url) plus the Markdown body as written. GitHub's `*_url` templates, node ids, full user objects, reactions and author associations are never mapped.
* Only the **latest N comments** (default 20) are included, each with the id you need to edit it.
* `github_update_issue` changes title, body, state, milestone, labels and assignees in **one call** and answers with a single line, for example `Updated o/r#5: closed (not_planned); +labels wontfix.`

### What it deliberately leaves out

Basic issue work only: search, read, create, edit, close/reopen, comment, labels, assignees and milestones, all with your token's own permissions. There are no repository, organization, team, webhook or admin tools, so none of their schemas are paid for. Attachments are not supported, because GitHub has no public API for uploading issue attachments.

## Trade-offs

* **Narrower scope.** If you need pull request reviews, Actions, code search or project boards, the official servers do more. You can run both and enable only what you need in each.
* **Text is still text.** Issue bodies, comments and Confluence pages come back as written. A long discussion stays long, and the savings are in the overhead around it.
* **The comparison depends on configuration.** The official GitHub server can be slimmed down by enabling fewer toolsets, and some of its tools accept `minimal_output`. The gap shrinks with careful tuning, but it doesn't disappear: its issues toolset alone is about 4× the size of this module.

## Measure it yourself

* **Per tool call:** run the server through the Aspire AppHost (`aspire run --project src/DevToolsMcp.AppHost`) and look at the `devtools.mcp.tool.output.size` histogram (characters returned per tool) and `devtools.http.client.response.body.size` (bytes received from the downstream API) in the dashboard. The ratio between the two is the saving per call.
* **Tool list:** `curl -s -X POST http://localhost:5180/mcp -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | wc -c` shows what a client receives.
* **Rule of thumb:** for English text and JSON, 1 token ≈ 4 characters.
