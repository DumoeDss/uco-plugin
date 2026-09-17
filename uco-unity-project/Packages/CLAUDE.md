# CLAUDE.md

## What This Is

Unity Copilot plugin — the Unity Editor/Runtime side of the uco bridge
(plain REST + WebSocket; no MCP protocol on the wire). Attribute-based
framework that registers and executes tools, prompts, and resources, with a
self-hosted Node bridge manager and auto-configuration for AI clients.

## Build / run

- **Open**: `uco-unity-project` folder in Unity Editor (compiles automatically)
- **Tests**: Unity Test Runner (`Window > General > Test Runner`) — EditMode in `Packages/com.atelierai.unity.copilot/Tests/Editor`, PlayMode in `Packages/com.atelierai.unity.copilot/Tests/Runtime`

## Critical invariants

- Edits to `.cs` files cause bridge silence during recompile — read `Editor.log` directly from disk to recover compile errors.
- **No spaces in project path** — validated on startup; will warn user.
- **Unity 2022.3+** minimum. Dual-editor gates: 2022.3 and 6000.5 both must pass EditMode.
