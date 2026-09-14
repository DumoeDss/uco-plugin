# Changelog

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
