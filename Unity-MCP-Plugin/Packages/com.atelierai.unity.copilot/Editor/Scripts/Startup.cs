/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using com.AtelierAI.Unity.Copilot.Editor.UI;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using UnityEditor;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace com.AtelierAI.Unity.Copilot.Editor
{
    [InitializeOnLoad]
    public static partial class Startup
    {
        static readonly ILogger _logger = UnityLoggerFactory.LoggerFactory.CreateLogger(nameof(Startup));

        static Startup()
        {
            UnityCopilotPluginEditor.Instance.BuildMcpPluginIfNeeded();
            UnityCopilotPluginEditor.Instance.AddUnityLogCollectorIfNeeded(() => new BufferedFileLogStorage());

            // Informational notice — historically the plugin treated a space in the project
            // path as a hard error, but in practice the modern startup pipeline (structured
            // ProcessStartInfo, JSON/TOML client configs with quoted command strings,
            // Path.Combine-based file IO) handles spaces correctly. We keep a one-shot
            // warning so users have a breadcrumb if they hit a third-party tool that does
            // not tolerate spaces in paths.
            if (Application.dataPath.Contains(" "))
                Debug.LogWarning("Unity Copilot: project path contains spaces. The plugin will start normally; if any specific workflow misbehaves with this path, please report it so we can fix the underlying issue.");

            SubscribeOnEditorEvents();

            // Initialize sub-systems
            API.Tool_Tests.Init();
            UpdateChecker.Init();
            PackageUtils.Init();
        }
    }
}
