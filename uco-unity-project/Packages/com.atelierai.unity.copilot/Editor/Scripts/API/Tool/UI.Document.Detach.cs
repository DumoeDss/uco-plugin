/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp), Copyright (c) Coplay Inc., MIT License.
 * https://github.com/CoplayDev/unity-mcp/blob/main/MCPForUnity/Editor/Tools/ManageUI.cs
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
using AIGD;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using UnityEditor;
using UnityEngine.UIElements;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string DocumentDetachToolId = "ui-document-detach";

        [UcoTool
        (
            DocumentDetachToolId,
            Title = "UI / Document / Detach",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Remove the `UnityEngine.UIElements.UIDocument` component from the target " +
            "GameObject. Returns `Ok=false` when the GameObject has no UIDocument. " +
            "Pair with '" + DocumentAttachToolId + "' to add it back.")]
        [UcoSkillBody("Detach (remove) a UIDocument component from a GameObject.\n\n" +
            "## Inputs\n\n" +
            "- `target` — host GameObject. Required.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Uses `Undo.DestroyObjectImmediate` so the operation is reversible from " +
            "the editor and visible in the Undo stack. Returns `Ok=false` when the GameObject is unresolved or " +
            "carries no UIDocument.")]
        [Description("Detach a UIDocument from a GameObject.")]
        public DocumentAttachResult DetachDocument
        (
            [Description("Target GameObject. Use 'gameobject-find' to locate it.")]
            GameObjectRef target
        )
        {
            if (target == null)
                return new DocumentAttachResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (!target.IsValid(out var refErr))
                return new DocumentAttachResult { Ok = false, Error = refErr };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                {
                    return new DocumentAttachResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found."
                    };
                }

                var doc = go.GetComponent<UIDocument>();
                if (doc == null)
                {
                    return new DocumentAttachResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Error = Error.UIDocumentMissing(go.name)
                    };
                }

                Undo.DestroyObjectImmediate(doc);
                EditorUtility.SetDirty(go);

                return new DocumentAttachResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go)
                };
            });
        }
    }
}
