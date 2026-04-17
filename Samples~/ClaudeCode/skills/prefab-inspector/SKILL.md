---
name: prefab-inspector
description: Inspect Unity prefabs against naming and inspector-binding conventions via UniMCP, and propose or apply renames.
---

# Prefab Inspector

<!-- WIP — workflow to be finalized as Phase 1/2 tools land. -->

## Inputs

- A list of prefab asset paths (or a folder).
- Project conventions loaded from `.claude/skills/prefab-inspector/conventions.md`.

## Workflow (planned)

1. Read `conventions.md` to learn the project's rules.
2. For each target prefab, call `prefab.inspect` to gather structure (root script, component tree, serialized fields).
3. Optionally call `prefab.screenshot` for semantic judgment (button labels, popup layout, etc.).
4. Compare against conventions. Produce a diff of proposed changes (prefab name, script/class name, field names).
5. Present the diff to the user and request approval.
6. On approval, call `editor.commit_checkpoint` (git snapshot), then apply changes via `prefab.rename`, `script.rename_class`, `script.rename_field` (with `[FormerlySerializedAs]` auto-insertion).

## Safety

- Never apply writes without a commit checkpoint.
- Never run writes while Unity is in Play Mode.
- Support `dryRun: true` on every write tool.
