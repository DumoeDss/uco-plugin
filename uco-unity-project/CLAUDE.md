# CLAUDE.md

## What this is

uco-unity-project — the Unity host project for the Unity Co-Pilot plugin
package (`Packages/com.atelierai.unity.copilot`). It is the dev harness and
gate host: EditMode/PlayMode tests run here, framework DLLs deploy into
`Assets/Plugins/NuGet`, and tools-manifest generation runs against it.

## Build / run

- **Open**: `uco-unity-project` folder in Unity Editor (compiles automatically)
- **Tests**: Unity Test Runner (`Window > General > Test Runner`) — EditMode in `Packages/com.atelierai.unity.copilot/Tests/Editor`, PlayMode in `Packages/com.atelierai.unity.copilot/Tests/Runtime`

## Critical invariants

- Edits to `.cs` files cause bridge silence during recompile — read `Editor.log` directly from disk to recover compile errors.
- **No spaces in project path** — validated on startup; will warn user.
- **Unity 2022.3+** minimum. Dual-editor gates: 2022.3 and 6000.5 both must pass EditMode.
- All Unity API calls must use `MainThread.Instance.Run()`.
- After any 6000.5 session, restore the dual-version `Packages/manifest.json` (test-framework 1.1.33, no template-only deps) before running 2022.3 or committing — Unity 6 writes its template deps back every session.

## Find detail in

- `Packages/CLAUDE.md` — plugin package specifics
- `../../uco-plugin/CLAUDE.md` — repo-level workflows and traps
