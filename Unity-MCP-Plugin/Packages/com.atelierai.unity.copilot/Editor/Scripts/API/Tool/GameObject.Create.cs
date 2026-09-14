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
using System.ComponentModel;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using AIGD;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_GameObject
    {
        public const string GameObjectCreateToolId = "gameobject-create";
        [UcoTool
        (
            GameObjectCreateToolId,
            Title = "GameObject / Create"
        )]
        [AuthoringCapability(
            MutationKind = AuthoringMutationKind.Create,
            UndoLevel = AuthoringUndoLevel.Full,
            SupportsValidation = true,
            SupportsPlanning = true,
            ValidatorType = typeof(UnityPilotAuthoringValidator),
            PlannerType = typeof(UnityPilotAuthoringPlanner),
            TransactionFactoryType = typeof(UnityAuthoringTransactionFactory))]
        [UcoSkillDescription("Create a new GameObject in the currently opened Prefab or active Scene, optionally " +
            "parented under another GameObject and pre-positioned. Pass `primitiveType` to spawn a Unity primitive " +
            "(Cube, Sphere, etc.) instead of an empty GameObject.")]
        [UcoSkillBody("Create a new GameObject in opened Prefab or in a Scene. " +
            "If needed - provide proper 'position', 'rotation' and 'scale' to reduce amount of operations.\n\n" +
            "## Inputs\n\n" +
            "- `name` — required non-empty name.\n" +
            "- `parentGameObjectRef` (optional) — when provided, the new GameObject is parented under this one " +
            "(`SetParent(parent, worldPositionStays: false)`); otherwise it's created at scene/prefab root.\n" +
            "- `position` / `rotation` / `scale` — optional transform; default to zero / zero / one.\n" +
            "- `isLocalSpace` — when `true`, applies the transform in local space relative to the parent.\n" +
            "- `primitiveType` (optional) — when set, the GameObject is created via `GameObject.CreatePrimitive` " +
            "(adds the appropriate renderer/collider for the primitive shape).")]
        [Description("Create a new GameObject in opened Prefab or in a Scene. " +
            "If needed - provide proper 'position', 'rotation' and 'scale' to reduce amount of operations.")]
        public GameObjectRef Create
        (
            [Description("Name of the new GameObject.")]
            string name,
            [Description("Parent GameObject reference. If not provided, the GameObject will be created at the root of the scene or prefab.")]
            GameObjectRef? parentGameObjectRef = null,
            [Description("Transform position of the GameObject.")]
            Vector3? position = null,
            [Description("Transform rotation of the GameObject. Euler angles in degrees.")]
            Vector3? rotation = null,
            [Description("Transform scale of the GameObject.")]
            Vector3? scale = null,
            [Description("World or Local space of transform.")]
            bool isLocalSpace = false,
            PrimitiveType? primitiveType = null
        )
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Name cannot be null or empty.", nameof(name));

            return MainThread.Instance.Run(() =>
            {
                // g-005: fail closed before the first mutation unless the policy pipeline approved this call.
                UnityAuthoringUndo.RequireAuthoringScope();
                var parentGo = default(GameObject);
                if (parentGameObjectRef?.IsValid(out _) == true)
                {
                    parentGo = parentGameObjectRef.FindGameObject(out var error);
                    if (error != null)
                        throw new ArgumentException(error, nameof(parentGameObjectRef));
                }

                position ??= Vector3.zero;
                rotation ??= Vector3.zero;
                scale ??= Vector3.one;

                var go = primitiveType != null
                    ? GameObject.CreatePrimitive(primitiveType.Value)
                    : new GameObject(name);

                UnityAuthoringUndo.RecordCreated(go);
                // Register the initial object snapshot before applying the
                // caller's name/parent/transform mutations.  Both records
                // belong to the one transaction group.
                UnityAuthoringUndo.RecordModified(go, completeSnapshot: true);
                go.name = name;

                // Set parent if provided
                if (parentGo != null)
                    go.transform.SetParent(parentGo.transform, false);

                // Set the transform properties
                go.SetTransform(
                    position: position,
                    rotation: rotation,
                    scale: scale,
                    isLocalSpace: isLocalSpace);

                UnityAuthoringUndo.MarkMutated();

                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new GameObjectRef(go);
            });
        }
    }
}
