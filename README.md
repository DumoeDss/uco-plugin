# uco-plugin

Unity Co-Pilot — the Unity side of the **uco** bridge: a UPM plugin package
that exposes the Editor as a plain REST + WebSocket service (a single simple wire protocol
on the wire), plus the framework sources it is built from.

**Public package:** `com.atelierai.unity.copilot` — install via the
[uco CLI](https://www.npmjs.com/package/uco)
(`npm i -g @atelierai/uco && uco install <path-to-project>`) or via OpenUPM. Package
docs live [in the package](uco-unity-project/Packages/com.atelierai.unity.copilot/README.md).

## Repository layout

| Path | What it is |
|---|---|
| `uco-unity-project/` | Unity host project — dev harness and the EditMode/PlayMode gate host |
| `uco-unity-project/Packages/com.atelierai.unity.copilot/` | **the published plugin package** |
| `uco-framework/` | `Uco.Framework` / `Uco.Framework.Common` sources — compiled into the static DLLs the package consumes |
| `ReflectorNet/` | reflection/serialization library sources (DLL build input) |
| `commands/` | dev scripts — gates, framework DLL build, version bump |

## Development

- **Dual-editor gates** (both must pass): EditMode test runs against Unity
  2022.3 LTS **and** 6000.5 — e.g.
  `commands/run-unity-tests.ps1 -UnityPath <Unity.exe> -TestMode editmode`.
- **Framework DLLs**: `commands/build-framework-dlls.ps1` — rebuilds
  ReflectorNet + `Uco.Framework*` and deploys into
  `uco-unity-project/Assets/Plugins/NuGet` (GUID-stable .meta files).
- **Version bump**: `commands/bump-version.ps1 <version>` — stamps the
  package and its runtime constant in lockstep.
- Known trap: Unity 6 writes template dependencies back into
  `uco-unity-project/Packages/manifest.json` on every session — restore the
  dual-version form (test-framework 1.1.33, no Unity-6-only modules) before
  running 2022.3 or committing.

## Attribution & license

Derived from [IvanMurzak/Unity-MCP](https://github.com/IvanMurzak/Unity-MCP)
(Apache-2.0). Licensed under the Apache License 2.0 — see the
[package LICENSE](uco-unity-project/Packages/com.atelierai.unity.copilot/LICENSE)
for the dual copyright notice.
