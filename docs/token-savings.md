# What devtools-mcp brings, and why

devtools-mcp is a small MCP server that runs on your own machine and connects an AI assistant to the developer tools you already use (Confluence, GitHub, …). It doesn't add features those services lack. Its purpose is to make each interaction **cheaper in tokens**, so you pay less, wait less and keep more of the context window for your actual work.

| | Without devtools-mcp | With devtools-mcp |
| --- | --- | --- |
| Tool definitions sent with every request | Every tool of every server you connect, used or not | Only tools of modules you configured: 6 per provider, ≈ 0.6–0.8 K tokens each |
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

* Pages come back as **Markdown** instead of storage XHTML, which is usually several times smaller and much easier for the model to read and write.
* `confluence_append_to_page` adds content **without the model re-sending the whole page**, and existing macros stay untouched.
* Version numbers are handled by the server (read, increment, retry once on conflict), so there are no failed calls followed by retries.
* All 6 tool schemas total about 2.5 KB (~630 tokens).

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
