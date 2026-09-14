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
using com.AtelierAI.Unity.Copilot.Runtime.Extensions;
using AIGD;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Physics
    {
        /// <summary>
        /// Result of a force / torque / explosion / impulse application.
        /// </summary>
        public class ForceResult
        {
            [Description("True when the force was applied successfully.")]
            public bool Ok { get; set; }

            [Description("Slash-separated hierarchy path of the target GameObject. Null when not resolved.")]
            public string? GameObjectPath { get; set; }

            [Description("Action that was applied: 'force' | 'torque' | 'explosion' | 'impulse'.")]
            public string Action { get; set; } = "";

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        public const string PhysicsForceApplyToolId = "physics-force-apply";

        [UcoTool
        (
            PhysicsForceApplyToolId,
            Title = "Physics / Force / Apply",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Apply a force, torque, explosion, or impulse to a Rigidbody (3D) or Rigidbody2D (2D). " +
            "Dispatches to `Rigidbody.AddForce`, `Rigidbody.AddTorque`, `Rigidbody.AddExplosionForce`, or the impulse " +
            "variant of `AddForce` based on `action`. Returns Ok=false with `Error` set when the target lacks the " +
            "appropriate rigidbody. Explosions are only supported in 3D physics.")]
        [UcoSkillBody("Apply a physics force to a rigidbody.\n\n" +
            "## Inputs\n\n" +
            "- `target` — GameObject hosting the Rigidbody / Rigidbody2D.\n" +
            "- `action` — `'force'` | `'torque'` | `'explosion'` | `'impulse'` (case-insensitive).\n" +
            "- `force` — used by `force` and `impulse` actions.\n" +
            "- `torque` — used by `torque`. In 2D only `torque.z` (or `force.z` fallback) is honored.\n" +
            "- `explosionForce`, `explosionPosition`, `explosionRadius` — required for `explosion` (3D only).\n" +
            "- `mode` — ForceMode for 3D: `'Force'` (default), `'Impulse'`, `'VelocityChange'`, `'Acceleration'`. " +
            "For 2D the analogous ForceMode2D is used; only `'Force'` and `'Impulse'` are valid.\n" +
            "- `dimension` — `'3d'` (default) or `'2d'`.\n\n" +
            "## Notes\n\n" +
            "Best invoked during Play Mode. In Edit Mode the rigidbody simulation is paused so the applied force " +
            "only takes effect after a 'physics-simulate-step' call.")]
        [Description("Apply a force / torque / explosion / impulse to a Rigidbody (3D or 2D).")]
        public ForceResult ApplyForce
        (
            [Description("Target GameObject hosting the Rigidbody / Rigidbody2D.")]
            GameObjectRef target,
            [Description("Action: 'force' | 'torque' | 'explosion' | 'impulse'.")]
            string action,
            [Description("Force vector. Used by 'force' and 'impulse' actions.")]
            Vector3? force = null,
            [Description("Torque vector. Used by 'torque' action. In 2D only the Z component is honored.")]
            Vector3? torque = null,
            [Description("Explosion magnitude. Used by 'explosion' action.")]
            float? explosionForce = null,
            [Description("Explosion origin in world space. Used by 'explosion' action.")]
            Vector3? explosionPosition = null,
            [Description("Explosion radius. Used by 'explosion' action.")]
            float? explosionRadius = null,
            [Description("ForceMode: 'Force' (default) | 'Impulse' | 'VelocityChange' | 'Acceleration'. 2D only supports 'Force' and 'Impulse'.")]
            string? mode = null,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null
        )
        {
            var actionStr = action ?? "";

            if (target == null)
                return new ForceResult { Ok = false, Action = actionStr, Error = Error.GameObjectRefIsNull() };

            if (!target.IsValid(out var refErr))
                return new ForceResult { Ok = false, Action = actionStr, Error = refErr };

            if (string.IsNullOrEmpty(action))
                return new ForceResult { Ok = false, Action = "", Error = "'action' is required: 'force' | 'torque' | 'explosion' | 'impulse'." };

            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new ForceResult { Ok = false, Action = actionStr, Error = dimErr };

            return MainThread.Instance.Run(() =>
            {
                var go = target.FindGameObject(out var findErr);
                if (findErr != null || go == null)
                    return new ForceResult
                    {
                        Ok = false,
                        Action = actionStr,
                        Error = findErr ?? "GameObject not found."
                    };

                var actionLower = action.Trim().ToLowerInvariant();
                var path = GetTransformPath(go.transform);

                if (Is2D(dim))
                    return ApplyForce2D(go, path, actionLower, force, torque, explosionForce, explosionPosition, explosionRadius, mode);

                return ApplyForce3D(go, path, actionLower, force, torque, explosionForce, explosionPosition, explosionRadius, mode);
            });
        }

        private static ForceResult ApplyForce3D(
            GameObject go, string path, string action,
            Vector3? force, Vector3? torque,
            float? explosionForce, Vector3? explosionPosition, float? explosionRadius,
            string? mode)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
                return ForceError(path, action, Error.NoRigidbody3DOnGameObject(go.name));

            if (!TryParseForceMode3D(mode, out var forceMode, out var modeErr))
                return ForceError(path, action, modeErr!);

            switch (action)
            {
                case "force":
                    if (!force.HasValue)
                        return ForceError(path, action, Error.ForceVectorRequired());
                    rb.AddForce(force.Value, forceMode);
                    return Success(path, action);

                case "impulse":
                    if (!force.HasValue)
                        return ForceError(path, action, Error.ForceVectorRequired());
                    rb.AddForce(force.Value, ForceMode.Impulse);
                    return Success(path, action);

                case "torque":
                    if (!torque.HasValue)
                        return ForceError(path, action, Error.ForceVectorRequired());
                    rb.AddTorque(torque.Value, forceMode);
                    return Success(path, action);

                case "explosion":
                    if (!explosionRadius.HasValue)
                        return ForceError(path, action, Error.ExplosionRadiusRequired());
                    if (!explosionForce.HasValue || !explosionPosition.HasValue)
                        return ForceError(path, action,
                            "'explosion' requires explosionForce, explosionPosition and explosionRadius.");
                    rb.AddExplosionForce(explosionForce.Value, explosionPosition.Value, explosionRadius.Value, 0f, forceMode);
                    return Success(path, action);

                default:
                    return ForceError(path, action,
                        $"Unknown action '{action}'. Expected 'force' | 'torque' | 'explosion' | 'impulse'.");
            }
        }

        private static ForceResult ApplyForce2D(
            GameObject go, string path, string action,
            Vector3? force, Vector3? torque,
            float? explosionForce, Vector3? explosionPosition, float? explosionRadius,
            string? mode)
        {
            var rb = go.GetComponent<Rigidbody2D>();
            if (rb == null)
                return ForceError(path, action, Error.NoRigidbody2DOnGameObject(go.name));

            if (!TryParseForceMode2D(mode, out var forceMode, out var modeErr))
                return ForceError(path, action, modeErr!);

            switch (action)
            {
                case "force":
                    if (!force.HasValue)
                        return ForceError(path, action, Error.ForceVectorRequired());
                    rb.AddForce(new Vector2(force.Value.x, force.Value.y), forceMode);
                    return Success(path, action);

                case "impulse":
                    if (!force.HasValue)
                        return ForceError(path, action, Error.ForceVectorRequired());
                    rb.AddForce(new Vector2(force.Value.x, force.Value.y), ForceMode2D.Impulse);
                    return Success(path, action);

                case "torque":
                    // 2D torque is a scalar (about Z). Prefer torque.z, then fall back to force.z when omitted.
                    var t = torque?.z ?? force?.z;
                    if (!t.HasValue)
                        return ForceError(path, action, Error.ForceVectorRequired());
                    rb.AddTorque(t.Value, forceMode);
                    return Success(path, action);

                case "explosion":
                    // Explosions are an exclusively-3D Unity API.
                    return ForceError(path, action, Error.ExplosionOnly3D());

                default:
                    return ForceError(path, action,
                        $"Unknown action '{action}'. Expected 'force' | 'torque' | 'explosion' | 'impulse'.");
            }
        }

        private static bool TryParseForceMode3D(string? raw, out ForceMode mode, out string? error)
        {
            if (string.IsNullOrEmpty(raw))
            {
                mode = ForceMode.Force;
                error = null;
                return true;
            }
            if (Enum.TryParse<ForceMode>(raw, ignoreCase: true, out var parsed))
            {
                mode = parsed;
                error = null;
                return true;
            }
            mode = ForceMode.Force;
            error = Error.InvalidForceMode3D(raw!);
            return false;
        }

        private static bool TryParseForceMode2D(string? raw, out ForceMode2D mode, out string? error)
        {
            if (string.IsNullOrEmpty(raw))
            {
                mode = ForceMode2D.Force;
                error = null;
                return true;
            }
            // 2D natively knows only Force / Impulse. Map VelocityChange / Acceleration so 3D-style
            // 'mode' values do not hard-fail when the same prompt re-runs against a 2D rigidbody.
            if (raw!.Equals("Impulse", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("VelocityChange", StringComparison.OrdinalIgnoreCase))
            {
                mode = ForceMode2D.Impulse;
                error = null;
                return true;
            }
            if (raw.Equals("Force", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("Acceleration", StringComparison.OrdinalIgnoreCase))
            {
                mode = ForceMode2D.Force;
                error = null;
                return true;
            }
            mode = ForceMode2D.Force;
            error = Error.InvalidForceMode2D(raw);
            return false;
        }

        private static ForceResult Success(string path, string action) =>
            new ForceResult { Ok = true, GameObjectPath = path, Action = action };

        private static ForceResult ForceError(string path, string action, string error) =>
            new ForceResult { Ok = false, GameObjectPath = path, Action = action, Error = error };
    }
}
