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
using System.Linq;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.IvanMurzak.ReflectorNet.Utils;
using AIGD;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_GameObject
    {
        public const string GameObjectComponentAddToolId = "gameobject-component-add";
        [UcoTool
        (
            GameObjectComponentAddToolId,
            Title = "GameObject / Component / Add"
        )]
        [AuthoringCapability(
            MutationKind = AuthoringMutationKind.Add,
            UndoLevel = AuthoringUndoLevel.Full,
            SupportsValidation = true,
            SupportsPlanning = true,
            ValidatorType = typeof(UnityPilotAuthoringValidator),
            PlannerType = typeof(UnityPilotAuthoringPlanner),
            TransactionFactoryType = typeof(UnityAuthoringTransactionFactory))]
        [UcoSkillDescription("Add one or more Components to a GameObject in the opened Prefab or active Scene. " +
            "Component types are looked up by full name (with namespace) or by class-name fallback. " +
            "Use '" + GameObjectFindToolId + "' to locate the host GameObject and '" + ComponentListToolId +
            "' to discover valid component type names.")]
        [UcoSkillBody("Add Component to GameObject in opened Prefab or in a Scene. " +
            "Use '" + GameObjectFindToolId + "' tool to find the target GameObject first. " +
            "Use '" + ComponentListToolId + "' tool to find the component type names to add.\n\n" +
            "## Inputs\n\n" +
            "- `componentNames` — list of component type names. Each entry may be a fully-qualified type name (preferred) " +
            "or a bare class name (resolved via fallback to `AllComponentTypes`).\n" +
            "- `gameObjectRef` — the target GameObject. Required.\n\n" +
            "## Behavior\n\n" +
            "Per-name errors (unknown type, type not assignable to `UnityEngine.Component`, add-failed/duplicate) are " +
            "accumulated in `response.Errors` / `response.Warnings` instead of throwing, so a single bad name does not " +
            "abort the whole batch. Successful additions populate `response.AddedComponents` with `ComponentDataShallow` " +
            "snapshots.")]
        [Description("Add Component to GameObject in opened Prefab or in a Scene. " +
            "Use '" + GameObjectFindToolId + "' tool to find the target GameObject first. " +
            "Use '" + ComponentListToolId + "' tool to find the component type names to add.")]
        public AddComponentResponse AddComponent
        (
            [Description("Full name of the Component. It should include full namespace path and the class name.")]
            string[] componentNames,
            GameObjectRef gameObjectRef
        )
        {
            if (gameObjectRef == null)
                throw new ArgumentNullException(nameof(gameObjectRef), "No GameObject reference provided.");

            if (!gameObjectRef.IsValid(out var gameObjectValidationError))
                throw new ArgumentException(gameObjectValidationError, nameof(gameObjectRef));

            if (componentNames == null)
                throw new ArgumentNullException(nameof(componentNames), "No component names provided.");

            if (componentNames.Length == 0)
                throw new ArgumentException("No component names provided.", nameof(componentNames));

            return MainThread.Instance.Run(() =>
            {
                // g-005: fail closed before the first mutation unless the policy pipeline approved this call.
                UnityAuthoringUndo.RequireAuthoringScope();
                var go = gameObjectRef.FindGameObject(out var error);
                if (error != null)
                    throw new Exception(error);

                if (go == null)
                    throw new Exception("GameObject not found.");

                var response = new AddComponentResponse();
                var addedAny = false;

                foreach (var componentName in componentNames)
                {
                    var type = TypeUtils.GetType(componentName);
                    if (type == null)
                    {
                        // try to find component with exact class name without namespace
                        type = AllComponentTypes.FirstOrDefault(t => t.Name == componentName);
                        if (type == null)
                        {
                            response.Errors ??= new List<string>();
                            response.Errors.Add($"Type '{componentName}' not found.");
                            continue;
                        }
                    }

                    // Check if type is a subclass of UnityEngine.Component
                    if (!typeof(UnityEngine.Component).IsAssignableFrom(type))
                    {
                        response.Errors ??= new List<string>();
                        response.Errors.Add($"Type '{componentName}' is not a subclass of UnityEngine.Component.");
                        continue;
                    }

                    // Undo.AddComponent creates and registers the component
                    // inside the transaction group in one step.
                    var newComponent = UnityAuthoringUndo.AddComponent(go, type);
                    if (newComponent == null)
                    {
                        response.Warnings ??= new List<string>();
                        response.Warnings.Add($"Component '{componentName}' already exists on GameObject or cannot be added.");
                        continue;
                    }

                    addedAny = true;

                    response.Messages ??= new List<string>();
                    response.Messages.Add($"Added component '{componentName}'.");

                    response.AddedComponents.Add(new ComponentDataShallow(newComponent));
                }

                if (addedAny)
                    UnityEditor.EditorUtility.SetDirty(go);
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                return response;
            });
        }

    }
}
