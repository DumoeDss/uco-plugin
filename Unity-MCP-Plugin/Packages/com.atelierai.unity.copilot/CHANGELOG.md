# Changelog

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
