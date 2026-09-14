<div align="center">
  <h1>Unity Co-Pilot</h1>
  <p>Drive the Unity Editor from any AI agent over plain REST + WebSocket — no MCP protocol on the wire.</p>
  <p><code>com.atelierai.unity.copilot</code></p>
</div>

Unity Co-Pilot is a Unity Editor/Runtime plugin that exposes the Editor as an
HTTP service: 160+ tools covering scenes, GameObjects, components, assets,
prefabs, scripts, packages, screenshots, console, tests, and builds — plus
prompts and resources. A self-hosted Node bridge connects the Editor to the
outside world; the **uco** CLI drives the whole surface from any agent or
terminal.

## Highlights

- **160+ Editor tools** — authoring, code (including Roslyn compile-and-execute
  with confirmation gating for risky operations), diagnostics, build & tests,
  visuals, physics
- **Three-surface AI skills** — `uco setup-skills` generates focused skill
  bundles (setup / lifecycle / live-editor) for Claude Code, Codex, Cursor,
  and other agents, refreshed from the live tool catalog
- **Deterministic, safe connectivity** — one port per project (hashed from the
  project path), bearer-token auth, localhost by default, no vendor lock-in:
  anything that can speak HTTP or run a CLI can drive the Editor
- **Durable calls** — async invocations return a call id queryable later
  (`uco call get <id>`); structured error envelopes survive the whole stack
- **Dual-editor support** — gates run against Unity 2022.3 LTS and 6000.5

## Requirements

- Unity **2022.3 or newer** (tested on 2022.3 LTS and 6000.5)
- **Node.js 20+** for the bridge server (the plugin auto-discovers the `uco`
  npm package — global or project-local — or set `nodeServerPath` in the
  config to point at a specific install)

## Install

### Via the uco CLI (recommended)

```bash
npm install -g @atelierai/uco
uco install <path-to-your-unity-project>
```

This embeds the plugin package, stages the framework DLLs, writes the initial
config, and can generate agent skills and wrappers (`uco setup-skills`).

### Via OpenUPM

Add the scoped registry to `Packages/manifest.json` and reference the package:

```json
{
  "dependencies": {
    "com.atelierai.unity.copilot": "1.0.0"
  },
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": [ "com.atelierai.unity.copilot" ]
    }
  ]
}
```

## Quick start

```bash
uco ping                        # health-check the bridge
uco call unity-tool-list        # list every tool the Editor exposes
uco exec --method-body --return-type int --code 'return GameObject.FindObjectsOfType<Camera>().Length;'
```

Configuration lives in `UserSettings/uco-config.json` (migrated automatically
from the legacy filename by older→newer versions of the plugin).

## Wire protocol

Plain HTTP + WebSocket with JSON envelopes (`/api/tools/...`). There is no MCP
protocol on the wire — the bridge was deliberately stripped down to REST so
any agent, script, or CI job can use it without an MCP SDK.

## Attribution & license

Derived from [IvanMurzak/Unity-MCP](https://github.com/IvanMurzak/Unity-MCP)
(Apache-2.0). Licensed under the Apache License 2.0 — see [LICENSE](LICENSE)
for the dual copyright notice.
