# Connection & Transport

- **Port**: Deterministic — SHA256 of project path, mapped to 20000–29999
- **Server**: The Node.js MCP server (npm package `cocli`, entry `bin/server.mjs`). No binary is staged or downloaded anymore. Entry discovery order: `nodeServerPath` in `UserSettings/AI-Game-Developer-Config.json` → `<project>/node_modules/cocli/bin/server.mjs` → npm global install. Launched as `node bin/server.mjs --port <N> --plugin-timeout-ms <T> --authorization none|required [--token <T>]`. When the port is already listening (externally started server), no second instance is launched — the plugin just connects.
- **Process Lifecycle** (`CopilotServerStatus`): `Stopped` → `Starting` → `Running` → `Stopping` → `Stopped`, plus `External`. PID persisted in EditorPrefs for domain reload resilience.
- **Domain Reload**: Disconnects before reload (only if `Connected`), rebuilds and reconnects after. Play mode transitions trigger delayed reconnection.
