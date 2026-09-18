# Tool, Prompt, and Resource Registration

Attribute-based registration for the three primitives the bridge exposes. All use `[System.ComponentModel.Description]` for AI-readable documentation.

- **Tools**: `[UcoPluginToolType]` on class, `[UcoPluginTool(Name = "category-action")]` on methods
- **Prompts**: `[UcoPluginPromptType]` on class, `[UcoPluginPrompt]` on methods
- **Resources**: `[UcoPluginResourceType]` on class, `[UcoPluginResource]` on methods (e.g., `gameobject://currentScene/{path}`)

## Testing Patterns

- Extend `BaseTest` class — provides `[UnitySetUp]` (initializes singleton, creates logger) and `[UnityTearDown]` (destroys all GameObjects)
- `BaseTest.RunTool(string toolName, string json)` helper — executes a tool and asserts success
- Use `[UnityTest]` with `IEnumerator` return type; call `yield return base.SetUp()` / `base.TearDown()`
- Some tests use standard NUnit `[Test]`/`[SetUp]`/`[TearDown]` when Unity APIs aren't needed

## Error Handling

- Structured error responses for AI consumption; graceful non-blocking cleanup on disconnect/quit
- SIGTERM for Unix (falls back to `Kill()`), immediate `Kill()` on Windows
- Log all exceptions (never silently swallowed)

## Configuration

- **No spaces in project path** — validated on startup with user warning
- **Unity 2022.3+** minimum
- Main UI: `Tools > Unity Copilot`
- Config file: `UserSettings/uco-config.json` (auto-created; legacy `AI-Game-Developer-Config.json` is read as a fallback and migrated)
