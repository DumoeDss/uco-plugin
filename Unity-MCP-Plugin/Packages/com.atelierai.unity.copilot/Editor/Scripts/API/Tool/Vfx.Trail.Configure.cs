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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Vfx
    {
        public const string VfxTrailConfigureToolId = "vfx-trail-configure";

        [UcoTool
        (
            VfxTrailConfigureToolId,
            Title = "VFX / Trail / Configure",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Configure a `UnityEngine.TrailRenderer` — time, start/end width, start/end color, " +
            "minVertexDistance, emitting flag, autodestruct flag. " +
            "Pass only the fields you want to change — null fields are left untouched.")]
        [UcoSkillBody("Configure a Unity TrailRenderer. Only the supplied (non-null) fields are written; " +
            "everything else is left as-is. The colors are accepted as `Vector4` (RGBA in [0..1]).\n\n" +
            "## Inputs\n\n" +
            "- `target` — host GameObject of the TrailRenderer. Required.\n" +
            "- `time` — trail duration in seconds.\n" +
            "- `startWidth` / `endWidth` — trail width at start / end (world units).\n" +
            "- `startColor` / `endColor` — RGBA in [0..1] as Vector4.\n" +
            "- `minVertexDistance` — minimum distance between trail vertices.\n" +
            "- `emitting` — whether the trail is emitting new segments.\n" +
            "- `autoDestruct` — whether the GameObject self-destroys after the trail fades.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Mutates the TrailRenderer via direct property writes, records Undo, " +
            "marks the component dirty, and returns a `TrailResult` with `Snapshot` reflecting post-write state.")]
        [Description("Configure a TrailRenderer's most-common fields. " +
            "Only provided (non-null) parameters are written.")]
        public TrailResult ConfigureTrail
        (
            [Description("Target GameObject hosting the TrailRenderer. Use 'gameobject-find' to locate it.")]
            GameObjectRef target,
            [Description("Trail duration in seconds. Ignored when null.")]
            float? time = null,
            [Description("Trail start width (world units). Ignored when null.")]
            float? startWidth = null,
            [Description("Trail end width (world units). Ignored when null.")]
            float? endWidth = null,
            [Description("Trail start color (RGBA in [0..1] as Vector4). Ignored when null.")]
            Vector4? startColor = null,
            [Description("Trail end color (RGBA in [0..1] as Vector4). Ignored when null.")]
            Vector4? endColor = null,
            [Description("Minimum distance between trail vertices. Ignored when null. Note: TrailRenderer.minVertexDistance is a float in Unity.")]
            float? minVertexDistance = null,
            [Description("Whether the trail is currently emitting new segments. Ignored when null.")]
            bool? emitting = null,
            [Description("Whether the GameObject auto-destructs after the trail fades. Ignored when null.")]
            bool? autoDestruct = null
        )
        {
            if (target == null)
                return new TrailResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (!target.IsValid(out var refErr))
                return new TrailResult { Ok = false, Error = refErr };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new TrailResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found."
                    };

                var tr = go.GetComponent<TrailRenderer>();
                if (tr == null)
                    return new TrailResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Error = Error.TrailRendererMissing(go.name)
                    };

                Undo.RecordObject(tr, "Configure TrailRenderer");

                var applied = new List<string>();

                if (time.HasValue) { tr.time = time.Value; applied.Add("time"); }
                if (startWidth.HasValue) { tr.startWidth = startWidth.Value; applied.Add("startWidth"); }
                if (endWidth.HasValue) { tr.endWidth = endWidth.Value; applied.Add("endWidth"); }
                if (startColor.HasValue)
                {
                    var c = startColor.Value;
                    tr.startColor = new Color(c.x, c.y, c.z, c.w);
                    applied.Add("startColor");
                }
                if (endColor.HasValue)
                {
                    var c = endColor.Value;
                    tr.endColor = new Color(c.x, c.y, c.z, c.w);
                    applied.Add("endColor");
                }
                if (minVertexDistance.HasValue) { tr.minVertexDistance = minVertexDistance.Value; applied.Add("minVertexDistance"); }
                if (emitting.HasValue) { tr.emitting = emitting.Value; applied.Add("emitting"); }
                if (autoDestruct.HasValue) { tr.autodestruct = autoDestruct.Value; applied.Add("autodestruct"); }

                EditorUtility.SetDirty(tr);
                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new TrailResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    AppliedFields = applied.Count > 0 ? string.Join(",", applied) : null,
                    Snapshot = BuildTrailSnapshot(go, tr)
                };
            });
        }
    }
}
