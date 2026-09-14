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
using System.Collections.Generic;
using System.ComponentModel;
using AIGD;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using UnityEngine.UIElements;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_UI
    {
        public const string ElementQueryToolId = "ui-element-query";

        [McpPluginTool
        (
            ElementQueryToolId,
            Title = "UI / Element / Query",
            ReadOnlyHint = true
        )]
        [McpPluginSkillDescription("Query the live `VisualElement` tree of a UIDocument on the target GameObject " +
            "using a small subset of UQuery: by name (`#myButton`), by USS class (`.button-primary`), or by type " +
            "(`Label`, `Button`, ...). Selectors can be combined: `Label.warning` returns Label elements with the " +
            "'warning' class. Returns a shallow snapshot (type / name / classes / text / resolved layout) for the " +
            "first `maxResults` matches.")]
        [McpPluginSkillBody("Query VisualElements under a UIDocument's rootVisualElement.\n\n" +
            "## Inputs\n\n" +
            "- `target` — host GameObject with a UIDocument. Required.\n" +
            "- `selector` — UQuery-style selector. Supported forms:\n" +
            "  - `#name` — match by VisualElement.name.\n" +
            "  - `.class` — match by USS class.\n" +
            "  - `TypeName` — match by C# type (resolved against `UnityEngine.UIElements`).\n" +
            "  - Combinations: `Label.warning`, `Button#submit`.\n" +
            "- `maxResults` (default 50) — caps the returned `Elements` array.\n\n" +
            "## Edit-mode caveat\n\n" +
            "`UIDocument.rootVisualElement` is normally only populated at runtime (Play Mode), because UIDocument " +
            "creates its panel lazily on `OnEnable`. In Edit Mode the rootVisualElement is often null — when that " +
            "happens this tool returns `Ok=false` with a precise message so callers can branch (e.g. ask the user " +
            "to enter Play Mode). Custom Editor Windows that host a UIToolkit `rootVisualElement` are out of scope " +
            "for this tool — query them through their owning EditorWindow instance instead.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Reads `rootVisualElement.Query()` and walks the resulting " +
            "`UQueryBuilder<VisualElement>`. Resolved layout (`resolvedStyle.display` and `layout`) is read for each " +
            "element and serialized into `ElementInfo`. Reads no scene state beyond the target GameObject — purely " +
            "informational.")]
        [Description("Query VisualElements under a UIDocument's rootVisualElement by UQuery-style selector.")]
        public ElementQueryResult QueryElements
        (
            [Description("Target GameObject hosting a UIDocument. Use 'gameobject-find' to locate it.")]
            GameObjectRef target,
            [Description("UQuery selector. Sample: '.button-primary', '#title', 'Label.warning'.")]
            string selector,
            [Description("Maximum number of results to return. Default 50.")]
            int maxResults = 50
        )
        {
            if (target == null)
                return new ElementQueryResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (!target.IsValid(out var refErr))
                return new ElementQueryResult { Ok = false, Error = refErr };

            if (string.IsNullOrEmpty(selector))
                return new ElementQueryResult { Ok = false, Error = Error.UQuerySelectorEmpty() };

            if (maxResults < 1)
                maxResults = 1;

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                {
                    return new ElementQueryResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found."
                    };
                }

                var doc = go.GetComponent<UIDocument>();
                if (doc == null)
                {
                    return new ElementQueryResult
                    {
                        Ok = false,
                        Error = Error.UIDocumentMissing(go.name)
                    };
                }

                var root = doc.rootVisualElement;
                if (root == null)
                {
                    // Edit-mode case — UIDocument has not built its panel yet.
                    return new ElementQueryResult
                    {
                        Ok = false,
                        Error = Error.EditorRootVisualElementUnavailable()
                    };
                }

                // Parse selector into (typeName?, name?, className?). Order-independent.
                ParseUQuerySelector(selector, out var typeName, out var elementName, out var className);

                // Resolve type (if requested) via UQueryBuilder<T> using reflection — but UIElements
                // doesn't expose a typed Query by string in 2022.3, so we filter manually after a
                // broad Query<VisualElement>() walk.
                Type? filterType = null;
                if (!string.IsNullOrEmpty(typeName))
                {
                    filterType = ResolveUIElementType(typeName!);
                    if (filterType == null)
                    {
                        return new ElementQueryResult
                        {
                            Ok = false,
                            Error = $"Unknown VisualElement type '{typeName}'. Expected a type from UnityEngine.UIElements (e.g. Label, Button, VisualElement)."
                        };
                    }
                }

                var matches = new List<ElementInfo>(capacity: Math.Min(maxResults, 64));
                int totalCount = 0;

                // Walk the whole tree once. Cheap for typical UIDocument trees (a few hundred
                // elements at most) and avoids the need to special-case UQueryBuilder generics.
                root.Query<VisualElement>().ForEach(ve =>
                {
                    if (filterType != null && !filterType.IsInstanceOfType(ve))
                        return;
                    if (!string.IsNullOrEmpty(elementName) && ve.name != elementName)
                        return;
                    if (!string.IsNullOrEmpty(className) && !ve.ClassListContains(className))
                        return;

                    totalCount++;
                    if (matches.Count >= maxResults)
                        return;

                    matches.Add(BuildElementInfo(ve));
                });

                return new ElementQueryResult
                {
                    Ok = true,
                    TotalCount = totalCount,
                    Elements = matches.ToArray()
                };
            });
        }

        // ----- UQuery helpers -----

        /// <summary>
        /// Parse a simple UQuery selector into (typeName?, name?, className?). Supports:
        ///   "TypeName", ".class", "#name", "TypeName.class", "TypeName#name", "#name.class".
        /// Anything beyond a single triple is ignored — power users can fall back to the
        /// raw Query API via the script-execute tool.
        /// </summary>
        internal static void ParseUQuerySelector(string selector, out string? typeName, out string? name, out string? className)
        {
            typeName = null;
            name = null;
            className = null;

            // Walk the string left-to-right, accumulating tokens by their introducer ('#', '.', or none).
            int i = 0;
            while (i < selector.Length)
            {
                char c = selector[i];
                if (c == '#' || c == '.')
                {
                    int start = i + 1;
                    int end = start;
                    while (end < selector.Length && selector[end] != '#' && selector[end] != '.')
                        end++;
                    string token = selector.Substring(start, end - start);
                    if (c == '#')
                        name = token;
                    else
                        className = token;
                    i = end;
                }
                else
                {
                    int start = i;
                    int end = start;
                    while (end < selector.Length && selector[end] != '#' && selector[end] != '.')
                        end++;
                    typeName = selector.Substring(start, end - start);
                    i = end;
                }
            }
        }

        /// <summary>
        /// Resolve a short type name (e.g. "Label", "Button", "VisualElement") into a
        /// runtime <see cref="Type"/> from the UnityEngine.UIElements assembly. Returns
        /// null when the type cannot be found.
        /// </summary>
        internal static Type? ResolveUIElementType(string typeName)
        {
            // Try a fully-qualified lookup first (handles user-passed namespaces).
            var directHit = Type.GetType(typeName);
            if (directHit != null && typeof(VisualElement).IsAssignableFrom(directHit))
                return directHit;

            // Then probe the UnityEngine.UIElements assembly for the short name.
            var uiAssembly = typeof(VisualElement).Assembly;
            foreach (var t in uiAssembly.GetTypes())
            {
                if (!typeof(VisualElement).IsAssignableFrom(t))
                    continue;
                if (t.Name == typeName || t.FullName == typeName)
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Project a live VisualElement into a serializable ElementInfo snapshot.
        /// </summary>
        internal static ElementInfo BuildElementInfo(VisualElement ve)
        {
            string? text = ve switch
            {
                TextElement te => te.text,
                _ => null
            };

            var classes = new List<string>(ve.GetClasses());
            var layout = ve.layout;

            // resolvedStyle.display may throw before the layout pass — guard with a try.
            bool visible = true;
            try
            {
                visible = ve.resolvedStyle.display != DisplayStyle.None && ve.visible;
            }
            catch
            {
                visible = ve.visible;
            }

            return new ElementInfo
            {
                Name = ve.name ?? "",
                TypeName = ve.GetType().FullName ?? ve.GetType().Name,
                ClassNames = classes.ToArray(),
                Text = text,
                Visible = visible,
                X = layout.x,
                Y = layout.y,
                Width = layout.width,
                Height = layout.height
            };
        }
    }
}
