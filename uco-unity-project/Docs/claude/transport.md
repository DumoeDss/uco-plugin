# Transport

- **Server**: the Node.js bridge (npm package `@atelierai/uco`, entry `bin/server.mjs`). No binary is staged or downloaded. Entry discovery order: `nodeServerPath` in `UserSettings/uco-config.json` → `<project>/node_modules/@atelierai/uco` (then legacy `uco`/`cocli` layouts) → npm global install. Launched as `node bin/server.mjs --port <N> --plugin-timeout-ms <T> --authorization none|required [--token <T>]`. When the port is already listening (externally started server), no second instance is launched — the plugin just connects.
- **Plugin side**: raw WebSocket to `/hub/plugin` with JSON envelopes (see the framework's `Consts.Rpc`).
- **Agent/CLI side**: plain REST under `/api/*` — no JSON-RPC, no SDK.
