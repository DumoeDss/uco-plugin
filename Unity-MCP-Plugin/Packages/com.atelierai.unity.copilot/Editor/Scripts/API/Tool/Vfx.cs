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
using System.ComponentModel;
using com.AtelierAI.Uco.Framework;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [UcoToolType]
    public partial class Tool_Vfx
    {
        // ---------------------------------------------------------------
        // Error / message helpers
        // ---------------------------------------------------------------
        public static class Error
        {
            public static string GameObjectRefRequired() =>
                "GameObject reference is required.";

            public static string ParticleSystemMissing(string goName) =>
                $"No 'UnityEngine.ParticleSystem' component found on GameObject '{goName}'.";

            public static string TrailRendererMissing(string goName) =>
                $"No 'UnityEngine.TrailRenderer' component found on GameObject '{goName}'.";

            public static string VisualEffectMissing(string goName) =>
                $"No 'UnityEngine.VFX.VisualEffect' component found on GameObject '{goName}'.";

            public static string VfxGraphNotInstalled() =>
                "VFX Graph package (com.unity.visualeffectgraph) not installed.";

            public static string UnknownAction(string value) =>
                $"Unknown action '{value}'. Expected one of: play, pause, stop, emit, clear.";

            public static string UnknownSimulationSpace(string value) =>
                $"Unknown simulationSpace '{value}'. Expected one of: Local, World, Custom.";

            public static string UnknownShapeType(string value) =>
                $"Unknown shapeType '{value}'. Expected one of: Sphere, Hemisphere, Cone, Box, " +
                "Donut, Circle, SingleSidedEdge, Rectangle, Mesh, MeshRenderer, SkinnedMeshRenderer, " +
                "BoxShell, BoxEdge.";

            public static string UnknownValueType(string value) =>
                $"Unknown valueType '{value}'. Expected one of: float, int, bool, vector2, vector3, vector4, color.";

            public static string PropertyMissing(string property) =>
                $"VFX Graph property '{property}' does not exist on the target VisualEffect asset.";

            public static string InvalidValueJson(string property, string detail) =>
                $"Failed to parse valueJson for property '{property}': {detail}";

            public static string MissingPropertyName() =>
                "Property name (propertyName) must be a non-empty string.";

            public static string MissingValueJson() =>
                "valueJson is required.";

            public static string MissingValueType() =>
                "valueType is required.";

            public static string MissingAction() =>
                "action is required.";
        }

        // ---------------------------------------------------------------
        // DTOs
        // ---------------------------------------------------------------

        /// <summary>
        /// Result returned by ParticleSystem tool calls.
        /// </summary>
        public class ParticleResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Hierarchy path of the target GameObject, when resolved.")]
            public string? GameObjectPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }

            [Description("Comma-separated names of the fields that were modified by this call. " +
                "Null for read-only operations.")]
            public string? AppliedFields { get; set; }

            [Description("Snapshot of the ParticleSystem state after the operation. " +
                "Null when no GameObject / ParticleSystem was resolved.")]
            public ParticleSnapshot? Snapshot { get; set; }
        }

        /// <summary>
        /// Read-only snapshot of a <see cref="UnityEngine.ParticleSystem"/>.
        /// </summary>
        public class ParticleSnapshot
        {
            [Description("Hierarchy path of the ParticleSystem GameObject.")]
            public string Path { get; set; } = "";

            [Description("ParticleSystem.isPlaying.")]
            public bool IsPlaying { get; set; }

            [Description("ParticleSystem.isPaused.")]
            public bool IsPaused { get; set; }

            [Description("ParticleSystem.isEmitting.")]
            public bool IsEmitting { get; set; }

            [Description("ParticleSystem.time (seconds since the system started).")]
            public float Time { get; set; }

            [Description("ParticleSystem.particleCount — number of live particles.")]
            public int ParticleCount { get; set; }

            // ----- Main module -----
            [Description("Main: duration in seconds.")]
            public float Duration { get; set; }

            [Description("Main: loop flag.")]
            public bool Looping { get; set; }

            [Description("Main: startLifetime constant value.")]
            public float StartLifetime { get; set; }

            [Description("Main: startSpeed constant value.")]
            public float StartSpeed { get; set; }

            [Description("Main: startSize constant value.")]
            public float StartSize { get; set; }

            [Description("Main: startColor (RGBA in [0..1]) — taken as the constant 'color' value.")]
            public Vector4 StartColor { get; set; }

            [Description("Main: gravityModifier constant value.")]
            public float GravityModifier { get; set; }

            [Description("Main: maxParticles.")]
            public int MaxParticles { get; set; }

            [Description("Main: simulationSpace enum name (Local / World / Custom).")]
            public string SimulationSpace { get; set; } = "";

            // ----- Emission module -----
            [Description("Emission: enabled flag.")]
            public bool EmissionEnabled { get; set; }

            [Description("Emission: rateOverTime constant value.")]
            public float EmissionRateOverTime { get; set; }

            // ----- Shape module -----
            [Description("Shape: enabled flag.")]
            public bool ShapeEnabled { get; set; }

            [Description("Shape: shapeType enum name.")]
            public string ShapeType { get; set; } = "";

            [Description("Shape: radius value.")]
            public float ShapeRadius { get; set; }

            // ----- Velocity over lifetime -----
            [Description("Velocity over lifetime: enabled flag.")]
            public bool VelocityEnabled { get; set; }

            [Description("Velocity over lifetime: linear x/y/z constants.")]
            public Vector3 VelocityLinear { get; set; }
        }

        /// <summary>
        /// Result returned by TrailRenderer tool calls.
        /// </summary>
        public class TrailResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Hierarchy path of the target GameObject, when resolved.")]
            public string? GameObjectPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }

            [Description("Comma-separated names of the fields that were modified by this call.")]
            public string? AppliedFields { get; set; }

            [Description("Snapshot of the TrailRenderer state after the operation.")]
            public TrailSnapshot? Snapshot { get; set; }
        }

        /// <summary>
        /// Read-only snapshot of a <see cref="UnityEngine.TrailRenderer"/>.
        /// </summary>
        public class TrailSnapshot
        {
            [Description("Hierarchy path of the TrailRenderer GameObject.")]
            public string Path { get; set; } = "";

            [Description("TrailRenderer.time (seconds).")]
            public float Time { get; set; }

            [Description("TrailRenderer.startWidth (world units).")]
            public float StartWidth { get; set; }

            [Description("TrailRenderer.endWidth (world units).")]
            public float EndWidth { get; set; }

            [Description("TrailRenderer.startColor (RGBA in [0..1]).")]
            public Vector4 StartColor { get; set; }

            [Description("TrailRenderer.endColor (RGBA in [0..1]).")]
            public Vector4 EndColor { get; set; }

            [Description("TrailRenderer.minVertexDistance (world units).")]
            public float MinVertexDistance { get; set; }

            [Description("TrailRenderer.emitting flag.")]
            public bool Emitting { get; set; }

            [Description("TrailRenderer.autodestruct flag.")]
            public bool AutoDestruct { get; set; }

            [Description("TrailRenderer.positionCount.")]
            public int PositionCount { get; set; }
        }

        /// <summary>
        /// Result returned by VFX Graph (VisualEffect) tool calls.
        /// </summary>
        public class VfxGraphResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Hierarchy path of the target GameObject, when resolved.")]
            public string? GameObjectPath { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }

            [Description("Name of the property that was written. Null on failure or when omitted.")]
            public string? PropertyName { get; set; }

            [Description("Value type that was written ('float','int','bool','vector2','vector3','vector4','color').")]
            public string? ValueType { get; set; }
        }

        // ---------------------------------------------------------------
        // VFX Graph package detection (reflection-only — no NuGet dep)
        // ---------------------------------------------------------------

        /// <summary>
        /// Detects whether the VFX Graph package (<c>com.unity.visualeffectgraph</c>) is available.
        /// </summary>
        internal static bool IsVfxGraphAvailable() => GetVisualEffectType() != null;

        /// <summary>
        /// Returns the <c>UnityEngine.VFX.VisualEffect</c> <see cref="Type"/>, or null when the
        /// package is not installed.
        /// </summary>
        internal static Type? GetVisualEffectType()
        {
            try
            {
                return Type.GetType("UnityEngine.VFX.VisualEffect, Unity.VisualEffectGraph.Runtime", false)
                    ?? Type.GetType("UnityEngine.VFX.VisualEffect, UnityEngine.VFXModule", false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Locate a <c>VisualEffect</c> component on a GameObject without taking a hard dependency
        /// on the VFX Graph assembly. Returns null when the package is not installed or when the
        /// GameObject does not host such a component.
        /// </summary>
        internal static UnityEngine.Component? FindVisualEffect(GameObject go)
        {
            if (go == null) return null;
            var t = GetVisualEffectType();
            if (t == null) return null;
            return go.GetComponent(t);
        }

        // ---------------------------------------------------------------
        // Snapshot builders
        // ---------------------------------------------------------------

        /// <summary>
        /// Build a <see cref="ParticleSnapshot"/> for the supplied ParticleSystem.
        /// Returns null when <paramref name="ps"/> is null. Must be called on the Unity main thread.
        /// </summary>
        internal static ParticleSnapshot? BuildParticleSnapshot(GameObject go, ParticleSystem ps)
        {
            if (ps == null) return null;
            var main = ps.main;
            var emission = ps.emission;
            var shape = ps.shape;
            var velocity = ps.velocityOverLifetime;

            var startCol = main.startColor.color;
            return new ParticleSnapshot
            {
                Path = GetHierarchyPath(go),
                IsPlaying = ps.isPlaying,
                IsPaused = ps.isPaused,
                IsEmitting = ps.isEmitting,
                Time = ps.time,
                ParticleCount = ps.particleCount,

                Duration = main.duration,
                Looping = main.loop,
                StartLifetime = main.startLifetime.constant,
                StartSpeed = main.startSpeed.constant,
                StartSize = main.startSize.constant,
                StartColor = new Vector4(startCol.r, startCol.g, startCol.b, startCol.a),
                GravityModifier = main.gravityModifier.constant,
                MaxParticles = main.maxParticles,
                SimulationSpace = main.simulationSpace.ToString(),

                EmissionEnabled = emission.enabled,
                EmissionRateOverTime = emission.rateOverTime.constant,

                ShapeEnabled = shape.enabled,
                ShapeType = shape.shapeType.ToString(),
                ShapeRadius = shape.radius,

                VelocityEnabled = velocity.enabled,
                VelocityLinear = new Vector3(velocity.x.constant, velocity.y.constant, velocity.z.constant)
            };
        }

        /// <summary>
        /// Build a <see cref="TrailSnapshot"/> for the supplied TrailRenderer.
        /// </summary>
        internal static TrailSnapshot? BuildTrailSnapshot(GameObject go, TrailRenderer tr)
        {
            if (tr == null) return null;
            var sc = tr.startColor;
            var ec = tr.endColor;
            return new TrailSnapshot
            {
                Path = GetHierarchyPath(go),
                Time = tr.time,
                StartWidth = tr.startWidth,
                EndWidth = tr.endWidth,
                StartColor = new Vector4(sc.r, sc.g, sc.b, sc.a),
                EndColor = new Vector4(ec.r, ec.g, ec.b, ec.a),
                MinVertexDistance = tr.minVertexDistance,
                Emitting = tr.emitting,
                AutoDestruct = tr.autodestruct,
                PositionCount = tr.positionCount
            };
        }

        /// <summary>
        /// Compute the slash-separated hierarchy path of a GameObject (e.g. "Root/Child/FX").
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
