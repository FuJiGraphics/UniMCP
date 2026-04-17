# Getting Started

<!-- WIP. -->

## Roadmap

### Phase 1 — Foundation (read-only)

- Unity `HttpListener` bridge with domain-reload handling.
- Tool registry with `[McpTool]` attribute-based auto-discovery.
- Python MCP server stub forwarding tool calls over HTTP.
- Read-only tools:
  - `prefab.list(folder, filter)`
  - `prefab.inspect(path)`
  - `script.read(path)`
  - `convention.read()`
- `.mcp.json` + Claude Code skill template.

### Phase 2 — Writes (rename core)

- `editor.commit_checkpoint(message)` — auto git snapshot.
- `prefab.rename(path, newName)` — GUID-preserving rename with reference update.
- `script.rename_class(path, newName)` — file + class name atomic rename.
- `script.rename_field(...)` — field rename with automatic `[FormerlySerializedAs]` insertion to preserve inspector bindings.
- All write tools support `dryRun: true`.

### Phase 3 — Semantic boost

- `prefab.screenshot(path)` — preview render returned as base64 PNG for vision-capable clients.

### Phase 4 — UX polish

- Optional Editor window: drag-and-drop prefab selection, "Send to Claude" helper.
- Project Settings UI for port / auto-start toggle.

## Installation (planned)

Requirements: Unity 6 (6000.0) or newer, Python 3.10+.

1. Add this package to `Packages/manifest.json`:

```json
"com.unimcp.core": "https://github.com/FuJiGraphics/UniMCP.git"
```

2. In Unity, open `Project Settings → UniMCP` and pick a port (default `7849`).
3. Import the `Claude Code Skill Templates` sample from the Package Manager window.
4. Install the Python server: `pip install -e Packages/com.unimcp.core/Server~` (dev) or `uvx unimcp` once published.
5. Register the server in `.mcp.json` (see `Samples~/ClaudeCode/.mcp.json.template`).
