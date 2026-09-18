# Changelog

## [1.0.4] - 2026-09-17 — embedded-install update fix

The in-editor updater no longer attempts (and fails with an opaque UPM
error) to update embedded packages installed by uco install. It now
detects the embedded layout and directs the user to run
`uco install <project>` instead.

## [1.0.3] - 2026-09-17 — Cloud mode removed; bridge finds the published npm package

- **Cloud connection mode deleted.** The upstream remnant connected to a dead
  third-party endpoint (ai-game.dev), silently prevented the local bridge from
  auto-starting (its gate only allowed Custom), and confused every session that
  hit a config without an explicit mode. ConnectionMode now has a single value
  (Custom), the default is Custom, the device-code auth flow and its UI are
  gone, and old configs carrying "Cloud" are mapped to Custom on load.
- **Node bridge discovery follows the published package.** npm rejected the
  bare name "uco" (typosquatting policy), so the package publishes as
  @atelierai/uco — but discovery still looked for node_modules/uco, which
  stopped existing the moment the pre-scope global install was removed.
  Discovery now probes @atelierai/uco, then the legacy uco and cocli layouts,
  project-local and npm-global.
- Token env overrides route straight to the local token (no mode routing);
  the cloud URL variable is gone; --url and the host variable keep working.

## [1.0.2] - 2026-09-15 — one menu tree, one name: Unity Copilot

- The product is named **Unity Copilot** (no hyphen). All menus, window
  titles, the Project Settings page, the scene-view overlay button, and
  generated skill docs now read from the single `ProductInfo` source.
- The leftover "AI Game Developer" menu tree (NuGet resolver items) is
  merged into **Tools ▸ Unity Copilot**; the stray root-level
  "Generate Tools Manifest" item moves under it too.
- Removed the dead **legacy inspector** menu item and the legacy
  `Commands/` folder (`start_legacy_inspector.bat`, `copy_readme.bat`) — the
  bridge speaks plain REST, with no endpoint of that kind to inspect.
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
