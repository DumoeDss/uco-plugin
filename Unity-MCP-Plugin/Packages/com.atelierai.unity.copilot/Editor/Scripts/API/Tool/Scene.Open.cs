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
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using AIGD;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Scene
    {
        public const string SceneOpenToolId = "scene-open";
        [McpPluginTool
        (
            SceneOpenToolId,
            Title = "Scene / Open"
        )]
        [AuthoringCapability(
            MutationKind = AuthoringMutationKind.Modify,
            UndoLevel = AuthoringUndoLevel.None,
            SupportsValidation = true,
            SupportsPlanning = true,
            ValidatorType = typeof(UnityPilotAuthoringValidator),
            PlannerType = typeof(UnityPilotAuthoringPlanner))]
        [McpPluginSkillDescription("Open a Unity scene asset in Single or Additive mode. " +
            "Returns the post-open list of all opened scenes. " +
            "Use '" + Tool_Assets.AssetsFindToolId + "' to locate the scene asset first.")]
        [McpPluginSkillBody("Open scene from the project asset file. " +
            "Use '" + Tool_Assets.AssetsFindToolId + "' tool to find the scene asset first.\n\n" +
            "## Inputs\n\n" +
            "- `sceneRef` — `AssetObjectRef` pointing at a `SceneAsset`. Throws if the asset cannot be resolved or " +
            "is not a `SceneAsset`.\n" +
            "- `loadSceneMode` (default `Single`):\n" +
            "  - `Single` — closes the currently opened scenes and opens this one.\n" +
            "  - `Additive` — keeps the currently opened scenes and opens this one alongside them.")]
        [Description("Open scene from the project asset file. " +
            "Use '" + Tool_Assets.AssetsFindToolId + "' tool to find the scene asset first.")]
        public SceneDataShallow[] Open
        (
            AssetObjectRef sceneRef,
            [Description("Open scene mode. " +
                "Single: closes the current scenes and opens a new one. " +
                "Additive: keeps the current scene and opens additional one.")]
            UnityEditor.SceneManagement.OpenSceneMode loadSceneMode = UnityEditor.SceneManagement.OpenSceneMode.Single
        )
        {
            return MainThread.Instance.Run(() =>
            {
                // g-005: fail closed before the first mutation unless the policy pipeline approved this call.
                UnityAuthoringUndo.RequireAuthoringScope();
                var sceneAsset = sceneRef.FindAssetObject<UnityEditor.SceneAsset>()
                    ?? throw new System.ArgumentException($"Requested scene is not valid or not found.");

                var scenePath = sceneAsset.GetAssetPath();

                var sceneOpened = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(scenePath, loadSceneMode);

                if (!sceneOpened.IsValid())
                    throw new System.Exception($"Failed to load scene at '{scenePath}'.\n{OpenedScenesText}");

                EditorUtils.RepaintAllEditorWindows();

                return OpenedScenes
                    .Select(scene => scene.ToSceneDataShallow())
                    .ToArray();
            });
        }
    }
}
