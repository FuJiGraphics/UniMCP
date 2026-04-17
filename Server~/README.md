# unimcp — Python MCP server

Stateless MCP server. Relays tool calls from MCP clients (Claude Code, etc.) to a running Unity Editor that hosts the UniMCP HTTP bridge.

## Install (dev)

```bash
cd Server~
pip install -e .
```

## Run

```bash
unimcp
```

Configure MCP clients to launch this command via stdio.
