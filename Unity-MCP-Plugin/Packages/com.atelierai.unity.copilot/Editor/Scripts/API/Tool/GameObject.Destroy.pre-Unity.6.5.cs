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
#if !UNITY_6000_5_OR_NEWER
using System;
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using AIGD;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_GameObject
    {
        public const string GameObjectDestroyToolId = "gameobject-destroy";
        [McpPluginTool
        (
            GameObjectDestroyToolId,
            Title = "GameObject / Destroy",
            DestructiveHint = true
        )]
        [AuthoringCapability(
            MutationKind = AuthoringMutationKind.Delete,
            UndoLevel = AuthoringUndoLevel.Full,
            SupportsValidation = true,
            SupportsPlanning = true,
            ValidatorType = typeof(UnityPilotAuthoringValidator),
            PlannerType = typeof(UnityPilotAuthoringPlanner),
            TransactionFactoryType = typeof(UnityAuthoringTransactionFactory))]
        [McpPluginSkillDescription(DestroySkill.Description)]
        [McpPluginSkillBody(DestroySkill.Body)]
        [Description("Destroy GameObject and all nested GameObjects recursively in opened Prefab or in a Scene. " +
            "Use '" + GameObjectFindToolId + "' tool to find the target GameObject first.")]
        public DestroyGameObjectResult Destroy(GameObjectRef gameObjectRef)
        {
            if (gameObjectRef == null)
                throw new ArgumentNullException(nameof(gameObjectRef), "No GameObject reference provided.");

            if (!gameObjectRef.IsValid(out var gameObjectValidationError))
                throw new ArgumentException(gameObjectValidationError, nameof(gameObjectRef));

            return MainThread.Instance.Run(() =>
            {
                // g-005: fail closed before the first mutation unless the policy pipeline approved this call.
                UnityAuthoringUndo.RequireAuthoringScope();
                var logger = UnityLoggerFactory.LoggerFactory.CreateLogger<Tool_GameObject>();

                var go = gameObjectRef.FindGameObject(out var error);
                if (error != null)
                    throw new Exception(error);

                var destroyedName = go!.name;
                var destroyedPath = go.GetPath();
                var destroyedInstanceId = go.GetInstanceID();

                logger.LogInformation("Destroying GameObject '{Name}' (InstanceID: {InstanceId}) at path '{Path}'",
                    destroyedName, destroyedInstanceId, destroyedPath);

                UnityAuthoringUndo.Destroy(go);

                logger.LogInformation("Successfully destroyed GameObject '{Name}' (InstanceID: {InstanceId})",
                    destroyedName, destroyedInstanceId);

                EditorUtils.RepaintAllEditorWindows();

                return new DestroyGameObjectResult
                {
                    DestroyedName = destroyedName,
                    DestroyedPath = destroyedPath,
                    DestroyedInstanceId = destroyedInstanceId
                };
            });
        }

    }
}
#endif
