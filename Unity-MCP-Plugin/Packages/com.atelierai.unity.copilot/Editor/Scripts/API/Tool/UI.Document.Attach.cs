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
using System;
using System.ComponentModel;
using AIGD;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string DocumentAttachToolId = "ui-document-attach";

        [UcoTool
        (
            DocumentAttachToolId,
            Title = "UI / Document / Attach",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Add a `UnityEngine.UIElements.UIDocument` to the target GameObject and wire up " +
            "its `visualTreeAsset` to a UXML at the given asset path. Optionally sets the `panelSettings` reference " +
            "(when the path points at a PanelSettings .asset) and the `sortingOrder`. " +
            "Use '" + UxmlCreateToolId + "' to author the UXML asset first.")]
        [UcoSkillBody("Attach a UIDocument to a GameObject and bind it to a UXML / PanelSettings.\n\n" +
            "## Inputs\n\n" +
            "- `target` — host GameObject. Required. UIDocument is added via `GetComponent` + `AddComponent` so " +
            "calling twice is idempotent (the existing UIDocument is reconfigured).\n" +
            "- `visualTreeAssetPath` — required project-relative path to a .uxml file. " +
            "Loaded with `AssetDatabase.LoadAssetAtPath<VisualTreeAsset>`.\n" +
            "- `panelSettingsPath` — optional. When provided, loaded with " +
            "`AssetDatabase.LoadAssetAtPath<PanelSettings>` and assigned to `UIDocument.panelSettings`.\n" +
            "- `sortingOrder` — optional. Assigned to `UIDocument.sortingOrder`.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Records Undo and marks the GameObject dirty so the change persists into " +
            "the scene/prefab. Returns `Ok=false` with a precise error when the GameObject is unresolved or any " +
            "asset path fails to load.")]
        [Description("Attach a UIDocument to a GameObject and wire it to a UXML asset.")]
        public DocumentAttachResult AttachDocument
        (
            [Description("Target GameObject hosting the UIDocument. Use 'gameobject-find' to locate it.")]
            GameObjectRef target,
            [Description("Project-relative path to .uxml source asset. Required.")]
            string visualTreeAssetPath,
            [Description("Optional project-relative path to a PanelSettings .asset to assign to UIDocument.panelSettings.")]
            string? panelSettingsPath = null,
            [Description("Optional sorting order. When null, UIDocument.sortingOrder is left untouched.")]
            float? sortingOrder = null
        )
        {
            if (target == null)
                return new DocumentAttachResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (!target.IsValid(out var refErr))
                return new DocumentAttachResult { Ok = false, Error = refErr };

            if (string.IsNullOrEmpty(visualTreeAssetPath))
                return new DocumentAttachResult { Ok = false, Error = Error.VisualTreeAssetPathRequired() };

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

                var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(visualTreeAssetPath);
                if (vta == null)
                {
                    return new DocumentAttachResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Error = Error.VisualTreeAssetNotFound(visualTreeAssetPath)
                    };
                }

                PanelSettings? panelSettings = null;
                if (!string.IsNullOrEmpty(panelSettingsPath))
                {
                    panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                    if (panelSettings == null)
                    {
                        return new DocumentAttachResult
                        {
                            Ok = false,
                            GameObjectPath = GetHierarchyPath(go),
                            Error = Error.PanelSettingsAssetNotFound(panelSettingsPath!)
                        };
                    }
                }

                // Reuse an existing UIDocument when present (idempotent attach) instead of
                // stacking duplicate components.
                var doc = go.GetComponent<UIDocument>();
                if (doc == null)
                {
                    Undo.AddComponent<UIDocument>(go);
                    doc = go.GetComponent<UIDocument>();
                    if (doc == null)
                    {
                        return new DocumentAttachResult
                        {
                            Ok = false,
                            GameObjectPath = GetHierarchyPath(go),
                            Error = "Failed to add UIDocument component."
                        };
                    }
                }
                else
                {
                    Undo.RecordObject(doc, "Attach UIDocument");
                }

                doc.visualTreeAsset = vta;
                if (panelSettings != null)
                    doc.panelSettings = panelSettings;
                if (sortingOrder.HasValue)
                    doc.sortingOrder = sortingOrder.Value;

                EditorUtility.SetDirty(doc);
                EditorUtility.SetDirty(go);

                return new DocumentAttachResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    AssetPath = visualTreeAssetPath
                };
            });
        }

        // ----- Shared hierarchy-path helper (used by every UI tool that touches a GameObject) -----

        /// <summary>
        /// Compute the slash-separated hierarchy path of a GameObject (e.g. "Root/Child/Canvas").
        /// </summary>
        internal static string GetHierarchyPath(GameObject go)
        {
            if (go == null) return "";
            var t = go.transform;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }
    }
}
