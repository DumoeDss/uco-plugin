# CLAUDE.md

## What This Is

uco-plugin — the Unity Co-Pilot bridge: a Node REST/WebSocket server plus the
Unity Editor/Runtime plugin. Sub-projects: `uco-unity-project/` (the Unity
project hosting the plugin package), `uco-framework/` (framework source:
`Uco.Framework*` projects), `cli/`, `Installer/`, `Unity-Tests/`.

## Build / Run

- Plugin EditMode gates: `commands/run-unity-tests.ps1` (or direct batchmode
  `-runTests`) on **both** Unity 2022.3 and 6000.5.
- Framework DLLs: `commands/build-framework-dlls.ps1` (netstandard2.1, deploys
  into `uco-unity-project/Assets/Plugins/NuGet`).
- Version bump: `.\commands\bump-version.ps1 <version>` (verify its file list —
  the fork package lives at
  `uco-unity-project/Packages/com.atelierai.unity.copilot/package.json`).

## Known workflow traps

- Unity 6 writes template builtin-module deps back into
  `uco-unity-project/Packages/manifest.json` on every session; after a 6000.5
  gate, restore the dual-version manifest (test-framework 1.1.33) before
  running 2022.3 or committing.
- tools-manifest.json is generated via the menu item
  `ToolsManifestGenerator.GenerateManifest` (batchmode `-executeMethod`).

## Find Detail In

- `uco-unity-project/CLAUDE.md` — sub-project specifics
- `docs/claude/` — architecture, style, release notes (upstream-era docs;
  names may lag the Phase E rename)
