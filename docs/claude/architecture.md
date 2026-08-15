# System Architecture

```
MCP Client (Claude/Cursor/etc.)
      ↕ streamableHttp (REST + WebSocket)
Unity-MCP-Server  (Node.js — npm package `cocli`, entry `bin/server.mjs`)
      ↕ WebSocket hub
Unity-MCP-Plugin  (Unity Editor/Runtime)
      ↕ Unity API (main thread)
Unity Engine
```

- The **MCP Server** is a Node.js application launched directly by the plugin with the `node` runtime — no binary is downloaded or staged into `Library/`. Entry discovery: `nodeServerPath` config → `<project>/node_modules/cocli/bin/server.mjs` → npm global install.
- The **MCP Plugin** auto-starts the server on Unity Editor load (`[InitializeOnLoad]`); if the port is already listening (external server), it connects instead of launching. Port is deterministic: SHA256 hash of project path, mapped to 20000–29999.
- Communication inside Unity always runs on the **main thread** via `MainThread.Instance.Run()`.
