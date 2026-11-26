# What devtools-mcp brings, and why

devtools-mcp is a small MCP server that runs on your own machine and connects an AI assistant to the developer tools you already use: Confluence, Jira and GitHub. It doesn't add features those services lack. Its purpose is to make each interaction **cheaper in tokens**, so you pay less, wait less and keep more of the context window for your actual work.

| | Without devtools-mcp | With devtools-mcp |
| --- | --- | --- |
| Tool definitions sent with every request | Every tool of every server you connect, used or not | Only tools of modules you configured: 6–7 per provider (Confluence ≈ 0.6 K tokens, Jira ≈ 0.8 K, GitHub ≈ 0.8 K) |
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
* **Few, terse tools.** Each provider has six to eight tools with short descriptions and flat, primitive parameters. Related operations are merged where it makes sense, for example one `github_update_issue` for edit, close, labels and assignees.
* **Lean results.** DTOs map only the fields that are printed, and everything else is dropped while deserializing. Lists come back as one `id | title | …` line per item, and documents as Markdown with a small front matter.
* **Short errors.** Failures become one actionable sentence ("GitHub API 404: Not Found (missing, or the token has no access)"), not an HTML error page or stack trace.
* **Measurable.** The `devtools.mcp.tool.output.size` metric records how many characters each tool call returned (see [Measure it yourself](#measure-it-yourself)).

Some benefits have nothing to do with tokens: it runs locally with your own permissions, binds to loopback only, keeps tokens in a per-user settings file, and ships as a single ~28 MB native executable (linux-x64) that answers `/health` about 0.1 s after launch.

## Confluence

### Fixed cost: the tool list

| Setup | Tools | Size | ≈ Tokens |
| --- | --- | --- | --- |
| devtools-mcp Confluence module | 8 | 3.4 KB | ~840 |

Measured the same way as for GitHub (compact name, description and input schema). The eight tools are search, get, export (to a local file), children, create, update, append and add draw.io diagram (listed only when the draw.io app is installed). There are no space-admin, permission, attachment-admin or user tools to pay for.

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

## Jira

### Fixed cost: the tool list

| Setup | Tools | Size | ≈ Tokens |
| --- | --- | --- | --- |
| devtools-mcp Jira module | 7 | 3.2 KB | ~800 |

This is measured the same way as GitHub: compact name, description and input schema. The seven tools are search, get, create, update (which also covers assignee and labels), transition (including close), comment and attach. There are no project, workflow, scheme, permission or user-admin tools to pay for. We haven't compared against the schemas of other Jira MCP servers.

### Variable cost: what each call returns

Measured against the public Apache Jira (Jira Data Center 10.3, `issues.apache.org`) on 2026-11-25. The table compares the raw REST v2 response with the devtools-mcp tool result:

| Call | Raw Jira API | devtools-mcp | Smaller by |
| --- | --- | --- | --- |
| Search, 20 issues (`project = KAFKA ORDER BY updated DESC`) | 234 KB (default fields); 32.6 KB even with `fields=` limited to the five printed fields | 2.4 KB | ~97× (13× against the filtered JSON) |
| One issue with comments (`KAFKA-10645`, 10 comments, 2.3 KB description) | 32.8 KB (131 fields, 92 of them custom fields; all 10 comments) | 6.0 KB (front matter, Markdown description, latest 5 comments) | ~5.4× |
| One issue that is mostly text (`KAFKA-17000`, a 21 KB stack trace, no comments) | 32.0 KB | ≈ 21 KB | ~1.5× |

Where the savings come from:

* **Only the fields that are printed are requested.** A default Jira issue response carries every field of the project's screens, mostly custom fields (`customfield_10010: null`, …), plus `self` links, avatar URL maps, `watches`, `votes`, `worklog`, `timetracking` and the full comment list. devtools-mcp asks for 13 fields when it gets an issue and 5 when it searches.
* **Search hits are one line each:** `key | type | status | assignee | updated | summary`, plus a `(20 of 20382; refine the JQL)` hint when there are more.
* **Comments are capped.** `jira_get_issue` includes the latest N comments (default 5, `comments=0` for none). The raw API always returns all of them, and on KAFKA-10645 they alone are 18.7 KB.
* **Markdown instead of Jira markup or ADF.** Descriptions and comments come back as Markdown (`{code:java}` becomes a fenced block, `[~user]` becomes `@user`, and `{color}` noise is removed), and the model writes Markdown back. Both Cloud and Data Center are read through REST API v2, so on Cloud the model never sees Atlassian Document Format JSON, which is several times larger than the text it describes.
* **One call where Jira needs several.** `jira_update_issue` edits fields, adds or removes labels and (re)assigns in one call, and answers with one line (`Updated OPS-7: summary, labels, assignee → Jane Doe.`). `jira_transition_issue` finds the transition by name or target status, so closing an issue takes one call instead of "list transitions, pick an id, post". A wrong name returns the short list of valid transitions instead of an error to retry from.
* **Users by name.** The assignee can be given as `me`, a username, an email or a display name. The server resolves it to an `accountId` (Cloud) or `name` (Data Center), so the model never has to search for users and copy account ids.

The third row shows the limit: when an issue is mostly text, that text is the cost. devtools-mcp only removes the envelope around it.

### Reproduce

The numbers come from anonymous REST v2 calls against `https://issues.apache.org/jira/rest/api/2/` (`search?jql=…&maxResults=20`, `issue/KAFKA-10645`, `issue/KAFKA-17000`). The devtools-mcp sizes come from feeding those responses through `JiraService`. Against your own Jira, compare `devtools.http.client.response.body.size` with `devtools.mcp.tool.output.size` in the Aspire dashboard.

## GitHub

### Fixed cost: the tool list

Measured as the compact JSON of each tool's name, description and input schema (what the client forwards to the model):

| Setup | Tools | Size | ≈ Tokens |
| --- | --- | --- | --- |
| devtools-mcp GitHub module | 6 | 3.3 KB | ~810 |
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

## Migration

`migrate_issues` (about 1 KB, ~250 tokens of schema, listed only when Jira and GitHub are both configured) moves issues between the two without the text passing through the model. Doing the same with the per-item tools means reading each issue with all its comments and then writing every description and comment again as model output: roughly the issue's size in input tokens plus the same again in (slower, pricier) output tokens, plus one call per comment. For a typical 6 KB issue with comments that is ~3,000 tokens and several calls; with `migrate_issues` it is one ~25-token line in a batch of up to 25 issues per call. The model cannot reword the text along the way either.

## Compared with no tool at all

> **Not measured.** This is a reasoned estimate. The cost depends on how much the agent has to experiment, so real numbers would need agent sessions run both ways.

An agent with only a shell can call the REST APIs itself with `curl` (or `gh` for GitHub). The tool list then costs nothing extra, because the shell tool is already loaded. Each task costs more, though:

* **The agent writes every call itself:** authentication headers, the right endpoint, query escaping, and usually a `jq` filter to shrink the JSON. These are output tokens, the slowest and most expensive kind, and they are paid again in every new conversation.
* **First attempts tend to read raw responses** (183 KB for a 20-hit GitHub search, 234 KB for a Jira one, see above) before the agent learns which fields to filter.
* **Mistakes cost whole round trips:** a wrong endpoint or header, pagination, Jira transition ids, Confluence version numbers and conflicts.
* **Some jobs are hard to do well from a shell at all:** converting Confluence storage XHTML to Markdown and back without losing macros, appending to a page without sending it twice, or resolving a Jira user to an account id.
* **The token has to be reachable from the shell,** often as plain text in an environment variable or in the transcript.

The gap is smallest for simple GitHub reads with the `gh` CLI, whose `gh issue list` and `gh issue view` output is already compact. It is largest for Confluence, which has no comparable CLI.

## Trade-offs

* **Narrower scope.** If you need pull request reviews, Actions, code search or project boards, the official servers do more. You can run both and enable only what you need in each.
* **Text is still text.** Issue bodies, comments and Confluence pages come back as written. A long discussion stays long, and the savings are in the overhead around it.
* **The comparison depends on configuration.** The official GitHub server can be slimmed down by enabling fewer toolsets, and some of its tools accept `minimal_output`. The gap shrinks with careful tuning, but it doesn't disappear: its issues toolset alone is about 4× the size of this module.

## Measure it yourself

* **Per tool call:** run the server through the Aspire AppHost (`aspire run --project src/DevToolsMcp.AppHost`) and look at the `devtools.mcp.tool.output.size` histogram (characters returned per tool) and `devtools.http.client.response.body.size` (bytes received from the downstream API) in the dashboard. The ratio between the two is the saving per call.
* **Tool list:** `curl -s -X POST http://localhost:5180/mcp -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | wc -c` shows what a client receives.
* **Rule of thumb:** for English text and JSON, 1 token ≈ 4 characters.
