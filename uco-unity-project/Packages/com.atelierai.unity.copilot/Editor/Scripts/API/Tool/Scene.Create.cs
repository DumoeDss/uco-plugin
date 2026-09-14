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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using AIGD;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Scene
    {
        public const string SceneCreateToolId = "scene-create";
        [UcoTool
        (
            SceneCreateToolId,
            Title = "Scene / Create"
        )]
        // NewScene + SaveScene writes a new asset file and replaces or adds an
        // open scene; neither is restored by an Undo group, so the capability
        // truthfully reports Undo=none and therefore requires a confirmed plan.
        [AuthoringCapability(
            MutationKind = AuthoringMutationKind.Create,
            UndoLevel = AuthoringUndoLevel.None,
            SupportsValidation = true,
            SupportsPlanning = true,
            ValidatorType = typeof(UnityPilotAuthoringValidator),
            PlannerType = typeof(UnityPilotAuthoringPlanner))]
        [AuthoringPathBinding(
            "path",
            Intent = AuthoringPathAccessIntent.Create,
            RootCategory = ProjectPathRootCategories.Assets,
            Required = true)]
        [UcoSkillDescription("Create a new Unity scene asset and save it at the given `.unity` path. " +
            "Use '" + SceneListOpenedToolId + "' to inspect the resulting opened-scene set afterwards.")]
        [UcoSkillBody("Create new scene in the project assets. " +
            "Use '" + SceneListOpenedToolId + "' tool to list all opened scenes after creation.\n\n" +
            "## Inputs\n\n" +
            "- `path` — must end with `.unity`. Non-empty.\n" +
            "- `newSceneSetup` (default `DefaultGameObjects`) — Unity's `NewSceneSetup` flag (`EmptyScene` or `DefaultGameObjects`).\n" +
            "- `newSceneMode` (default `Single`) — `Single` closes other scenes, `Additive` keeps them open.\n\n" +
            "## Behavior\n\n" +
            "Calls `EditorSceneManager.NewScene` + `SaveScene(path)` on the main thread, repaints editor windows, " +
            "and returns a `SceneDataShallow` for the newly created scene.")]
        [Description("Create new scene in the project assets. " +
            "Use '" + SceneListOpenedToolId + "' tool to list all opened scenes after creation.")]
        public SceneDataShallow Create
        (
            [Description("Path to the scene file. Should end with \".unity\" extension.")]
            string path,
            UnityEditor.SceneManagement.NewSceneSetup? newSceneSetup = UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
            UnityEditor.SceneManagement.NewSceneMode? newSceneMode = UnityEditor.SceneManagement.NewSceneMode.Single
        )
        {
            return MainThread.Instance.Run(() =>
            {
                // g-005: fail closed before the first mutation unless the policy pipeline approved this call.
                UnityAuthoringUndo.RequireAuthoringScope();
                if (string.IsNullOrEmpty(path))
                    throw new System.Exception(Error.ScenePathIsEmpty());

                if (!path.EndsWith(".unity"))
                    throw new System.Exception(Error.FilePathMustEndsWithUnity());

                // Create a new empty scene
                var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    newSceneSetup ?? UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                    newSceneMode ?? UnityEditor.SceneManagement.NewSceneMode.Single);

                // Save the scene asset at the specified path
                bool saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, path);
                if (!saved)
                    throw new System.Exception($"Failed to save scene at '{path}'.\n{OpenedScenesText}");

                // Scene creation is an external asset write (Undo=none); no
                // transaction is open, so this only marks an ambient batch
                // parent when one exists.
                UnityAuthoringUndo.MarkMutated();

                EditorUtils.RepaintAllEditorWindows();

                return scene.ToSceneDataShallow();
            });
        }
    }
}
