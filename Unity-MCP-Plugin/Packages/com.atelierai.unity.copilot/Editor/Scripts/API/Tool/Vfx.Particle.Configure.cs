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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
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
        public const string VfxParticleConfigureToolId = "vfx-particle-configure";

        [McpPluginTool
        (
            VfxParticleConfigureToolId,
            Title = "VFX / Particle / Configure",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Configure high-frequency fields on a `UnityEngine.ParticleSystem` — " +
            "main module (duration / loop / startLifetime / startSpeed / startSize / startColor / " +
            "gravityModifier / maxParticles / simulationSpace), emission module (rateOverTime / enabled), " +
            "shape module (enabled / shapeType / radius), and velocity-over-lifetime (enabled / linear). " +
            "Pass only the fields you want to change — null fields are left untouched. " +
            "Use '" + VfxParticleGetToolId + "' to inspect current values first.")]
        [McpPluginSkillBody("Configure a Unity ParticleSystem. Only the supplied (non-null) fields are written; " +
            "everything else is left as-is. Curve / random-range / two-constant modes for MinMaxCurves are not " +
            "exposed by this tool — constant values overwrite the current curve. Use direct component modification " +
            "via 'gameobject-component-modify' for advanced cases.\n\n" +
            "## Modules covered\n\n" +
            "1. `main` — duration, looping, startLifetime, startSpeed, startSize, startColor (Vector4 RGBA), " +
            "gravityModifier, maxParticles, simulationSpace (Local / World / Custom).\n" +
            "2. `emission` — rateOverTime constant, enabled.\n" +
            "3. `shape` — enabled, shapeType, radius.\n" +
            "4. `velocityOverLifetime` — enabled, linear x/y/z constants.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Mutates the ParticleSystem via its module-value-type accessors " +
            "(`main.duration = ...` etc.), records Undo, marks the component dirty, and returns a `ParticleResult` " +
            "with `AppliedFields` (comma-separated names of modified fields) and `Snapshot` reflecting post-write state.")]
        [Description("Configure a ParticleSystem's most-common module fields. " +
            "Only provided (non-null) parameters are written. " +
            "Use '" + VfxParticleGetToolId + "' to inspect current values first.")]
        public ParticleResult ConfigureParticle
        (
            [Description("Target GameObject hosting the ParticleSystem. Use 'gameobject-find' to locate it.")]
            GameObjectRef target,

            // Main module
            [Description("Main: duration in seconds. Ignored when null.")]
            float? duration = null,
            [Description("Main: looping flag. Ignored when null.")]
            bool? looping = null,
            [Description("Main: startLifetime constant value. Ignored when null.")]
            float? startLifetime = null,
            [Description("Main: startSpeed constant value. Ignored when null.")]
            float? startSpeed = null,
            [Description("Main: startSize constant value. Ignored when null.")]
            float? startSize = null,
            [Description("Main: startColor (RGBA in [0..1] as Vector4). Ignored when null.")]
            Vector4? startColor = null,
            [Description("Main: gravityModifier constant value. Ignored when null.")]
            float? gravityModifier = null,
            [Description("Main: maxParticles cap. Ignored when null.")]
            int? maxParticles = null,
            [Description("Main: simulationSpace — one of 'Local', 'World', 'Custom' (case-insensitive). Ignored when null.")]
            string? simulationSpace = null,

            // Emission module
            [Description("Emission: rateOverTime constant value. Ignored when null.")]
            float? emissionRateOverTime = null,
            [Description("Emission: enabled flag. Ignored when null.")]
            bool? emissionEnabled = null,

            // Shape module
            [Description("Shape: enabled flag. Ignored when null.")]
            bool? shapeEnabled = null,
            [Description("Shape: shapeType — case-insensitive enum name " +
                "('Sphere', 'Hemisphere', 'Cone', 'Box', 'Donut', 'Circle', 'SingleSidedEdge', etc.). " +
                "Ignored when null.")]
            string? shapeType = null,
            [Description("Shape: radius value. Ignored when null.")]
            float? shapeRadius = null,

            // Velocity over lifetime
            [Description("Velocity over lifetime: enabled flag. Ignored when null.")]
            bool? velocityEnabled = null,
            [Description("Velocity over lifetime: linear x/y/z constants. Ignored when null.")]
            Vector3? velocityLinear = null
        )
        {
            if (target == null)
                return new ParticleResult { Ok = false, Error = Error.GameObjectRefRequired() };

            if (!target.IsValid(out var refErr))
                return new ParticleResult { Ok = false, Error = refErr };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new ParticleResult
                    {
                        Ok = false,
                        Error = findErr ?? "GameObject not found."
                    };

                var ps = go.GetComponent<ParticleSystem>();
                if (ps == null)
                    return new ParticleResult
                    {
                        Ok = false,
                        GameObjectPath = GetHierarchyPath(go),
                        Error = Error.ParticleSystemMissing(go.name)
                    };

                Undo.RecordObject(ps, "Configure ParticleSystem");

                var applied = new List<string>();

                // -------- Main module (struct — must reassign individual fields) --------
                if (duration.HasValue || looping.HasValue || startLifetime.HasValue
                    || startSpeed.HasValue || startSize.HasValue || startColor.HasValue
                    || gravityModifier.HasValue || maxParticles.HasValue
                    || !string.IsNullOrEmpty(simulationSpace))
                {
                    var main = ps.main;

                    if (duration.HasValue) { main.duration = duration.Value; applied.Add("duration"); }
                    if (looping.HasValue) { main.loop = looping.Value; applied.Add("looping"); }
                    if (startLifetime.HasValue) { main.startLifetime = startLifetime.Value; applied.Add("startLifetime"); }
                    if (startSpeed.HasValue) { main.startSpeed = startSpeed.Value; applied.Add("startSpeed"); }
                    if (startSize.HasValue) { main.startSize = startSize.Value; applied.Add("startSize"); }
                    if (startColor.HasValue)
                    {
                        var c = startColor.Value;
                        main.startColor = new Color(c.x, c.y, c.z, c.w);
                        applied.Add("startColor");
                    }
                    if (gravityModifier.HasValue) { main.gravityModifier = gravityModifier.Value; applied.Add("gravityModifier"); }
                    if (maxParticles.HasValue) { main.maxParticles = maxParticles.Value; applied.Add("maxParticles"); }
                    if (!string.IsNullOrEmpty(simulationSpace))
                    {
                        if (!Enum.TryParse<ParticleSystemSimulationSpace>(simulationSpace, ignoreCase: true, out var space))
                            return new ParticleResult
                            {
                                Ok = false,
                                GameObjectPath = GetHierarchyPath(go),
                                Error = Error.UnknownSimulationSpace(simulationSpace!)
                            };
                        main.simulationSpace = space;
                        applied.Add("simulationSpace");
                    }
                }

                // -------- Emission module --------
                if (emissionRateOverTime.HasValue || emissionEnabled.HasValue)
                {
                    var emission = ps.emission;
                    if (emissionEnabled.HasValue) { emission.enabled = emissionEnabled.Value; applied.Add("emission.enabled"); }
                    if (emissionRateOverTime.HasValue) { emission.rateOverTime = emissionRateOverTime.Value; applied.Add("emission.rateOverTime"); }
                }

                // -------- Shape module --------
                if (shapeEnabled.HasValue || !string.IsNullOrEmpty(shapeType) || shapeRadius.HasValue)
                {
                    var shape = ps.shape;
                    if (shapeEnabled.HasValue) { shape.enabled = shapeEnabled.Value; applied.Add("shape.enabled"); }
                    if (!string.IsNullOrEmpty(shapeType))
                    {
                        if (!Enum.TryParse<ParticleSystemShapeType>(shapeType, ignoreCase: true, out var st))
                            return new ParticleResult
                            {
                                Ok = false,
                                GameObjectPath = GetHierarchyPath(go),
                                Error = Error.UnknownShapeType(shapeType!)
                            };
                        shape.shapeType = st;
                        applied.Add("shape.shapeType");
                    }
                    if (shapeRadius.HasValue) { shape.radius = shapeRadius.Value; applied.Add("shape.radius"); }
                }

                // -------- Velocity over lifetime --------
                if (velocityEnabled.HasValue || velocityLinear.HasValue)
                {
                    var vol = ps.velocityOverLifetime;
                    if (velocityEnabled.HasValue) { vol.enabled = velocityEnabled.Value; applied.Add("velocity.enabled"); }
                    if (velocityLinear.HasValue)
                    {
                        var v = velocityLinear.Value;
                        vol.x = v.x;
                        vol.y = v.y;
                        vol.z = v.z;
                        applied.Add("velocity.linear");
                    }
                }

                EditorUtility.SetDirty(ps);
                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new ParticleResult
                {
                    Ok = true,
                    GameObjectPath = GetHierarchyPath(go),
                    AppliedFields = applied.Count > 0 ? string.Join(",", applied) : null,
                    Snapshot = BuildParticleSnapshot(go, ps)
                };
            });
        }
    }
}
