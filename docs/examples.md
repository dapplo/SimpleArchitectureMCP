# What you can do with it

Once your client is [connected](clients.md), you just ask in plain language. The model picks the tools; you never type tool names. The examples below show typical prompts and, in brackets, the tools the model usually calls, so you can see what happens and why it stays cheap.

Everything runs with **your own permissions** in Confluence, Jira and GitHub. There are no admin tools, so the model can't change projects, workflows, repository settings or permissions.

## Confluence

**Find and read**

* "Find our Confluence pages about the release process in space ENG and summarize the current steps." (`confluence_search`, `confluence_get_page`)
* "What changed on the 'Onboarding' page? Show me the page and list the sections." (`confluence_search`, `confluence_get_page`)
* "List the child pages of the 'Architecture' page and tell me which ones look outdated." (`confluence_get_page_children`)

**Write**

* "Create a page 'Retro 2026-11-25' under 'Team Retros' in space ENG with sections What went well, What didn't, Actions." (`confluence_create_page`)
* "Append today's decision to the 'Decision log' page: we move the nightly build to 02:00 UTC." (`confluence_append_to_page`: only the new snippet is sent, not the whole page)
* "Fix the typos in the 'Getting started' page and save it with the version message 'typos'." (`confluence_get_page`, `confluence_update_page`)

Pages come back as Markdown. Macros the server can't map appear as placeholders like `**[Confluence Macro: jira | key=ABC-1]**`. For pages full of complex macros, ask the model to "edit it in storage format", which makes it use `format=storage` both ways so nothing is lost.

## Jira

**Find and triage**

* "Show my open issues in project ABC, newest first." (`jira_search` with `assignee = currentUser() AND project = ABC AND resolution = Unresolved ORDER BY updated DESC`)
* "What is ABC-123 about, and what did people say in the last comments?" (`jira_get_issue`)
* "Which bugs in ABC were created this week and are still unassigned?" (`jira_search`)

**Create and update**

* "Create a bug in ABC: 'Login fails with SSO on Safari', priority High, label `sso`, assign it to me, with steps to reproduce." (`jira_create_issue`)
* "Create sub-tasks under ABC-200 for backend, frontend and docs." (`jira_create_issue` with `parent_key`)
* "Add the label `customer` to ABC-123 and assign it to Maria." (`jira_update_issue`: one call; ambiguous names return a short list to choose from)
* "Move ABC-123 to In Review and comment that the PR is up." (`jira_transition_issue` with `transition` and `comment`)
* "Close ABC-123 as Done." (`jira_transition_issue`; if your workflow uses another name, the error lists the valid transitions)
* "Attach `build/report.html` to ABC-123." (`jira_attach_file`; hidden folders and `AppData` are refused)

## GitHub issues

Set `GitHub:DefaultRepo` (or the *Default repository* field on the settings page) and you can leave the repository out of every prompt.

* "List the open bugs in `acme/web` labelled `needs-triage`." (`github_search_issues`)
* "Summarize issue #42 and its discussion." (`github_get_issue`, which also works for pull requests)
* "Open an issue: 'Settings page loses focus after save', label `bug`, assign me." (`github_list_labels` if unsure of the label names, then `github_create_issue`)
* "Close #42 as not planned and explain why in a comment." (`github_update_issue`, `github_comment`)
* "Add the `good first issue` label to #17, #18 and #21 and remove `needs-triage`." (`github_update_issue` once per issue: labels, assignees, state and title change in one call)
* "Fix the typo in my last comment on #42." (`github_get_issue` to find the comment id, then `github_comment` with `comment_id`)

## Workflows across tools

These combine providers in one conversation. They are where a local server pays off most: each step is a short line or a compact Markdown page instead of a full API object.

**Bug report to tracked work**
> "Read GitHub issue #87, create a matching Jira bug in ABC with the steps to reproduce and a link back, then comment the Jira key on #87."

`github_get_issue` → `jira_create_issue` → `github_comment`

**Release notes**
> "Find the Jira issues in ABC that were resolved since 2026-11-01 with fixVersion 2.4, group them by type, and append the list as 'Release 2.4' to the Confluence page 'Release notes'."

`jira_search` → `confluence_search` → `confluence_append_to_page`

**Meeting notes to tickets**
> "Read the Confluence page 'Planning 2026-11-25', create a Jira task in ABC for every action item with its owner as assignee, and add the new keys next to the items on the page."

`confluence_get_page` → `jira_create_issue` (several) → `confluence_update_page`

**Stand-up summary**
> "What did I work on yesterday? Check my Jira issues updated since yesterday and my GitHub issues in acme/web, and write a three-line stand-up."

`jira_search` + `github_search_issues`

**Spec check**
> "Compare the requirements on the Confluence page 'Export v2 spec' with the open Jira stories in epic ABC-300 and list what has no story yet."

`confluence_search` → `confluence_get_page` → `jira_search`

**Documentation from code work**
> "Summarize the discussion in GitHub issue #120 into a 'How it works' section and append it to the Confluence page 'Sync service'."

`github_get_issue` → `confluence_append_to_page`

## Keep it cheap

The server already trims every answer, but your prompts can help too:

* **Ask for what you need.** "Show the last 3 comments of ABC-123" makes the model pass `comments=3`; "just the status of #42" lets it skip comments entirely.
* **Limit searches.** "the 5 newest" is cheaper than "all".
* **Name the space, project or repository.** It narrows search results, and fewer hits mean fewer tokens.
* **Append instead of rewriting.** "Add to the page" uses `confluence_append_to_page`, which sends only the snippet.
* **Combine changes.** "Assign to me, add label X and set priority High" is one `jira_update_issue` or `github_update_issue` call.
* **Configure only the modules you use.** Unconfigured modules add no tool definitions to any request.

See [token-savings.md](token-savings.md) for measurements.
