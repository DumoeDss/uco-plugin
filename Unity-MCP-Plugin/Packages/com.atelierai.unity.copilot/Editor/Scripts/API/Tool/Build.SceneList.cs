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
using System.Linq;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildSceneListToolId = "build-scene-list";

        [UcoTool
        (
            BuildSceneListToolId,
            Title = "Build / List Scenes",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("List scenes currently registered in `EditorBuildSettings.scenes`, " +
            "preserving build-order. Each entry reports path, enabled flag, and asset GUID.")]
        [UcoSkillBody("Return the current `EditorBuildSettings.scenes` array as a list of " +
            "`BuildSceneEntry` records. The order matches Unity's build settings list (scene 0 is the " +
            "first scene loaded by a built player). Empty `Guid` indicates the asset could not be resolved " +
            "(e.g. the scene file was deleted but the entry was not removed). " +
            "Set `includeDisabled=false` to list only scenes that participate in the build.")]
        [Description("List scenes registered in EditorBuildSettings, preserving build order. " +
            "Set includeDisabled=false to omit scenes disabled in the build settings.")]
        public BuildSceneEntry[] ListScenes(
            [Description("Include scenes disabled in EditorBuildSettings (default true). " +
                "Set false to list only scenes that will be packed into the player.")]
            bool includeDisabled = true)
        {
            return MainThread.Instance.Run(() =>
            {
                var scenes = SnapshotScenes();
                return includeDisabled ? scenes.ToArray() : scenes.Where(scene => scene.Enabled).ToArray();
            });
        }
    }
}
