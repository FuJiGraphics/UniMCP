# UniMCP

Unity Editor MCP (Model Context Protocol) SDK.

Exposes Unity Editor capabilities (prefab inspection, asset renaming, serialized field manipulation, preview rendering, etc.) as MCP tools so MCP clients like Claude Code can drive Unity Editor workflows with AI assistance.

## Status

Work in progress. See roadmap in `Documentation~/getting-started.md`.

## Architecture

```
MCP client (e.g. Claude Code)
        │  stdio MCP
        ▼
Python MCP server  (Server~/)
        │  HTTP  localhost:<port>
        ▼
Unity Editor bridge  (Editor/)
        │
        ▼
AssetDatabase / PrefabUtility / SerializedObject
```

- `Editor/` — Unity Editor C# bridge. HttpListener + tool registry. Domain-reload aware.
- `Server~/` — Python MCP server. Stateless relay to the Unity bridge.
- `Samples~/ClaudeCode/` — `.mcp.json` and skill templates for Claude Code.

## Requirements

- Unity 2022.3 LTS or newer
- Python 3.10+

## Installation

*(WIP — filled in as features land)*

## License

MIT. See `LICENSE`.
