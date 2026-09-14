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
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildSceneListToolId = "build-scene-list";

        [McpPluginTool
        (
            BuildSceneListToolId,
            Title = "Build / List Scenes",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("List every scene currently registered in `EditorBuildSettings.scenes`, " +
            "preserving build-order. Each entry reports path, enabled flag, and asset GUID.")]
        [McpPluginSkillBody("Return the current `EditorBuildSettings.scenes` array as a list of " +
            "`BuildSceneEntry` records. The order matches Unity's build settings list (scene 0 is the " +
            "first scene loaded by a built player). Empty `Guid` indicates the asset could not be resolved " +
            "(e.g. the scene file was deleted but the entry was not removed).")]
        [Description("List scenes registered in EditorBuildSettings, preserving build order.")]
        public BuildSceneEntry[] ListScenes()
        {
            return MainThread.Instance.Run(() => SnapshotScenes().ToArray());
        }
    }
}
