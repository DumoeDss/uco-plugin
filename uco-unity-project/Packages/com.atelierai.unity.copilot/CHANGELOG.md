# Changelog

## [1.0.0] - 2026-09-14 — first public release

Inaugural version under the `com.atelierai.unity.copilot` identity. The
0.7x line was the private fork era; the version counter restarts here with
the public repository.

### Fixed

- Node server auto-discovery now probes the `uco` npm package first (the
  package was renamed cocli -> uco in 0.76.0, but discovery still looked
  only for `node_modules/cocli`, which on machines with a pre-rename global
  install resolved to stale 0.2.x server code — or to nothing at all on
  clean machines). The deprecated `cocli` package name remains a
  last-resort fallback; an explicit `nodeServerPath` in the plugin config
  still wins over both.

## [0.76.0] - 2026-09-14 — Phase E rename (uco)

Ships together with the renamed CLI package **uco 0.3.0** (formerly cocli).

### Changed (rename)

- Framework DLLs renamed: `McpPlugin.dll` -> `Uco.Framework.dll`,
  `McpPlugin.Common.dll` -> `Uco.Framework.Common.dll` (GUIDs preserved via
  renamed .meta files; asmdef precompiledReferences and link.xml updated;
  `uco install` removes the legacy-named DLLs from installed projects).
- Framework namespaces renamed:
  `com.IvanMurzak.McpPlugin` -> `com.AtelierAI.Uco.Framework`,
  `com.IvanMurzak.McpPlugin.Common` -> `com.AtelierAI.Uco.Framework.Common`.
- Tool/skill attributes renamed: `[McpPluginTool]` -> `[UcoTool]`,
  `[McpPluginToolType]` -> `[UcoToolType]`, `[McpPluginSkillDescription]` ->
  `[UcoSkillDescription]`, `[McpPluginSkillBody]` -> `[UcoSkillBody]`, plus
  the prompt/resource/argument attribute family and the manager/builder
  surface (`McpToolManager` -> `UcoToolManager`, `McpPluginBuilder` ->
  `UcoBuilder`, `IMcpPluginBuilder` retained).
- Config file renamed: `UserSettings/AI-Game-Developer-Config.json` ->
  `UserSettings/uco-config.json` with a one-shot migration on first Editor
  start (the legacy file is copied to the new name and removed).
- Plugin branding: displayName "Unity Co-Pilot", author AtelierAI, README
  header de-branded (upstream attribution retained), LICENSE dual-copyright
  (Ivan Murzak + AtelierAI fork line), Editor UI strings "AI Game Developer"
  -> "Unity Co-Pilot". `instance-get-current` env fields unchanged.
- Version constants and package metadata bumped to 0.76.0.

### Compatibility

- Wire protocol, tool names, package id (`com.atelierai.unity.copilot`),
  asmdef names, and `com.AtelierAI.Unity.Copilot.*` namespaces are unchanged.
- Old config filename is read as a fallback by both the plugin and the CLI
  until migration; installed projects should upgrade via `uco install`
  (removes the old framework DLLs).

## [0.75.1] - 2026-09-14

Sibling-cleanup release: clears the five pre-existing EditMode failures
inherited with the accumulated in-tree work, and fixes a project-manifest
regression shipped in 0.75.0.

### Fixed

- **Zero-parameter tools (ToolParameterTests)**: the seven MCP tools that
  exposed no input parameters — `build-scene-list`, `instance-get-current`,
  `tools-list-groups`, `graphics-lightbake-cancel`, `graphics-lightbake-clear`,
  `graphics-lightbake-status`, `graphics-rendering-stats` — each gained a
  meaningful optional parameter (defaults preserve the previous behavior, so
  existing zero-arg calls are unaffected): `includeDisabled`,
  `includeEnvironment` (populates new `IsBatchMode`/`IsCi` diagnostic fields on
  `UnityInstanceEntry`), `includeTools`, `failIfNotRunning`,
  `cancelRunningBake`, `includeDiskSize`, `includeMemoryStats`. Some MCP
  clients (e.g. GitHub Copilot) break on zero-parameter tools.
- **Tokenizer test indexes (CSharpTokenizerTests)**: the two failing tests
  targeted the first `'a'` in the source (inside the `char`/`var` keyword)
  instead of the literal body; the tokenizer itself was correct.
- **ApplyEdits test fixtures (ScriptApplyEditsTests)**: the simple-replacement
  test miscounted the column of the `'1'` (16 instead of 19); the
  multi-edit fixture replaced identifiers with bare tokens (`AAA` to `ZZ`)
  that the tool's Roslyn syntax validation correctly rejected. Both fixtures
  now encode their intended scenarios in valid C#.
- **Project manifest regression from 0.75.0**: the 0.75.0 snapshot
  accidentally shipped a Unity 6000.5-template `Packages/manifest.json` whose
  builtin-module dependencies (`com.unity.modules.accessibility`,
  `adaptiveperformance`, `physicscore2d`, `vectorgraphics`,
  `com.unity.multiplayer.center`, `test-framework 1.7.0`) do not exist in
  Unity 2022.3, breaking 2022.3 resolution once the package cache was
  rebuilt. Restored the dual-version-compatible manifest
  (`test-framework 1.1.33`), matching the pre-snapshot state.

## [0.75.0] - 2026-09-14

Feedback-response release for the DSAnimStudioUnity production usage round
(2026-09-10 to 09-14); ships together with cocli 0.2.0.

### Added

- **Test failure notifications (COCli-12)**: every terminal test-operation
  failure path (compilation failed, `tests-no-match`, start failures, resume
  failures, execution timeout after the cancellation grace) now delivers the
  failure to the deferred caller via `NotifyToolRequestCompleted` — the
  originating request no longer stays `processing` forever while the
  operation record alone goes terminal.
- **`script-execute` preprocessor symbols (COCli-13)**: a `defines` parameter
  (up to 16 identifiers, sanitized and de-duplicated) feeds the Roslyn parse
  options. The Editor assemblies' symbols are not inherited by default —
  `[Conditional("ENABLE_PROFILER")]`-style APIs are no longer silently
  compiled out when the symbol is passed explicitly. The skill body now
  documents this and the JsonUtility dynamic-assembly limitation (composite
  fields of script-defined types; prefer System.Text.Json).
- **Build queue diagnostics (COCli-13)**: `BuildJobInfo` reports
  `QueuedSeconds` (age while queued/scheduled) and `Blocked` (the scheduler's
  current cause, e.g. `capacity-admission`), so a job stuck before execution
  is distinguishable from a running build.
- **Durable build handle (COCli-11)**: `BuildJobInfo` implements
  `IDurableOperationHandle` and exposes `OperationId` (same value as
  `JobId`), so `--wait` and generic operation polling work for
  `build-player`; the immediate response is `processing` with an operation
  status instead of a bare success.

### Changed

- **Test summary scope (COCli-12)**: `TotalTests` reports the filtered
  execution scope. The fallback prefers the discovery-matched count, then the
  `RunStarted` filtered count, then the executed count — counting the whole
  Unity result tree happens only for runs that bypassed discovery entirely
  (previously a stale persisted match count could report the whole-project
  discovery total, e.g. "total=1319 while executed=487").
- **Batchmode connection gate (COCli-13)**: batchmode launches (offline
  builds, command-line automation) skip the bridge connection retry loop
  unless `keepServerRunning` is configured, keeping connection warnings out
  of BuildReport output. `EnvironmentUtils.IsBatchMode()` is public for
  tests.
- **Bounded failure diagnostics (COCli-09)**: the tool-execution pipeline and
  tool manager put a bounded, single-line cause (base-exception type +
  message) into the structured error message and the bounded
  type/message/stack trio into `details`; legacy callers get a bounded
  message instead of the full exception dump. The generic
  "Tool execution failed." placeholder never shadows the tool's own failure
  text.
- Version bumped to 0.75.0 (`package.json`, `UnityCopilotPlugin.Version`);
  the vendored copy shipped inside the cocli 0.2.0 bundle includes the Unity
  6000.5 compatibility hotfix (`EditorSceneSandbox` split with
  `UNITY_6000_5_OR_NEWER`, `pre-Unity.6.5` twin) that the vendored 0.74.0
  copy lacked.

## [Unreleased]

### Added

- **Bridge identity handshake (`bridge-identity-v1`, COCli-01)**: the version
  handshake now advertises the Editor's `projectPath`, `editorPid`,
  `unityVersion`, and `instanceId` under a `bridge-identity-v1` capability, so
  the Node server can fail constrained calls closed (`identity_mismatch` /
  `identity_unavailable`) instead of routing them to the wrong Editor. The
  identity is supplied through `McpPluginBuilder.WithHandshakeIdentity(...)`
  and is absent (legacy shape) when no host identity is registered.
- **Test-operation provenance (COCli-03)**: durable operations expose a
  `SourceRevision` compile epoch (`domainGeneration` + per-session compilation
  counter, captured at run start), explicit `StartedAtUtc` alongside
  `CreatedAtUtc`/`CompletedAtUtc`, an `Execution: "fresh"` guarantee (every run
  executes its own runner execution; no result reuse exists), and a structured
  `Blocked` cause (`compilation`, `previous-run-settling`,
  `capacity-admission`, `cancellation-pending`) with `RetryAfterMs` — blocked
  is never a failure.
- **Scene hygiene (COCli-06)**: `script-execute` and `tests-run` accept
  `sandboxScene` to run in a disposable untitled scene whose restoration
  (setup, active scene, selection, dirty state) is reported as
  `SandboxRestored` with a cause on failure. Both tools report
  `Mutated`/`MutatedScenes` computed from observed scene state. The existing
  dirty-user-scene blocker still applies in sandbox mode. **Wire-visible**:
  `script-execute` results are now a `ScriptExecuteResult` wrapper
  (`Value`, `Mutated`, `MutatedScenes`, `SandboxUsed`, `SandboxRestored`,
  `SandboxRestoreCause`) instead of a bare serialized value.
- **Console diagnostics (COCli-07)**: log entries carry `Source`
  (`product`/`bridge`/`tool`/`unity`) and, when emitted on the main thread
  inside an owned execution window, `CorrelationId`/`OperationId`.
  `console-get-logs` accepts `correlationId`, `operationId`, `source`, and
  `sinceUnixMs` filters and reports `DroppedEntries`/`TruncatedEntries`
  (existing filter names unchanged). Plugin/bridge/tool logger diagnostics now
  default to the collector-only channel — the Unity Editor Console stays
  reserved for product and Unity output; mirroring is opt-in via the
  `UnityCopilot.MirrorDiagnosticsToConsole` Editor pref. **Wire-visible**:
  `console-get-logs` returns
  `{ Entries, DroppedEntries, TruncatedEntries }` instead of a bare array.
- **Controlled tool-failure diagnostics (COCli-04)**: a tool exception on a
  controlled call keeps the bounded exception type, message, and stack in
  `error.details` instead of dropping them server-side (script-execute Roslyn
  diagnostics and `TargetInvocationException` details included).

### Fixed

- **`EntityId` wire format moved from JSON number to JSON string of decimal digits**
  (Unity 6.5+ paths only). Closes #759, resolves #754. JS-based MCP clients
  (Claude Agent SDK, etc.) parse JSON numbers as IEEE-754 doubles, so any
  `EntityId` raw ulong past `2^53 - 1` was rounded on the JS side and the
  rounded value sent back to Unity could not resolve the original object.
  Serialising the value as an opaque JSON string preserves full 64-bit
  precision across any language boundary. Inbound still accepts both forms
  (string preferred, number accepted for back-compat); outbound is always a
  string. Schema is now `{ "type": "string", "pattern": "^[0-9]+$" }`. Affects
  every Unity 6.5+ converter that writes `EntityId` or an `instanceID` field:
  `EntityIdConverter`, `ObjectRefConverter`, `GameObjectRefConverter`,
  `AssetObjectRefConverter`, `ComponentRefConverter`, `SceneRefConverter`,
  and the `Tool_Assets.Modify` `instanceID` injection. Pre-Unity-6.5 paths
  (legacy `int InstanceID`, safely inside the JS-safe range) are untouched.

### Changed (BREAKING)

- **Namespace flattened to `AIGD`**: AI-facing data model namespace renamed from
  `com.IvanMurzak.Unity.MCP.Runtime.Data` to `AIGD` across all data types
  (`GameObjectRef`, `ComponentRef`, `AssetObjectRef`, `SceneRef`, `ObjectRef`,
  `*Data`, `*DataShallow`, `*Metadata`, `PathPatch`, etc.). Closes #676.
  Replace `using com.IvanMurzak.Unity.MCP.Runtime.Data;` with `using AIGD;`.
  This supersedes reverted PR #701 (which used an intermediate `Unity.MCP.Data` name).
- **Nested-data-model convention REVERSED**: Data classes that were previously
  declared as nested types inside MCP tool `partial` classes (e.g.,
  `Tool_GameObject.DestroyGameObjectResult`, `Tool_Assets.CopyAssetsResponse`,
  `Tool_Tool.InputData/ResultData`) have been EXTRACTED into top-level types under
  `Editor/Scripts/API/Tool/Data/` in the `AIGD` namespace. `Tool_Tool.InputData`
  was renamed to `AIGD.ToolToggleInput`; `Tool_Tool.ResultData` became `AIGD.ToolToggleResult`.
  All other extracted types preserved their original names. New AI-facing data models MUST
  be top-level types in `AIGD` (see constitution Principle IV).
- `unity-skill-create` tool guidance updated to instruct AI agents to declare data
  models as top-level types in `AIGD`, not nested inside the tool class.
- Constitution bumped 1.4.0 → 1.5.0 documenting the rule reversal and the new
  `AIGD` namespace exception in Principle IV.

## [0.17.1] - 2025-01-XX

### Fixed

- **Play Mode Reconnection**: Fixed Unity-MCP-Plugin not reconnecting after exiting Play mode. The plugin now automatically re-establishes connection when returning to Edit mode if "Keep Connected" is enabled.
- Added proper handling for Unity's Play mode state changes (`EditorApplication.playModeStateChanged`)
- Enhanced logging for connection lifecycle debugging

### Added

- Comprehensive test coverage for Play mode reconnection scenarios
- Debug logging for Play mode transitions to help troubleshooting connection issues

## [0.1.0] - 2025-04-01

### Added

- Initial release of the Unity package.
