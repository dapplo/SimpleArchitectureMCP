---
name: migrate-issues
description: Move issues between Jira and GitHub with migrate_issues safely. Use when asked to migrate, copy or move issues between Jira and GitHub.
metadata:
  requires: migrate_issues
---
# Migrating issues

`migrate_issues` copies on the server; never read issues yourself to recreate them.

1. Scope: agree on the source (issue keys, JQL, or GitHub search) and the target (`owner/repo` or a Jira project key).
2. Dry run: call with `dry_run: true` and `limit: 5`. Show the lines to the user. Check labels, and that people
   come out as mapped users rather than plain names (the user map lives on the settings page).
3. Small batch: run the same call without `dry_run`, `limit: 5`. Ask the user to open one created issue and check
   comments, attachments and status before continuing.
4. The rest: pass `cursor` from "next cursor" until there is none, at most 25 per call. Report progress in one line
   per batch, not per issue.
5. Errors: rerun the same call. Already copied issues are skipped, so a rerun resumes and never duplicates.

Don't edit the migration record file, and don't delete issues created by a run without asking.
