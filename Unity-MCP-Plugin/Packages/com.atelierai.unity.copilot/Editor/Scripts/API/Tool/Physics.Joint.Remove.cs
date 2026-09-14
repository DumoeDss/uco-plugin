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
using System.Collections.Generic;
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;
using UnityEngine;
using Component = UnityEngine.Component;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Physics
    {
        public const string PhysicsJointRemoveToolId = "physics-joint-remove";

        [McpPluginTool
        (
            PhysicsJointRemoveToolId,
            Title = "Physics / Joint / Remove",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Remove one or all Joint / Joint2D components from a GameObject. " +
            "Use `jointIndex = -1` to remove every matching joint at once, or a specific 0-based index to remove just " +
            "one. The `jointType` filter restricts removal to a specific joint family.")]
        [McpPluginSkillBody("Remove Joint components from a GameObject in the opened Prefab or active Scene.\n\n" +
            "## Inputs\n\n" +
            "- `target` — host GameObject. Required.\n" +
            "- `jointIndex` — 0-based index into the filtered joint list; `-1` removes every match. Default 0.\n" +
            "- `jointType` — case-insensitive alias filter. " +
            "3D: fixed/hinge/spring/character/configurable. " +
            "2D: distance/fixed/friction/hinge/relative/slider/spring/target/wheel. " +
            "When omitted, BOTH 3D and 2D joints are considered.\n\n" +
            "## Behavior\n\n" +
            "Walks `GetComponents<Joint>()` and `GetComponents<Joint2D>()`, applies the optional type filter, then " +
            "destroys the selected component(s) via `Undo.DestroyObjectImmediate`. Returns a `JointRemoveResult` with " +
            "the number of removed components.")]
        [Description("Remove one or all Joint / Joint2D components from a GameObject. " +
            "Pass jointIndex=-1 to remove every match.")]
        public JointRemoveResult RemoveJoint
        (
            [Description("GameObject hosting the joint(s) to remove. Required.")]
            GameObjectRef target,
            [Description("Index 0-based, or -1 to remove all matching joints. Default 0.")]
            int? jointIndex = null,
            [Description("Optional joint-type filter (case-insensitive alias). " +
                "3D: fixed/hinge/spring/character/configurable. " +
                "2D: distance/fixed/friction/hinge/relative/slider/spring/target/wheel.")]
            string? jointType = null
        )
        {
            if (target == null)
                return new JointRemoveResult { Ok = false, Error = "GameObject reference is required." };

            if (!target.IsValid(out var refErr))
                return new JointRemoveResult { Ok = false, Error = refErr };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new JointRemoveResult { Ok = false, Error = findErr ?? "GameObject not found." };

                // Collect all candidate joints (3D + 2D) preserving component order.
                var candidates = new List<Component>();
                foreach (var j in go.GetComponents<Joint>()) candidates.Add(j);
                foreach (var j in go.GetComponents<Joint2D>()) candidates.Add(j);

                if (candidates.Count == 0)
                    return new JointRemoveResult { Ok = false, GameObjectPath = JointPathOf(go), Error = "No Joint or Joint2D components on this GameObject." };

                // Apply jointType filter (matches against concrete type name prefix).
                List<Component> filtered;
                if (!string.IsNullOrEmpty(jointType))
                {
                    var filter = jointType!.Trim().ToLowerInvariant();
                    filtered = new List<Component>();
                    foreach (var c in candidates)
                    {
                        if (c.GetType().Name.ToLowerInvariant().StartsWith(filter))
                            filtered.Add(c);
                    }
                    if (filtered.Count == 0)
                    {
                        return new JointRemoveResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = $"No joints match jointType '{jointType}'. Present types: {string.Join(", ", JointTypeNamesUnique(candidates))}."
                        };
                    }
                }
                else
                {
                    filtered = candidates;
                }

                var idx = jointIndex ?? 0;
                var toRemove = new List<Component>();
                if (idx == -1)
                {
                    toRemove.AddRange(filtered);
                }
                else
                {
                    if (idx < 0 || idx >= filtered.Count)
                        return new JointRemoveResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = $"jointIndex {idx} out of range (have {filtered.Count} matching joint(s))."
                        };
                    toRemove.Add(filtered[idx]);
                }

                int removed = 0;
                foreach (var c in toRemove)
                {
                    if (c == null) continue;
                    Undo.DestroyObjectImmediate(c);
                    removed++;
                }

                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new JointRemoveResult
                {
                    Ok = removed > 0,
                    GameObjectPath = JointPathOf(go),
                    RemovedCount = removed,
                    Error = removed > 0 ? null : "Nothing was removed."
                };
            });
        }

        private static IEnumerable<string> JointTypeNamesUnique(List<UnityEngine.Component> joints)
        {
            var seen = new HashSet<string>();
            foreach (var j in joints)
            {
                var n = j.GetType().Name;
                if (seen.Add(n)) yield return n;
            }
        }
    }

    // -------------------------------------------------------------------
    // DTO declared at namespace level.
    // -------------------------------------------------------------------

    public class JointRemoveResult
    {
        [Description("True when at least one joint was removed.")]
        public bool Ok { get; set; }

        [Description("Hierarchy path of the target GameObject, when resolved.")]
        public string? GameObjectPath { get; set; }

        [Description("Number of joint components actually destroyed by this call.")]
        public int RemovedCount { get; set; }

        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }
    }
}
