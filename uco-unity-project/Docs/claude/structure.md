# Package Structure

```
Packages/com.atelierai.unity.copilot/
├── Editor/
│   ├── UnityCopilotPluginEditor.cs        # Editor singleton (+ .Static, .Build, .Config)
│   ├── Scripts/
│   │   ├── API/                           # Tool/prompt/resource implementations (partial classes, 1 op per file)
│   │   ├── CopilotServerManager.cs        # Node bridge discovery + lifecycle
│   │   ├── UI/                            # Windows, overlays, menu items
│   │   └── Utils/                         # Config, migration, helpers
│   ├── DependencyResolver/                # NuGet bootstrapper for external DLLs
│   └── UI/ (uxml/uss)                     # Layout assets
├── Runtime/
│   ├── UnityCopilotPluginRuntime.cs       # Runtime singleton (+ .Static.cs)
│   ├── UnityCopilotPluginBuilder.cs
│   └── Utils/ (EnvironmentUtils, ...)
├── Plugins/                               # Bundled DLLs (Uco.Framework, Uco.Framework.Common, ReflectorNet)
└── Tests/
```

- **UnityCopilotPluginEditor** (4 partials: `.cs`, `.Static.cs`, `.Build.cs`, `.Config.cs`) — Editor-only singleton managing the bridge connection, config persistence (JSON file I/O), and lazy assembly scanning via `UcoPluginBuilder`
- **UnityCopilotPluginRuntime** (2 partials: `.cs`, `.Static.cs`) — Runtime singleton with `Initialize(Action<IUcoPluginBuilder>?)` API for game builds; no JSON config dependency
- **Plugins/** DLLs are built by `commands/build-framework-dlls.ps1` from `uco-framework/`
