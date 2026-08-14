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
        public const string BuildSceneAddToolId = "build-scene-add";

        [McpPluginTool
        (
            BuildSceneAddToolId,
            Title = "Build / Add Scene",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Add a scene to `EditorBuildSettings.scenes` at the requested index " +
            "(default: append). If the scene is already present, its `enabled` flag is updated and the " +
            "row is moved to `insertAt` when supplied.")]
        [McpPluginSkillBody("Add or move a scene in `EditorBuildSettings.scenes`. " +
            "If the scene is not yet present it is inserted at `insertAt` (or appended when null). " +
            "If the scene is already present it is moved to `insertAt` (when supplied) and its `enabled` " +
            "flag is updated. The full updated scene list is returned.\n\n" +
            "## Inputs\n\n" +
            "- `scenePath` — project-relative `.unity` path; must resolve to an existing asset.\n" +
            "- `enabled` (default `true`) — whether the new/updated entry is enabled in the build.\n" +
            "- `insertAt` — optional zero-based index; clamped to `[0, scenes.Length]`. When null, the " +
            "scene is appended (or left in place for an existing scene).")]
        [Description("Add a scene to EditorBuildSettings.scenes. Returns the updated list.")]
        public BuildSceneEntry[] AddScene
        (
            [Description("Project-relative path to the .unity scene asset.")]
            string scenePath,
            [Description("Whether the scene should be enabled in the build. Default: true.")]
            bool enabled = true,
            [Description("Optional insertion index. When null, the scene is appended.")]
            int? insertAt = null
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(Error.ScenePathInvalid(scenePath ?? "(null)"), nameof(scenePath));

                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scenePath) == null)
                    throw new ArgumentException(Error.SceneNotFound(scenePath), nameof(scenePath));

                var guid = AssetDatabase.AssetPathToGUID(scenePath);
                var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

                // Locate any existing entry by path.
                var existingIdx = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].path, scenePath, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIdx = i;
                        break;
                    }
                }

                EditorBuildSettingsScene entry;
                if (existingIdx >= 0)
                {
                    entry = list[existingIdx];
                    entry.enabled = enabled;
                    list.RemoveAt(existingIdx);
                }
                else
                {
                    entry = new EditorBuildSettingsScene(scenePath, enabled);
                    if (!string.IsNullOrEmpty(guid) && GUID.TryParse(guid, out var parsedGuid))
                        entry.guid = parsedGuid;
                }

                var targetIdx = insertAt ?? (existingIdx >= 0 ? existingIdx : list.Count);
                if (targetIdx < 0) targetIdx = 0;
                if (targetIdx > list.Count) targetIdx = list.Count;
                list.Insert(targetIdx, entry);

                EditorBuildSettings.scenes = list.ToArray();
                return SnapshotScenes().ToArray();
            });
        }
    }
}
