# Changelog

## [1.0.2] - 2026-09-15 — one menu tree, one name: Unity Copilot

- The product is named **Unity Copilot** (no hyphen). All menus, window
  titles, the Project Settings page, the scene-view overlay button, and
  generated skill docs now read from the single `ProductInfo` source.
- The leftover "AI Game Developer" menu tree (NuGet resolver items) is
  merged into **Tools ▸ Unity Copilot**; the stray root-level
  "Generate Tools Manifest" item moves under it too.
- Removed the dead **Launch MCP Inspector** item and the legacy
  `Commands/` folder (`start_mcp_inspector.bat`, `copy_readme.bat`) — the
  bridge speaks plain REST, there is no MCP endpoint to inspect.
- Team update kill-switch asset renamed to
  `ProjectSettings/Copilot-UpdateSettings.asset` (the old
  AI-Game-Developer-named file is no longer read; the toggle resets to
  enabled once).

## [1.0.1] - 2026-09-15 — self-contained package (framework DLLs move inside)

The three locally-built framework DLLs (ReflectorNet.dll, Uco.Framework.dll,
Uco.Framework.Common.dll) now ship inside the package under `Plugins/`
instead of being staged into the project's `Assets/Plugins/NuGet/`. A bare
install from OpenUPM or a git URL now compiles and runs: the bundled
DependencyResolver still fetches the external NuGet set (all public
packages) on first import, and the framework trio travels with the package.

`uco install` (1.0.1+) removes project-level copies of the relocated trio
on upgrade — duplicates would collide with the embedded assemblies.
`build-framework-dlls` now deploys into the package.


## [1.0.0] - 2026-09-14 — first public release

Inaugural version under the `com.atelierai.unity.copilot` identity. The
0.7x line was the private fork era; the version counter restarts here with
the public repository.

### Fixed

- Node server auto-discovery now looks for the `uco` npm package (the
  package was renamed cocli -> uco in 0.76.0, but discovery still looked
  only for `node_modules/cocli`, which on machines with a pre-rename global
  install resolved to stale 0.2.x server code — or to nothing at all on
  clean machines). An explicit `nodeServerPath` in the plugin config still
  wins over auto-discovery.
