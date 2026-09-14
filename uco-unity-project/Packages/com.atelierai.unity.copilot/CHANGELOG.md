# Changelog

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
