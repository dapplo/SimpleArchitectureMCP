---
name: archi-to-confluence
description: Publish ArchiMate views to a Confluence page as editable draw.io diagrams. Use when asked to put an Archi model or view on Confluence.
metadata:
  requires: archi_to_drawio confluence_add_drawio
---
# ArchiMate views on Confluence

1. Convert: `archi_to_drawio` with the model file (`.archimate` or exchange `.xml`) and the `views` to publish.
   It returns the `.drawio` path and its page names; nothing needs opening in Archi.
2. Page: find the target page (`confluence_search`), or create one with a sentence on what the views show.
3. Attach: `confluence_add_drawio` with the page id and the `.drawio` path. Give a stable `name`, since re-running
   with the same name updates the diagram in place instead of adding a second one.
4. Updating later: repeat steps 1 and 3 with the same `name`. Don't delete and recreate the attachment.
