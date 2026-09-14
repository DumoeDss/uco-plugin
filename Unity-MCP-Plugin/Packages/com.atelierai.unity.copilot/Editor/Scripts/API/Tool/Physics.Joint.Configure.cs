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
using Component = UnityEngine.Component;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Physics
    {
        public const string PhysicsJointConfigureToolId = "physics-joint-configure";

        [McpPluginTool
        (
            PhysicsJointConfigureToolId,
            Title = "Physics / Joint / Configure",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Configure an existing 3D or 2D Joint on a GameObject — anchors, connected body, " +
            "axis, break thresholds, motor, limits, springs, and (ConfigurableJoint) per-axis motion locks. " +
            "Only the supplied (non-null) fields are written; everything else is left as-is. " +
            "Use '" + PhysicsJointAddToolId + "' to attach the joint first.")]
        [McpPluginSkillBody("Configure an existing Joint on a GameObject. Only non-null parameters are written.\n\n" +
            "## Joint targeting\n\n" +
            "When multiple joints live on the same GameObject, use `jointIndex` (0-based, default 0) and/or `jointType` " +
            "(case-insensitive alias) to pick the right one. `jointType` is matched against the joint's concrete type " +
            "name (e.g. 'hinge' matches 'HingeJoint' or 'HingeJoint2D' depending on `dimension`).\n\n" +
            "## Field groups\n\n" +
            "- **Common (3D & 2D)** — connectedBody, anchor, connectedAnchor, axis (3D only), breakForce/breakTorque (3D).\n" +
            "- **Hinge** — useMotor, motorTargetVelocity, motorForce (3D) / motorMaxTorque (2D), useLimits, limitMin, " +
            "limitMax, useSpring, springForce, springDamper.\n" +
            "- **Spring (3D SpringJoint / 2D SpringJoint2D)** — spring, damper, minDistance, maxDistance, tolerance, " +
            "distance (2D), frequency (2D), dampingRatio (2D).\n" +
            "- **Distance/Slider (2D)** — distance, frequency, dampingRatio.\n" +
            "- **Wheel (2D)** — wheelDamping, wheelFrequency, useMotor, motorTargetVelocity, motorMaxTorque.\n" +
            "- **ConfigurableJoint (3D)** — xMotion / yMotion / zMotion / angularXMotion / angularYMotion / " +
            "angularZMotion (each one of Locked / Limited / Free, case-insensitive).\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Returns a `JointConfigureResult` with `AppliedFields` listing every field " +
            "that was written (so the caller can confirm which inputs took effect). Unknown enum strings produce an " +
            "error and abort before any field is written for that joint.")]
        [Description("Configure an existing 3D or 2D Joint on a GameObject. " +
            "Only provided (non-null) parameters are written. " +
            "Use 'jointIndex' + 'jointType' to disambiguate multi-joint GameObjects.")]
        public JointConfigureResult ConfigureJoint
        (
            [Description("GameObject hosting the joint. Required.")]
            GameObjectRef target,
            [Description("Index of the joint on the GameObject (0-based, in component-order). Defaults to 0.")]
            int? jointIndex = null,
            [Description("Optional joint-type filter (case-insensitive alias). " +
                "3D: fixed/hinge/spring/character/configurable. " +
                "2D: distance/fixed/friction/hinge/relative/slider/spring/target/wheel.")]
            string? jointType = null,

            // ---- Common ----
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null,
            [Description("GameObject providing the connected Rigidbody / Rigidbody2D. Pass an unresolved ref to leave unchanged.")]
            GameObjectRef? connectedBody = null,
            [Description("Anchor (local space).")]
            Vector3? anchor = null,
            [Description("Connected anchor (connected body's local space).")]
            Vector3? connectedAnchor = null,
            [Description("Axis (3D joints only — HingeJoint/ConfigurableJoint/CharacterJoint).")]
            Vector3? axis = null,
            [Description("3D-only: force threshold above which the joint breaks. Float.PositiveInfinity = unbreakable.")]
            float? breakForce = null,
            [Description("3D-only: torque threshold above which the joint breaks.")]
            float? breakTorque = null,

            // ---- HingeJoint / HingeJoint2D / WheelJoint2D ----
            [Description("Hinge/Wheel: enable the motor.")]
            bool? useMotor = null,
            [Description("Hinge/Wheel motor: target angular velocity.")]
            float? motorTargetVelocity = null,
            [Description("3D HingeJoint motor.force.")]
            float? motorForce = null,
            [Description("2D HingeJoint / WheelJoint motor.maxMotorTorque.")]
            float? motorMaxTorque = null,
            [Description("Hinge: enable angular limits.")]
            bool? useLimits = null,
            [Description("Hinge limit minimum (degrees).")]
            float? limitMin = null,
            [Description("Hinge limit maximum (degrees).")]
            float? limitMax = null,
            [Description("Hinge: enable the spring.")]
            bool? useSpring = null,
            [Description("Hinge spring stiffness (3D 'spring' / 2D analogous field).")]
            float? springForce = null,
            [Description("Hinge spring damper.")]
            float? springDamper = null,

            // ---- SpringJoint (3D) / SpringJoint2D (2D) ----
            [Description("SpringJoint(2D): spring stiffness.")]
            float? spring = null,
            [Description("SpringJoint(2D): damper coefficient.")]
            float? damper = null,
            [Description("3D SpringJoint: minimum distance.")]
            float? minDistance = null,
            [Description("3D SpringJoint: maximum distance.")]
            float? maxDistance = null,
            [Description("3D SpringJoint: tolerance.")]
            float? tolerance = null,

            // ---- DistanceJoint2D / SliderJoint2D / SpringJoint2D ----
            [Description("DistanceJoint2D / SliderJoint2D / SpringJoint2D: target distance.")]
            float? distance = null,
            [Description("2D spring-style joints: oscillation frequency (Hz).")]
            float? frequency = null,
            [Description("2D spring-style joints: damping ratio.")]
            float? dampingRatio = null,

            // ---- WheelJoint2D suspension ----
            [Description("WheelJoint2D suspension: damping ratio.")]
            float? wheelDamping = null,
            [Description("WheelJoint2D suspension: frequency (Hz).")]
            float? wheelFrequency = null,

            // ---- ConfigurableJoint ----
            [Description("ConfigurableJoint xMotion: Locked / Limited / Free (case-insensitive).")]
            string? xMotion = null,
            [Description("ConfigurableJoint yMotion: Locked / Limited / Free.")]
            string? yMotion = null,
            [Description("ConfigurableJoint zMotion: Locked / Limited / Free.")]
            string? zMotion = null,
            [Description("ConfigurableJoint angularXMotion: Locked / Limited / Free.")]
            string? angularXMotion = null,
            [Description("ConfigurableJoint angularYMotion: Locked / Limited / Free.")]
            string? angularYMotion = null,
            [Description("ConfigurableJoint angularZMotion: Locked / Limited / Free.")]
            string? angularZMotion = null
        )
        {
            if (target == null)
                return new JointConfigureResult { Ok = false, Error = "GameObject reference is required." };

            if (!target.IsValid(out var refErr))
                return new JointConfigureResult { Ok = false, Error = refErr };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new JointConfigureResult { Ok = false, Error = findErr ?? "GameObject not found." };

                var dim = (dimension ?? "3d").Trim().ToLowerInvariant();
                if (dim != "3d" && dim != "2d")
                    return new JointConfigureResult
                    {
                        Ok = false,
                        GameObjectPath = JointPathOf(go),
                        Error = $"Unknown dimension '{dimension}'. Expected '3d' or '2d'."
                    };
                var is2D = dim == "2d";

                // --- 1) Locate the target joint on this GameObject ---
                Component? joint;
                string resolvedTypeName;
                if (is2D)
                {
                    joint = FindJoint2D(go, jointType, jointIndex, out var err2);
                    if (joint == null)
                        return new JointConfigureResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = err2
                        };
                    resolvedTypeName = joint.GetType().Name;
                }
                else
                {
                    joint = FindJoint3D(go, jointType, jointIndex, out var err3);
                    if (joint == null)
                        return new JointConfigureResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = err3
                        };
                    resolvedTypeName = joint.GetType().Name;
                }

                Undo.RecordObject(joint, "Configure Joint");
                var applied = new List<string>();

                // --- 2) Apply per-dimension ---
                if (is2D)
                {
                    var err = ApplyConfigure2D(
                        (Joint2D)joint, applied,
                        connectedBody, anchor, connectedAnchor,
                        useMotor, motorTargetVelocity, motorMaxTorque,
                        useLimits, limitMin, limitMax,
                        useSpring, springForce, springDamper,
                        spring, damper,
                        distance, frequency, dampingRatio,
                        wheelDamping, wheelFrequency);
                    if (err != null)
                        return new JointConfigureResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            JointType = resolvedTypeName,
                            Error = err,
                            AppliedFields = applied.ToArray()
                        };
                }
                else
                {
                    var err = ApplyConfigure3D(
                        (Joint)joint, applied,
                        connectedBody, anchor, connectedAnchor, axis,
                        breakForce, breakTorque,
                        useMotor, motorTargetVelocity, motorForce,
                        useLimits, limitMin, limitMax,
                        useSpring, springForce, springDamper,
                        spring, damper, minDistance, maxDistance, tolerance,
                        xMotion, yMotion, zMotion,
                        angularXMotion, angularYMotion, angularZMotion);
                    if (err != null)
                        return new JointConfigureResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            JointType = resolvedTypeName,
                            Error = err,
                            AppliedFields = applied.ToArray()
                        };
                }

                EditorUtility.SetDirty(joint);
                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new JointConfigureResult
                {
                    Ok = true,
                    GameObjectPath = JointPathOf(go),
                    JointType = resolvedTypeName,
                    AppliedFields = applied.ToArray()
                };
            });
        }

        // ===================================================================
        // 3D joint configuration
        // ===================================================================

        private static string? ApplyConfigure3D(
            Joint joint,
            List<string> applied,
            GameObjectRef? connectedBodyRef,
            Vector3? anchor,
            Vector3? connectedAnchor,
            Vector3? axis,
            float? breakForce,
            float? breakTorque,
            bool? useMotor,
            float? motorTargetVelocity,
            float? motorForce,
            bool? useLimits,
            float? limitMin,
            float? limitMax,
            bool? useSpring,
            float? springForce,
            float? springDamper,
            float? spring,
            float? damper,
            float? minDistance,
            float? maxDistance,
            float? tolerance,
            string? xMotion,
            string? yMotion,
            string? zMotion,
            string? angularXMotion,
            string? angularYMotion,
            string? angularZMotion)
        {
            // -- connectedBody --
            if (connectedBodyRef != null && connectedBodyRef.IsValid(out _))
            {
                var cbGo = connectedBodyRef.FindGameObject(out var cbErr);
                if (cbErr != null || cbGo == null)
                    return $"connectedBody could not be resolved: {cbErr ?? "GameObject not found."}";

                var rb = cbGo.GetComponent<Rigidbody>();
                if (rb == null)
                    rb = Undo.AddComponent<Rigidbody>(cbGo);
                joint.connectedBody = rb;
                applied.Add("connectedBody");
            }

            // -- anchors --
            if (anchor.HasValue) { joint.anchor = anchor.Value; applied.Add("anchor"); }
            if (connectedAnchor.HasValue) { joint.connectedAnchor = connectedAnchor.Value; applied.Add("connectedAnchor"); }

            // -- axis (subset of joint types) --
            if (axis.HasValue)
            {
                switch (joint)
                {
                    case HingeJoint hj: hj.axis = axis.Value; applied.Add("axis"); break;
                    case ConfigurableJoint cj0: cj0.axis = axis.Value; applied.Add("axis"); break;
                    case CharacterJoint chj: chj.axis = axis.Value; applied.Add("axis"); break;
                    default: break; // FixedJoint / SpringJoint silently skip
                }
            }

            if (breakForce.HasValue) { joint.breakForce = breakForce.Value; applied.Add("breakForce"); }
            if (breakTorque.HasValue) { joint.breakTorque = breakTorque.Value; applied.Add("breakTorque"); }

            // -- HingeJoint specifics --
            if (joint is HingeJoint hinge)
            {
                if (useMotor.HasValue) { hinge.useMotor = useMotor.Value; applied.Add("useMotor"); }
                if (motorTargetVelocity.HasValue || motorForce.HasValue)
                {
                    var motor = hinge.motor;
                    if (motorTargetVelocity.HasValue) { motor.targetVelocity = motorTargetVelocity.Value; applied.Add("motor.targetVelocity"); }
                    if (motorForce.HasValue) { motor.force = motorForce.Value; applied.Add("motor.force"); }
                    hinge.motor = motor;
                }
                if (useLimits.HasValue) { hinge.useLimits = useLimits.Value; applied.Add("useLimits"); }
                if (limitMin.HasValue || limitMax.HasValue)
                {
                    var limits = hinge.limits;
                    if (limitMin.HasValue) { limits.min = limitMin.Value; applied.Add("limits.min"); }
                    if (limitMax.HasValue) { limits.max = limitMax.Value; applied.Add("limits.max"); }
                    hinge.limits = limits;
                }
                if (useSpring.HasValue) { hinge.useSpring = useSpring.Value; applied.Add("useSpring"); }
                if (springForce.HasValue || springDamper.HasValue)
                {
                    var s = hinge.spring;
                    if (springForce.HasValue) { s.spring = springForce.Value; applied.Add("spring.spring"); }
                    if (springDamper.HasValue) { s.damper = springDamper.Value; applied.Add("spring.damper"); }
                    hinge.spring = s;
                }
            }

            // -- SpringJoint specifics --
            if (joint is SpringJoint sj)
            {
                if (spring.HasValue) { sj.spring = spring.Value; applied.Add("spring"); }
                if (damper.HasValue) { sj.damper = damper.Value; applied.Add("damper"); }
                if (minDistance.HasValue) { sj.minDistance = minDistance.Value; applied.Add("minDistance"); }
                if (maxDistance.HasValue) { sj.maxDistance = maxDistance.Value; applied.Add("maxDistance"); }
                if (tolerance.HasValue) { sj.tolerance = tolerance.Value; applied.Add("tolerance"); }
            }

            // -- ConfigurableJoint motion locks --
            if (joint is ConfigurableJoint cj)
            {
                var motionErr = ApplyConfigurableMotion(cj, applied,
                    xMotion, yMotion, zMotion,
                    angularXMotion, angularYMotion, angularZMotion);
                if (motionErr != null) return motionErr;
            }

            return null;
        }

        /// <summary>
        /// Apply x/y/z + angularX/Y/Z motion overrides to a ConfigurableJoint. Returns an error string when any of the
        /// provided enum aliases cannot be parsed.
        /// </summary>
        private static string? ApplyConfigurableMotion(
            ConfigurableJoint cj,
            List<string> applied,
            string? xMotion,
            string? yMotion,
            string? zMotion,
            string? angularXMotion,
            string? angularYMotion,
            string? angularZMotion)
        {
            if (!string.IsNullOrEmpty(xMotion))
            {
                if (!TryParseMotion(xMotion, out var m)) return UnknownMotionError(nameof(xMotion), xMotion!);
                cj.xMotion = m; applied.Add("xMotion");
            }
            if (!string.IsNullOrEmpty(yMotion))
            {
                if (!TryParseMotion(yMotion, out var m)) return UnknownMotionError(nameof(yMotion), yMotion!);
                cj.yMotion = m; applied.Add("yMotion");
            }
            if (!string.IsNullOrEmpty(zMotion))
            {
                if (!TryParseMotion(zMotion, out var m)) return UnknownMotionError(nameof(zMotion), zMotion!);
                cj.zMotion = m; applied.Add("zMotion");
            }
            if (!string.IsNullOrEmpty(angularXMotion))
            {
                if (!TryParseMotion(angularXMotion, out var m)) return UnknownMotionError(nameof(angularXMotion), angularXMotion!);
                cj.angularXMotion = m; applied.Add("angularXMotion");
            }
            if (!string.IsNullOrEmpty(angularYMotion))
            {
                if (!TryParseMotion(angularYMotion, out var m)) return UnknownMotionError(nameof(angularYMotion), angularYMotion!);
                cj.angularYMotion = m; applied.Add("angularYMotion");
            }
            if (!string.IsNullOrEmpty(angularZMotion))
            {
                if (!TryParseMotion(angularZMotion, out var m)) return UnknownMotionError(nameof(angularZMotion), angularZMotion!);
                cj.angularZMotion = m; applied.Add("angularZMotion");
            }
            return null;
        }

        private static bool TryParseMotion(string s, out ConfigurableJointMotion m)
            => Enum.TryParse(s, ignoreCase: true, out m);

        private static string UnknownMotionError(string field, string value) =>
            $"Unknown ConfigurableJointMotion value '{value}' for '{field}'. Expected one of: Locked, Limited, Free.";

        // ===================================================================
        // 2D joint configuration
        // ===================================================================

        private static string? ApplyConfigure2D(
            Joint2D joint,
            List<string> applied,
            GameObjectRef? connectedBodyRef,
            Vector3? anchor,
            Vector3? connectedAnchor,
            bool? useMotor,
            float? motorTargetVelocity,
            float? motorMaxTorque,
            bool? useLimits,
            float? limitMin,
            float? limitMax,
            bool? useSpring,
            float? springForce,
            float? springDamper,
            float? spring,
            float? damper,
            float? distance,
            float? frequency,
            float? dampingRatio,
            float? wheelDamping,
            float? wheelFrequency)
        {
            // -- connectedBody --
            if (connectedBodyRef != null && connectedBodyRef.IsValid(out _))
            {
                var cbGo = connectedBodyRef.FindGameObject(out var cbErr);
                if (cbErr != null || cbGo == null)
                    return $"connectedBody could not be resolved: {cbErr ?? "GameObject not found."}";

                var rb = cbGo.GetComponent<Rigidbody2D>();
                if (rb == null)
                    rb = Undo.AddComponent<Rigidbody2D>(cbGo);
                joint.connectedBody = rb;
                applied.Add("connectedBody");
            }

            // -- anchors (AnchoredJoint2D base) --
            if (joint is AnchoredJoint2D anchored)
            {
                if (anchor.HasValue)
                {
                    anchored.anchor = new Vector2(anchor.Value.x, anchor.Value.y);
                    applied.Add("anchor");
                }
                if (connectedAnchor.HasValue)
                {
                    anchored.connectedAnchor = new Vector2(connectedAnchor.Value.x, connectedAnchor.Value.y);
                    applied.Add("connectedAnchor");
                }
            }

            // -- DistanceJoint2D --
            if (joint is DistanceJoint2D dj2)
            {
                if (distance.HasValue) { dj2.distance = distance.Value; applied.Add("distance"); }
            }

            // -- SpringJoint2D --
            // Note: SpringJoint2D exposes 'frequency'/'dampingRatio' rather than the 3D-style 'spring'/'damper';
            // 'spring'/'damper' params are intentionally ignored here — use frequency/dampingRatio instead.
            if (joint is SpringJoint2D sj2)
            {
                if (distance.HasValue) { sj2.distance = distance.Value; applied.Add("distance"); }
                if (frequency.HasValue) { sj2.frequency = frequency.Value; applied.Add("frequency"); }
                if (dampingRatio.HasValue) { sj2.dampingRatio = dampingRatio.Value; applied.Add("dampingRatio"); }
            }

            // -- SliderJoint2D --
            if (joint is SliderJoint2D sl2)
            {
                if (useMotor.HasValue) { sl2.useMotor = useMotor.Value; applied.Add("useMotor"); }
                if (motorTargetVelocity.HasValue || motorMaxTorque.HasValue)
                {
                    var motor = sl2.motor;
                    if (motorTargetVelocity.HasValue) { motor.motorSpeed = motorTargetVelocity.Value; applied.Add("motor.motorSpeed"); }
                    if (motorMaxTorque.HasValue) { motor.maxMotorTorque = motorMaxTorque.Value; applied.Add("motor.maxMotorTorque"); }
                    sl2.motor = motor;
                }
                if (useLimits.HasValue) { sl2.useLimits = useLimits.Value; applied.Add("useLimits"); }
                if (limitMin.HasValue || limitMax.HasValue)
                {
                    var lim = sl2.limits;
                    if (limitMin.HasValue) { lim.min = limitMin.Value; applied.Add("limits.min"); }
                    if (limitMax.HasValue) { lim.max = limitMax.Value; applied.Add("limits.max"); }
                    sl2.limits = lim;
                }
            }

            // -- HingeJoint2D --
            if (joint is HingeJoint2D hj2)
            {
                if (useMotor.HasValue) { hj2.useMotor = useMotor.Value; applied.Add("useMotor"); }
                if (motorTargetVelocity.HasValue || motorMaxTorque.HasValue)
                {
                    var motor = hj2.motor;
                    if (motorTargetVelocity.HasValue) { motor.motorSpeed = motorTargetVelocity.Value; applied.Add("motor.motorSpeed"); }
                    if (motorMaxTorque.HasValue) { motor.maxMotorTorque = motorMaxTorque.Value; applied.Add("motor.maxMotorTorque"); }
                    hj2.motor = motor;
                }
                if (useLimits.HasValue) { hj2.useLimits = useLimits.Value; applied.Add("useLimits"); }
                if (limitMin.HasValue || limitMax.HasValue)
                {
                    var lim = hj2.limits;
                    if (limitMin.HasValue) { lim.min = limitMin.Value; applied.Add("limits.min"); }
                    if (limitMax.HasValue) { lim.max = limitMax.Value; applied.Add("limits.max"); }
                    hj2.limits = lim;
                }
                // HingeJoint2D has no built-in spring struct; fall through to ignore useSpring/springForce/springDamper.
                if (useSpring.HasValue) applied.Add("useSpring(ignored — HingeJoint2D has no spring struct)");
            }

            // -- WheelJoint2D --
            if (joint is WheelJoint2D wj2)
            {
                if (useMotor.HasValue) { wj2.useMotor = useMotor.Value; applied.Add("useMotor"); }
                if (motorTargetVelocity.HasValue || motorMaxTorque.HasValue)
                {
                    var motor = wj2.motor;
                    if (motorTargetVelocity.HasValue) { motor.motorSpeed = motorTargetVelocity.Value; applied.Add("motor.motorSpeed"); }
                    if (motorMaxTorque.HasValue) { motor.maxMotorTorque = motorMaxTorque.Value; applied.Add("motor.maxMotorTorque"); }
                    wj2.motor = motor;
                }
                if (wheelDamping.HasValue || wheelFrequency.HasValue)
                {
                    var susp = wj2.suspension;
                    if (wheelDamping.HasValue) { susp.dampingRatio = wheelDamping.Value; applied.Add("suspension.dampingRatio"); }
                    if (wheelFrequency.HasValue) { susp.frequency = wheelFrequency.Value; applied.Add("suspension.frequency"); }
                    wj2.suspension = susp;
                }
            }

            // -- FrictionJoint2D --
            if (joint is FrictionJoint2D fj2)
            {
                // Friction joint accepts motorMaxTorque as 'maxTorque' and motorTargetVelocity as 'maxForce' analogue;
                // expose conservatively — only direct ones to avoid surprising the LLM.
                if (motorMaxTorque.HasValue) { fj2.maxTorque = motorMaxTorque.Value; applied.Add("maxTorque"); }
            }

            // -- RelativeJoint2D --
            if (joint is RelativeJoint2D rj2)
            {
                if (motorMaxTorque.HasValue) { rj2.maxTorque = motorMaxTorque.Value; applied.Add("maxTorque"); }
            }

            // -- TargetJoint2D (no anchored base — needs its own anchor handling) --
            if (joint is TargetJoint2D tj2)
            {
                if (anchor.HasValue)
                {
                    tj2.anchor = new Vector2(anchor.Value.x, anchor.Value.y);
                    if (!applied.Contains("anchor")) applied.Add("anchor");
                }
                if (frequency.HasValue) { tj2.frequency = frequency.Value; if (!applied.Contains("frequency")) applied.Add("frequency"); }
                if (dampingRatio.HasValue) { tj2.dampingRatio = dampingRatio.Value; if (!applied.Contains("dampingRatio")) applied.Add("dampingRatio"); }
                if (motorMaxTorque.HasValue) { tj2.maxForce = motorMaxTorque.Value; applied.Add("maxForce"); }
            }

            // FixedJoint2D has no additional knobs beyond the AnchoredJoint2D base + break thresholds.
            // 2D break thresholds (breakForce/breakTorque) are not exposed in this call to keep parity with the
            // 3D-only description — use 'gameobject-component-modify' for advanced 2D break-threshold cases.

            return null;
        }

        // ===================================================================
        // Find-by-index / type helpers
        // ===================================================================

        private static Joint? FindJoint3D(GameObject go, string? typeFilter, int? index, out string? err)
        {
            var all = go.GetComponents<Joint>();
            return FindJointByFilter(all, typeFilter, index, "3D", out err);
        }

        private static Joint2D? FindJoint2D(GameObject go, string? typeFilter, int? index, out string? err)
        {
            var all = go.GetComponents<Joint2D>();
            return FindJointByFilter(all, typeFilter, index, "2D", out err);
        }

        /// <summary>
        /// Pick a joint out of the component list using an optional case-insensitive type alias and an optional index.
        /// </summary>
        private static T? FindJointByFilter<T>(T[] all, string? typeFilter, int? index, string dimLabel, out string? err)
            where T : Component
        {
            err = null;
            if (all == null || all.Length == 0)
            {
                err = $"No {dimLabel} Joint components found on the GameObject.";
                return null;
            }

            IEnumerable<T> candidates = all;
            if (!string.IsNullOrEmpty(typeFilter))
            {
                var filter = typeFilter!.Trim().ToLowerInvariant();
                var matches = new List<T>();
                foreach (var c in all)
                {
                    var name = c.GetType().Name; // e.g. "HingeJoint" / "HingeJoint2D"
                    if (name.ToLowerInvariant().StartsWith(filter))
                        matches.Add(c);
                }
                if (matches.Count == 0)
                {
                    err = $"No {dimLabel} Joint of type '{typeFilter}' found on the GameObject. " +
                          $"Present types: {string.Join(", ", JointTypeNames(all))}.";
                    return null;
                }
                candidates = matches;
            }

            var list = new List<T>(candidates);
            var idx = index ?? 0;
            if (idx < 0 || idx >= list.Count)
            {
                err = $"jointIndex {idx} out of range (have {list.Count} matching {dimLabel} joint(s)).";
                return null;
            }
            return list[idx];
        }

        private static IEnumerable<string> JointTypeNames<T>(T[] joints) where T : Component
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

    public class JointConfigureResult
    {
        [Description("True when the configure call wrote at least one field successfully.")]
        public bool Ok { get; set; }

        [Description("Hierarchy path of the target GameObject, when resolved.")]
        public string? GameObjectPath { get; set; }

        [Description("Concrete joint type that was configured (e.g. 'HingeJoint2D', 'ConfigurableJoint').")]
        public string? JointType { get; set; }

        [Description("Names of all fields that were actually written by this call (so the caller can verify which " +
            "inputs took effect on this joint type).")]
        public string[] AppliedFields { get; set; } = Array.Empty<string>();

        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }
    }
}
