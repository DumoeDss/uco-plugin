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
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using UnityEditor;
using UnityEngine;

namespace AIGD
{
    [Description("Snapshot of a Rigidbody / Rigidbody2D and the changes applied to it.")]
    public class RigidbodyResult
    {
        [Description("True when the operation completed without errors.")]
        public bool Ok { get; set; }
        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }

        [Description("Name of the host GameObject.")]
        public string? Target { get; set; }
        [Description("Dimension of the rigidbody: '2d' or '3d'.")]
        public string Dimension { get; set; } = "3d";

        // Shared
        [Description("Rigidbody mass.")]
        public float? Mass { get; set; }
        [Description("Linear damping (Unity 6) / drag (pre-Unity 6).")]
        public float? LinearDamping { get; set; }
        [Description("Angular damping (Unity 6) / angularDrag (pre-Unity 6).")]
        public float? AngularDamping { get; set; }
        [Description("Whether the body is kinematic.")]
        public bool? IsKinematic { get; set; }
        [Description("Collision detection mode name.")]
        public string? CollisionDetection { get; set; }
        [Description("Constraints flags as a comma-separated enum name list.")]
        public string? Constraints { get; set; }

        // 3D-only
        [Description("Whether 3D gravity is applied (3D only).")]
        public bool? UseGravity { get; set; }
        [Description("Interpolation mode name (3D only).")]
        public string? Interpolation { get; set; }

        // 2D-only
        [Description("2D gravity scale (2D only).")]
        public float? GravityScale { get; set; }
        [Description("2D body type name (Dynamic / Kinematic / Static).")]
        public string? BodyType { get; set; }
        [Description("Whether the 2D body is simulated.")]
        public bool? Simulated { get; set; }

        [Description("Names of properties mutated by configure. Empty for get.")]
        public List<string> Changed { get; set; } = new List<string>();
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;

    public partial class Tool_Physics
    {
        public const string PhysicsRigidbodyGetToolId = "physics-rigidbody-get";
        public const string PhysicsRigidbodyConfigureToolId = "physics-rigidbody-configure";

        // -----------------------------------------------------------------
        // Get
        // -----------------------------------------------------------------

        [McpPluginTool
        (
            PhysicsRigidbodyGetToolId,
            Title = "Physics / Rigidbody / Get",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Snapshot the Rigidbody (3D) or Rigidbody2D (2D) state on the target GameObject. " +
            "When the GameObject has both, the explicit 'dimension' argument disambiguates; otherwise the present " +
            "component wins.")]
        [McpPluginSkillBody("Reads `mass`, damping, kinematic / gravity flags, interpolation, collision detection, " +
            "constraints, and current linear / angular velocity into a `RigidbodyResult`.\n\n" +
            "## Inputs\n\n" +
            "- `target` — GameObjectRef (path / name / instanceID).\n" +
            "- `dimension` (optional) — `'2d'` or `'3d'` override. When omitted and only one of the components is " +
            "present, that component is used.")]
        [Description("Snapshot the Rigidbody / Rigidbody2D state on the target GameObject.")]
        public RigidbodyResult GetRigidbody
        (
            [Description("Target GameObject with a Rigidbody or Rigidbody2D.")]
            GameObjectRef target,
            [Description("Dimension override: '3d' or '2d'. If omitted, inferred from the present component.")]
            string? dimension = null
        )
        {
            if (target == null)
                return new RigidbodyResult { Ok = false, Error = Error.GameObjectRefIsNull() };
            if (!target.IsValid(out var refErr))
                return new RigidbodyResult { Ok = false, Error = refErr };

            PhysicsDimension? explicitDim = null;
            if (!string.IsNullOrEmpty(dimension))
            {
                if (!TryParseDimension(dimension, out var dimParsed, out var dimErr))
                    return new RigidbodyResult { Ok = false, Error = dimErr };
                explicitDim = dimParsed;
            }

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var goErr);
                if (go == null)
                    return new RigidbodyResult { Ok = false, Error = goErr ?? "GameObject not found." };

                var rb3D = go.GetComponent<Rigidbody>();
                var rb2D = go.GetComponent<Rigidbody2D>();
                bool is2D = ChooseDimension(explicitDim, rb3D != null, rb2D != null, out var dimErr);
                if (dimErr != null)
                    return new RigidbodyResult { Ok = false, Error = dimErr.Replace("__name__", go.name) };

                if (is2D)
                    return SnapshotRigidbody2D(go, rb2D!);

                return SnapshotRigidbody3D(go, rb3D!);
            });
        }

        // -----------------------------------------------------------------
        // Configure
        // -----------------------------------------------------------------

        [McpPluginTool
        (
            PhysicsRigidbodyConfigureToolId,
            Title = "Physics / Rigidbody / Configure",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Mutate fields of a Rigidbody / Rigidbody2D. Only non-null arguments are " +
            "applied. The dimension is inferred from the present component when not specified.")]
        [McpPluginSkillBody("Writes to `Rigidbody` (3D) or `Rigidbody2D` (2D). Maps `drag` / `angularDrag` to the " +
            "Unity 6 `linearDamping` / `angularDamping` API automatically.\n\n" +
            "## Inputs\n\n" +
            "- `target` — GameObjectRef.\n" +
            "- `dimension` (optional) — override the inferred dimension.\n" +
            "- `mass`, `drag` (linearDamping), `angularDrag` (angularDamping), `isKinematic`, " +
            "`collisionDetection`, `constraints` — apply to both dimensions.\n" +
            "- 3D-only: `useGravity`, `interpolation`.\n" +
            "- 2D-only: `gravityScale`, `bodyType`, `simulated`.\n\n" +
            "## Unity-version notes\n\n" +
            "On Unity 6+ this writes to `linearDamping` / `angularDamping`. On older versions it falls back to the " +
            "legacy `drag` / `angularDrag` properties under the `#if UNITY_6000_0_OR_NEWER` guard.")]
        [Description("Mutate fields of a Rigidbody / Rigidbody2D on the target GameObject.")]
        public RigidbodyResult ConfigureRigidbody
        (
            [Description("Target GameObject with a Rigidbody or Rigidbody2D.")]
            GameObjectRef target,
            [Description("Dimension override: '3d' or '2d'. If omitted, inferred from the present component.")]
            string? dimension = null,
            [Description("Mass.")]
            float? mass = null,
            [Description("Linear drag / damping. Maps to Unity 6 linearDamping.")]
            float? drag = null,
            [Description("Angular drag / damping. Maps to Unity 6 angularDamping.")]
            float? angularDrag = null,
            [Description("Whether 3D gravity is applied (3D only).")]
            bool? useGravity = null,
            [Description("Whether the body is kinematic.")]
            bool? isKinematic = null,
            [Description("Interpolation mode (3D only): None, Interpolate, Extrapolate.")]
            string? interpolation = null,
            [Description("Collision detection mode. 3D: Discrete / Continuous / ContinuousDynamic / ContinuousSpeculative. " +
                "2D: Discrete / Continuous.")]
            string? collisionDetection = null,
            [Description("Constraints flags. Comma-separated enum names (e.g. 'FreezePositionX, FreezeRotationY') " +
                "or an integer bitmask.")]
            string? constraints = null,
            [Description("Gravity scale (2D only).")]
            float? gravityScale = null,
            [Description("Body type (2D only): Dynamic, Kinematic, Static.")]
            string? bodyType = null,
            [Description("Whether the 2D body is simulated.")]
            bool? simulated = null
        )
        {
            if (target == null)
                return new RigidbodyResult { Ok = false, Error = Error.GameObjectRefIsNull() };
            if (!target.IsValid(out var refErr))
                return new RigidbodyResult { Ok = false, Error = refErr };

            PhysicsDimension? explicitDim = null;
            if (!string.IsNullOrEmpty(dimension))
            {
                if (!TryParseDimension(dimension, out var dimParsed, out var dimErr))
                    return new RigidbodyResult { Ok = false, Error = dimErr };
                explicitDim = dimParsed;
            }

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var goErr);
                if (go == null)
                    return new RigidbodyResult { Ok = false, Error = goErr ?? "GameObject not found." };

                var rb3D = go.GetComponent<Rigidbody>();
                var rb2D = go.GetComponent<Rigidbody2D>();
                bool is2D = ChooseDimension(explicitDim, rb3D != null, rb2D != null, out var dimErr);
                if (dimErr != null)
                    return new RigidbodyResult { Ok = false, Error = dimErr.Replace("__name__", go.name) };

                if (is2D)
                    return ConfigureRigidbody2D(go, rb2D!, mass, drag, angularDrag, isKinematic,
                        collisionDetection, constraints, gravityScale, bodyType, simulated);

                return ConfigureRigidbody3D(go, rb3D!, mass, drag, angularDrag, useGravity, isKinematic,
                    interpolation, collisionDetection, constraints);
            });
        }

        // -----------------------------------------------------------------
        // Internal helpers — dimension selection
        // -----------------------------------------------------------------

        /// <summary>
        /// Selects the dimension to operate on. Returns true for 2D, false for 3D.
        /// Populates <paramref name="error"/> when no Rigidbody of any dimension is present. The placeholder
        /// <c>__name__</c> is replaced with the GameObject name by the caller.
        /// </summary>
        private static bool ChooseDimension(PhysicsDimension? explicitDim, bool has3D, bool has2D, out string? error)
        {
            error = null;
            if (explicitDim.HasValue)
                return explicitDim.Value == PhysicsDimension.TwoD;

            if (!has3D && !has2D)
            {
                error = Error.NoRigidbodyOnGameObject("__name__");
                return false;
            }

            // Prefer the present component. When both are present, 3D wins.
            return has2D && !has3D;
        }

        // -----------------------------------------------------------------
        // Internal helpers — Rigidbody 3D
        // -----------------------------------------------------------------

        private static RigidbodyResult SnapshotRigidbody3D(GameObject go, Rigidbody? rb)
        {
            if (rb == null)
                return new RigidbodyResult { Ok = false, Error = Error.NoRigidbody3DOnGameObject(go.name) };

            return new RigidbodyResult
            {
                Ok = true,
                Target = go.name,
                Dimension = "3d",
                Mass = rb.mass,
#if UNITY_6000_0_OR_NEWER
                LinearDamping = rb.linearDamping,
                AngularDamping = rb.angularDamping,
#else
                LinearDamping = rb.drag,
                AngularDamping = rb.angularDrag,
#endif
                UseGravity = rb.useGravity,
                IsKinematic = rb.isKinematic,
                Interpolation = rb.interpolation.ToString(),
                CollisionDetection = rb.collisionDetectionMode.ToString(),
                Constraints = rb.constraints.ToString()
            };
        }

        private static RigidbodyResult ConfigureRigidbody3D(
            GameObject go,
            Rigidbody? rb,
            float? mass,
            float? drag,
            float? angularDrag,
            bool? useGravity,
            bool? isKinematic,
            string? interpolation,
            string? collisionDetection,
            string? constraints)
        {
            if (rb == null)
                return new RigidbodyResult { Ok = false, Error = Error.NoRigidbody3DOnGameObject(go.name) };

            Undo.RecordObject(rb, "Configure Rigidbody");

            var changed = new List<string>();
            if (mass.HasValue) { rb.mass = mass.Value; changed.Add("mass"); }

            if (drag.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                rb.linearDamping = drag.Value;
#else
                rb.drag = drag.Value;
#endif
                changed.Add("linearDamping");
            }

            if (angularDrag.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                rb.angularDamping = angularDrag.Value;
#else
                rb.angularDrag = angularDrag.Value;
#endif
                changed.Add("angularDamping");
            }

            if (useGravity.HasValue) { rb.useGravity = useGravity.Value; changed.Add("useGravity"); }
            if (isKinematic.HasValue) { rb.isKinematic = isKinematic.Value; changed.Add("isKinematic"); }

            if (!string.IsNullOrEmpty(interpolation))
            {
                if (!Enum.TryParse<RigidbodyInterpolation>(interpolation, true, out var interp))
                    return new RigidbodyResult { Ok = false, Error = Error.InvalidInterpolation(interpolation!) };
                rb.interpolation = interp;
                changed.Add("interpolation");
            }

            if (!string.IsNullOrEmpty(collisionDetection))
            {
                if (!Enum.TryParse<CollisionDetectionMode>(collisionDetection, true, out var mode))
                    return new RigidbodyResult { Ok = false, Error = Error.InvalidCollisionDetection3D(collisionDetection!) };
                rb.collisionDetectionMode = mode;
                changed.Add("collisionDetection");
            }

            if (!string.IsNullOrEmpty(constraints))
            {
                if (int.TryParse(constraints, out int bitmask))
                {
                    rb.constraints = (RigidbodyConstraints)bitmask;
                    changed.Add("constraints");
                }
                else if (Enum.TryParse<RigidbodyConstraints>(constraints, true, out var c))
                {
                    rb.constraints = c;
                    changed.Add("constraints");
                }
                else
                {
                    return new RigidbodyResult { Ok = false, Error = Error.InvalidConstraints3D(constraints!) };
                }
            }

            EditorUtility.SetDirty(rb);

            var snapshot = SnapshotRigidbody3D(go, rb);
            snapshot.Changed = changed;
            return snapshot;
        }

        // -----------------------------------------------------------------
        // Internal helpers — Rigidbody 2D
        // -----------------------------------------------------------------

        private static RigidbodyResult SnapshotRigidbody2D(GameObject go, Rigidbody2D? rb)
        {
            if (rb == null)
                return new RigidbodyResult { Ok = false, Error = Error.NoRigidbody2DOnGameObject(go.name) };

            return new RigidbodyResult
            {
                Ok = true,
                Target = go.name,
                Dimension = "2d",
                Mass = rb.mass,
#if UNITY_6000_0_OR_NEWER
                LinearDamping = rb.linearDamping,
                AngularDamping = rb.angularDamping,
#else
                LinearDamping = rb.drag,
                AngularDamping = rb.angularDrag,
#endif
                IsKinematic = rb.isKinematic,
                GravityScale = rb.gravityScale,
                BodyType = rb.bodyType.ToString(),
                Simulated = rb.simulated,
                CollisionDetection = rb.collisionDetectionMode.ToString(),
                Constraints = rb.constraints.ToString()
            };
        }

        private static RigidbodyResult ConfigureRigidbody2D(
            GameObject go,
            Rigidbody2D? rb,
            float? mass,
            float? drag,
            float? angularDrag,
            bool? isKinematic,
            string? collisionDetection,
            string? constraints,
            float? gravityScale,
            string? bodyType,
            bool? simulated)
        {
            if (rb == null)
                return new RigidbodyResult { Ok = false, Error = Error.NoRigidbody2DOnGameObject(go.name) };

            Undo.RecordObject(rb, "Configure Rigidbody2D");

            var changed = new List<string>();
            if (mass.HasValue) { rb.mass = mass.Value; changed.Add("mass"); }

            if (drag.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                rb.linearDamping = drag.Value;
#else
                rb.drag = drag.Value;
#endif
                changed.Add("linearDamping");
            }

            if (angularDrag.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                rb.angularDamping = angularDrag.Value;
#else
                rb.angularDrag = angularDrag.Value;
#endif
                changed.Add("angularDamping");
            }

            if (isKinematic.HasValue) { rb.isKinematic = isKinematic.Value; changed.Add("isKinematic"); }
            if (gravityScale.HasValue) { rb.gravityScale = gravityScale.Value; changed.Add("gravityScale"); }
            if (simulated.HasValue) { rb.simulated = simulated.Value; changed.Add("simulated"); }

            if (!string.IsNullOrEmpty(bodyType))
            {
                if (!Enum.TryParse<RigidbodyType2D>(bodyType, true, out var bt))
                    return new RigidbodyResult { Ok = false, Error = Error.InvalidBodyType2D(bodyType!) };
                rb.bodyType = bt;
                changed.Add("bodyType");
            }

            if (!string.IsNullOrEmpty(collisionDetection))
            {
                if (!Enum.TryParse<CollisionDetectionMode2D>(collisionDetection, true, out var mode))
                    return new RigidbodyResult { Ok = false, Error = Error.InvalidCollisionDetection2D(collisionDetection!) };
                rb.collisionDetectionMode = mode;
                changed.Add("collisionDetection");
            }

            if (!string.IsNullOrEmpty(constraints))
            {
                if (int.TryParse(constraints, out int bitmask))
                {
                    rb.constraints = (RigidbodyConstraints2D)bitmask;
                    changed.Add("constraints");
                }
                else if (Enum.TryParse<RigidbodyConstraints2D>(constraints, true, out var c))
                {
                    rb.constraints = c;
                    changed.Add("constraints");
                }
                else
                {
                    return new RigidbodyResult { Ok = false, Error = Error.InvalidConstraints2D(constraints!) };
                }
            }

            EditorUtility.SetDirty(rb);

            var snapshot = SnapshotRigidbody2D(go, rb);
            snapshot.Changed = changed;
            return snapshot;
        }
    }
}
