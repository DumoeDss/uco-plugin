/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 */

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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildSceneRemoveToolId = "build-scene-remove";

        [McpPluginTool
        (
            BuildSceneRemoveToolId,
            Title = "Build / Remove Scene",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Remove a scene from `EditorBuildSettings.scenes` by path. " +
            "No-op (and returns the unchanged list) when the scene is not registered.")]
        [McpPluginSkillBody("Remove a scene from `EditorBuildSettings.scenes`. " +
            "Matches on path (case-insensitive). When the scene is not present in the current list the " +
            "tool returns the unchanged snapshot — it does not throw — so callers can treat the call as " +
            "idempotent.\n\n" +
            "## Inputs\n\n" +
            "- `scenePath` — project-relative `.unity` path to remove.")]
        [Description("Remove a scene from EditorBuildSettings.scenes by path. Returns the updated list.")]
        public BuildSceneEntry[] RemoveScene
        (
            [Description("Project-relative path of the scene to remove.")]
            string scenePath
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(scenePath))
                    throw new ArgumentException(Error.ScenePathInvalid(scenePath ?? "(null)"), nameof(scenePath));

                var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
                var removed = false;
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i].path, scenePath, StringComparison.OrdinalIgnoreCase))
                    {
                        list.RemoveAt(i);
                        removed = true;
                    }
                }

                if (removed)
                    EditorBuildSettings.scenes = list.ToArray();

                return SnapshotScenes().ToArray();
            });
        }
    }
}
