---
name: confluence-writing
description: Conventions for creating and editing Confluence pages in Markdown with the confluence_* tools. Use when writing or updating a Confluence page.
metadata:
  requires: confluence_create_page confluence_update_page
---
# Writing Confluence pages

- Find the right place first: `confluence_search` for an existing page on the topic (update it rather than create a
  duplicate), and pass `parent_id` when creating so the page lands in the tree.
- Write Markdown. Supported: headings, lists, `- [ ]` task lists, tables, block quotes, links, code fences
  (```` ```java title="Example" ```` sets code macro parameters).
- Macros: `**[Confluence Macro: name | key=value]**` on its own line. Macros with a body: a block quote starting with
  that line, e.g. `> **[Confluence Macro: info]**` then the text. Use `info`, `note`, `warning`, `toc`.
- Start with one sentence on what the page is for; keep headings at level 2 and 3 (the title is level 1).
- Adding to a page: `confluence_append_to_page`, which keeps existing content and macros without resending them.
- Rewriting: `confluence_get_page`, then `confluence_update_page` with a short `version_message`. If the page shows
  macros you can't reproduce, read and write it with `format=storage` instead, changing only what you need.
- Diagrams: attach them with `confluence_add_drawio`; don't paste images as text.
