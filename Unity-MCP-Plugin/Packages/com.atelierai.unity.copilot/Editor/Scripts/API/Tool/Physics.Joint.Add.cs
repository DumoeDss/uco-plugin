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
        public const string PhysicsJointAddToolId = "physics-joint-add";

        [UcoTool
        (
            PhysicsJointAddToolId,
            Title = "Physics / Joint / Add",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Attach a 3D or 2D Joint component to a GameObject. " +
            "3D joints: fixed | hinge | spring | character | configurable. " +
            "2D joints: distance | fixed | friction | hinge | relative | slider | spring | target | wheel. " +
            "Auto-adds the required Rigidbody / Rigidbody2D if missing. " +
            "Use '" + PhysicsJointConfigureToolId + "' to set motors, limits, springs, and motion locks afterwards.")]
        [UcoSkillBody("Add a Joint component to a GameObject in the opened Prefab or active Scene.\n\n" +
            "## Inputs\n\n" +
            "- `target` — host GameObject. Required.\n" +
            "- `jointType` — joint kind. Case-insensitive. See description for the valid set per dimension.\n" +
            "- `dimension` — '3d' (default) or '2d'. Selects the 3D vs 2D joint family.\n" +
            "- `connectedBody` — optional second GameObject this joint connects to. When null the joint connects to 'world'.\n" +
            "- `anchor` / `connectedAnchor` — optional local-space attachment points.\n" +
            "- `axis` — 3D-only axis vector (HingeJoint / ConfigurableJoint).\n" +
            "- `autoConfigureConnectedAnchor` — when set, toggles Unity's 'Auto Configure Connected Anchor' on " +
            "joints that expose it (most 2D joints).\n\n" +
            "## Behavior\n\n" +
            "1. If the host lacks a Rigidbody (3D) / Rigidbody2D (2D), one is added automatically and a warning is logged.\n" +
            "2. If `connectedBody` is provided, its Rigidbody / Rigidbody2D is resolved and wired into the joint.\n" +
            "3. Anchors and axis are applied where supported by the joint type.\n" +
            "4. Returns a `JointAddResult` with the resolved joint type and target hierarchy path.")]
        [Description("Attach a 3D or 2D Joint component to a GameObject. " +
            "Auto-adds Rigidbody/Rigidbody2D when missing. " +
            "Use '" + PhysicsJointConfigureToolId + "' afterwards to tune motors/limits/springs.")]
        public JointAddResult AddJoint
        (
            [Description("GameObject to attach the joint to. Required.")]
            GameObjectRef target,
            [Description("Joint type. 3D: fixed/hinge/spring/character/configurable. " +
                "2D: distance/fixed/friction/hinge/relative/slider/spring/target/wheel.")]
            string jointType,
            [Description("Dimension: '3d' (default) or '2d'. Case-insensitive.")]
            string? dimension = null,
            [Description("GameObject that this joint connects to (the 'connected body'). Null = connect to world.")]
            GameObjectRef? connectedBody = null,
            [Description("Anchor (local space) where the joint attaches on the host body.")]
            Vector3? anchor = null,
            [Description("Connected anchor in the connected body's local space.")]
            Vector3? connectedAnchor = null,
            [Description("Axis (3D joints only — HingeJoint/ConfigurableJoint).")]
            Vector3? axis = null,
            [Description("Toggle 'Auto Configure Connected Anchor' (where the joint exposes it; most 2D joints).")]
            bool? autoConfigureConnectedAnchor = null
        )
        {
            if (target == null)
                return new JointAddResult { Ok = false, Error = "GameObject reference is required." };

            if (!target.IsValid(out var refErr))
                return new JointAddResult { Ok = false, Error = refErr };

            if (string.IsNullOrWhiteSpace(jointType))
                return new JointAddResult { Ok = false, Error = "jointType is required." };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new JointAddResult { Ok = false, Error = findErr ?? "GameObject not found." };

                var dim = (dimension ?? "3d").Trim().ToLowerInvariant();
                if (dim != "3d" && dim != "2d")
                    return new JointAddResult
                    {
                        Ok = false,
                        GameObjectPath = JointPathOf(go),
                        Error = $"Unknown dimension '{dimension}'. Expected '3d' or '2d'."
                    };

                var jt = jointType.Trim().ToLowerInvariant();
                var is2D = dim == "2d";

                // --- 1) Ensure the host has a Rigidbody / Rigidbody2D ---
                if (is2D)
                {
                    if (go.GetComponent<Rigidbody2D>() == null)
                    {
                        Undo.AddComponent<Rigidbody2D>(go);
                        Debug.LogWarning($"[physics-joint-add] Auto-added Rigidbody2D to '{go.name}' (required for 2D joints).");
                    }
                }
                else
                {
                    if (go.GetComponent<Rigidbody>() == null)
                    {
                        Undo.AddComponent<Rigidbody>(go);
                        Debug.LogWarning($"[physics-joint-add] Auto-added Rigidbody to '{go.name}' (required for 3D joints).");
                    }
                }

                // --- 2) Resolve the connected body GameObject (optional) ---
                GameObject? cbGo = null;
                if (connectedBody != null)
                {
                    if (!connectedBody.IsValid(out var cbValidErr))
                        return new JointAddResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = $"connectedBody is invalid: {cbValidErr}"
                        };

                    cbGo = connectedBody.FindGameObject(out var cbErr);
                    if (cbErr != null || cbGo == null)
                        return new JointAddResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = $"connectedBody could not be resolved: {cbErr ?? "GameObject not found."}"
                        };
                }

                // --- 3) Add the joint component ---
                Component? joint = null;
                string resolvedType;

                if (is2D)
                {
                    var t2 = Resolve2DJointType(jt);
                    if (t2 == null)
                        return new JointAddResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = UnknownJointTypeError(jointType)
                        };
                    joint = Undo.AddComponent(go, t2);
                    resolvedType = t2.Name;
                }
                else
                {
                    var t3 = Resolve3DJointType(jt);
                    if (t3 == null)
                        return new JointAddResult
                        {
                            Ok = false,
                            GameObjectPath = JointPathOf(go),
                            Error = UnknownJointTypeError(jointType)
                        };
                    joint = Undo.AddComponent(go, t3);
                    resolvedType = t3.Name;
                }

                if (joint == null)
                    return new JointAddResult
                    {
                        Ok = false,
                        GameObjectPath = JointPathOf(go),
                        JointType = resolvedType,
                        Error = $"Failed to add component '{resolvedType}' to '{go.name}'."
                    };

                // --- 4) Wire connected body, anchors, axis, autoConfigure ---
                if (is2D)
                {
                    ApplyAddOptions2D((AnchoredJoint2D)joint, cbGo, anchor, connectedAnchor, autoConfigureConnectedAnchor);
                }
                else
                {
                    ApplyAddOptions3D((Joint)joint, cbGo, anchor, connectedAnchor, axis, autoConfigureConnectedAnchor);
                }

                EditorUtility.SetDirty(joint);
                EditorUtility.SetDirty(go);
                EditorUtils.RepaintAllEditorWindows();

                return new JointAddResult
                {
                    Ok = true,
                    GameObjectPath = JointPathOf(go),
                    JointType = resolvedType
                };
            });
        }

        // ---------------------------------------------------------------
        // Joint-type lookup helpers (shared by Add / Configure / Remove)
        // ---------------------------------------------------------------

        /// <summary>
        /// Resolve a lower-case 3D joint-type alias to its concrete <see cref="Joint"/>-derived type.
        /// Returns null when the alias is not recognized.
        /// </summary>
        internal static Type? Resolve3DJointType(string lowerAlias) => lowerAlias switch
        {
            "fixed" => typeof(FixedJoint),
            "hinge" => typeof(HingeJoint),
            "spring" => typeof(SpringJoint),
            "character" => typeof(CharacterJoint),
            "configurable" => typeof(ConfigurableJoint),
            _ => null
        };

        /// <summary>
        /// Resolve a lower-case 2D joint-type alias to its concrete <see cref="Joint2D"/>-derived type.
        /// Returns null when the alias is not recognized.
        /// </summary>
        internal static Type? Resolve2DJointType(string lowerAlias) => lowerAlias switch
        {
            "distance" => typeof(DistanceJoint2D),
            "fixed" => typeof(FixedJoint2D),
            "friction" => typeof(FrictionJoint2D),
            "hinge" => typeof(HingeJoint2D),
            "relative" => typeof(RelativeJoint2D),
            "slider" => typeof(SliderJoint2D),
            "spring" => typeof(SpringJoint2D),
            "target" => typeof(TargetJoint2D),
            "wheel" => typeof(WheelJoint2D),
            _ => null
        };

        /// <summary>
        /// Standard error message for an unknown jointType alias. Mentions the full valid set so the LLM can self-correct.
        /// </summary>
        internal static string UnknownJointTypeError(string given) =>
            $"Unknown joint type: '{given}'. " +
            "Valid 3D: fixed,hinge,spring,character,configurable. " +
            "Valid 2D: distance,fixed,friction,hinge,relative,slider,spring,target,wheel.";

        /// <summary>
        /// Slash-separated hierarchy path of a GameObject, used in result DTOs. Uniquely named to avoid colliding
        /// with helpers declared in other Tool_Physics partials.
        /// </summary>
        internal static string JointPathOf(GameObject? go)
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

        // ---------------------------------------------------------------
        // Wire-up helpers
        // ---------------------------------------------------------------

        private static void ApplyAddOptions3D(
            Joint joint,
            GameObject? cbGo,
            Vector3? anchor,
            Vector3? connectedAnchor,
            Vector3? axis,
            bool? autoConfigureConnectedAnchor)
        {
            if (cbGo != null)
            {
                var rb = cbGo.GetComponent<Rigidbody>();
                if (rb == null)
                    rb = Undo.AddComponent<Rigidbody>(cbGo);
                joint.connectedBody = rb;
            }

            if (anchor.HasValue)
                joint.anchor = anchor.Value;

            if (connectedAnchor.HasValue)
                joint.connectedAnchor = connectedAnchor.Value;

            if (axis.HasValue)
            {
                switch (joint)
                {
                    case HingeJoint hj: hj.axis = axis.Value; break;
                    case ConfigurableJoint cj: cj.axis = axis.Value; break;
                    case CharacterJoint chj: chj.axis = axis.Value; break;
                    default: /* FixedJoint / SpringJoint have no 'axis' */ break;
                }
            }

            if (autoConfigureConnectedAnchor.HasValue && joint is ConfigurableJoint conf)
                conf.autoConfigureConnectedAnchor = autoConfigureConnectedAnchor.Value;
        }

        private static void ApplyAddOptions2D(
            AnchoredJoint2D joint,
            GameObject? cbGo,
            Vector3? anchor,
            Vector3? connectedAnchor,
            bool? autoConfigureConnectedAnchor)
        {
            if (cbGo != null)
            {
                var rb = cbGo.GetComponent<Rigidbody2D>();
                if (rb == null)
                    rb = Undo.AddComponent<Rigidbody2D>(cbGo);
                joint.connectedBody = rb;
            }

            if (anchor.HasValue)
                joint.anchor = new Vector2(anchor.Value.x, anchor.Value.y);

            if (connectedAnchor.HasValue)
                joint.connectedAnchor = new Vector2(connectedAnchor.Value.x, connectedAnchor.Value.y);

            if (autoConfigureConnectedAnchor.HasValue)
                joint.autoConfigureConnectedAnchor = autoConfigureConnectedAnchor.Value;
        }
    }

    // -------------------------------------------------------------------
    // DTOs declared at namespace level (not nested in Tool_Physics) so the
    // joint-tool partials never collide with sibling Physics partials.
    // -------------------------------------------------------------------

    public class JointAddResult
    {
        [Description("True when the joint was successfully added.")]
        public bool Ok { get; set; }

        [Description("Hierarchy path of the target GameObject, when resolved.")]
        public string? GameObjectPath { get; set; }

        [Description("Resolved concrete joint type name (e.g. 'HingeJoint', 'SpringJoint2D').")]
        public string? JointType { get; set; }

        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }
    }
}
