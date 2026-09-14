/*
┌──────────────────────────────────────────────────────────────────┐
│  Adapted from MCP for Unity (https://github.com/CoplayDev/unity-mcp)
│  Copyright (c) 2025 Coplay Inc.                                  │
│  Licensed under the MIT License.                                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [McpPluginToolType]
    public partial class Tool_Profiler
    {
        public static class Error
        {
            public static string ParameterRequired(string name)
                => $"Parameter '{name}' is required.";

            public static string UnknownProfilerArea(string name, string validList)
                => $"Unknown ProfilerArea '{name}'. Valid: {validList}.";

            public static string UnknownProfilerCategory(string name, string validList)
                => $"Unknown ProfilerCategory '{name}'. Valid: {validList}.";

            public static string ObjectNotFound(string objectPath)
                => $"Object not found at path '{objectPath}'. Try a scene hierarchy path (e.g. '/Player/Mesh') or an asset path (e.g. 'Assets/Textures/hero.png').";

            public static string LogDirectoryNotFound(string dir)
                => $"Log file directory does not exist: '{dir}'.";

            public static string SnapshotFileNotFound(string path)
                => $"Snapshot file not found: '{path}'.";

            public const string MemoryProfilerPackageMissing =
                "Install 'com.unity.memoryprofiler' to use memory snapshots. " +
                "Use the 'package-add' tool with packageId 'com.unity.memoryprofiler'.";

            public const string FrameDebuggerUtilityUnavailable =
                "UnityEditorInternal.FrameDebuggerUtility was not found via reflection. The Unity version may have moved/renamed it.";

            public const string FrameTimingFeatureDisabled =
                "Frame Timing Stats is not enabled. Enable it in Project Settings > Player > Other Settings > 'Frame Timing Stats', or use a Development Build (always enabled).";
        }

        /// <summary>
        /// Best-effort guard for memory-profiler package presence.
        /// Looks for the editor-side type that ships with com.unity.memoryprofiler.
        /// </summary>
        internal static bool IsMemoryProfilerPackageAvailable()
        {
            // Newer entry-point (used by 0.7+ of com.unity.memoryprofiler).
            var t = Type.GetType("Unity.MemoryProfiler.MemoryProfiler, Unity.MemoryProfiler.Editor", throwOnError: false);
            if (t != null) return true;

            // Older entry-point name still present in some 0.4–0.6 versions.
            t = Type.GetType("Unity.MemoryProfiler.Editor.MemoryProfilerWindow, Unity.MemoryProfiler.Editor", throwOnError: false);
            return t != null;
        }
    }
}
