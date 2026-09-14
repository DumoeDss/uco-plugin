# Startup Flow

Triggered by `[InitializeOnLoad]` static constructor in `Startup.cs`:

1. Build `IMcpPlugin` instance (scan assemblies for tools/prompts/resources)
2. Add `BufferedFileLogStorage` log collector for early log capture
3. **Deferred** connection via `EditorApplication.delayCall` (prevents Unity freeze on async WebSocket connect)
4. Queue local server auto-start check (Node.js cocli server — see transport.md; skipped in CI)
5. **Deferred** server auto-start via `EditorApplication.delayCall`
6. Validate project path (no spaces)
7. Subscribe to editor lifecycle events (domain reload, play mode transitions)
8. CI environment detection — skips connection and server start in CI
