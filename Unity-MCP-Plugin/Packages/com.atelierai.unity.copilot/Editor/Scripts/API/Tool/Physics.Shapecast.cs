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
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Physics
    {
        public const string PhysicsShapecastToolId = "physics-shapecast";

        [McpPluginTool
        (
            PhysicsShapecastToolId,
            Title = "Physics / Shapecast",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Cast a moving shape (`box` | `sphere` | `capsule`) along `direction` and return " +
            "the colliders it hits. Wraps `Physics.BoxCast(All)`, `Physics.SphereCast(All)`, `Physics.CapsuleCast(All)` " +
            "for 3D and `Physics2D.BoxCast(All)`, `Physics2D.CircleCast(All)`, `Physics2D.CapsuleCast(All)` for 2D.")]
        [McpPluginSkillBody("Shapecast a primitive volume along a direction.\n\n" +
            "## Inputs (common)\n\n" +
            "- `shape` — `'box'`, `'sphere'`, or `'capsule'` (case-insensitive). In 2D `'sphere'` and `'circle'` are equivalent.\n" +
            "- `origin` — shape center / start position in world space.\n" +
            "- `direction` — cast direction (normalized internally).\n" +
            "- `maxDistance` — defaults to `Mathf.Infinity`.\n" +
            "- `layerMask` / `dimension` — same semantics as 'physics-raycast'.\n" +
            "- `returnAll` — `false` (default) returns the closest hit only; `true` returns every hit.\n\n" +
            "## Shape-specific parameters\n\n" +
            "- `box`: `halfExtents` (required, Vector3) plus optional `rotation` (Quaternion). " +
            "For 2D the half-extents are doubled to obtain `Physics2D.BoxCast`'s SIZE parameter; only `rotation`'s " +
            "Z-Euler angle is honored.\n" +
            "- `sphere`: `radius` (required). In 2D this maps to `Physics2D.CircleCast`.\n" +
            "- `capsule`: `radius` + `capsulePoint2` (the second end-cap; `origin` is the first end-cap). " +
            "For 2D capsule cast, the capsule size is derived from `halfExtents` (x=width, y=height); " +
            "when `halfExtents` is null the capsule degenerates to a circle of the supplied `radius`.\n\n" +
            "## Output\n\n" +
            "An array of `RaycastResult`. When `returnAll` is false the array has at most one element. " +
            "An array with a single element whose `Hit` is false and `Error` is set indicates an input " +
            "validation failure (e.g. missing `halfExtents` for box).")]
        [Description("Shapecast a primitive volume (box/sphere/capsule) and return the colliders it hits.")]
        public RaycastResult[] Shapecast
        (
            [Description("Shape: 'box' | 'sphere' | 'capsule'.")]
            string shape,
            [Description("Shape center / start position in world space.")]
            Vector3 origin,
            [Description("Cast direction (normalized internally).")]
            Vector3 direction,
            [Description("Box only: half-extents (Vector3). For 2D capsule it is also used as the capsule size (x=width, y=height).")]
            Vector3? halfExtents = null,
            [Description("Sphere / capsule radius.")]
            float? radius = null,
            [Description("Capsule only: world-space position of the second end-cap (the first is `origin`).")]
            Vector3? capsulePoint2 = null,
            [Description("Box only: rotation (Quaternion) of the box. Identity when null.")]
            Quaternion? rotation = null,
            [Description("Max cast distance. Defaults to Mathf.Infinity when null.")]
            float? maxDistance = null,
            [Description("Layer mask bitmask. Defaults to all (~0) when null or -1.")]
            int? layerMask = null,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null,
            [Description("When true, returns every hit along the cast. When false, returns at most the closest hit.")]
            bool returnAll = false
        )
        {
            if (string.IsNullOrEmpty(shape))
                return ErrorArray(Error.ShapeRequired());

            if (direction.sqrMagnitude <= 0f)
                return ErrorArray(Error.DirectionIsZero());

            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return ErrorArray(dimErr!);

            return MainThread.Instance.Run(() =>
            {
                var mask = ResolveLayerMask(layerMask);
                var dist = maxDistance ?? Mathf.Infinity;
                var dir = direction.normalized;
                var rot = rotation ?? Quaternion.identity;
                var shapeLower = shape.Trim().ToLowerInvariant();

                if (Is2D(dim))
                    return Shapecast2D(shapeLower, origin, dir, halfExtents, radius, rot, dist, mask, returnAll);

                return Shapecast3D(shapeLower, origin, dir, halfExtents, radius, capsulePoint2, rot, dist, mask, returnAll);
            });
        }

        private static RaycastResult[] Shapecast3D(
            string shape, Vector3 origin, Vector3 dir,
            Vector3? halfExtents, float? radius, Vector3? capsulePoint2,
            Quaternion rot, float dist, int mask, bool returnAll)
        {
            switch (shape)
            {
                case "box":
                    if (!halfExtents.HasValue)
                        return ErrorArray("'halfExtents' is required for 'box' shapecast.");
                    if (returnAll)
                    {
                        var hits = UnityEngine.Physics.BoxCastAll(origin, halfExtents.Value, dir, rot, dist, mask);
                        return BuildArray3D(hits);
                    }
                    return UnityEngine.Physics.BoxCast(origin, halfExtents.Value, dir, out var bh, rot, dist, mask)
                        ? new[] { BuildRaycastResult(bh) }
                        : Array.Empty<RaycastResult>();

                case "sphere":
                case "circle":
                    if (!radius.HasValue)
                        return ErrorArray("'radius' is required for 'sphere' shapecast.");
                    if (returnAll)
                    {
                        var hits = UnityEngine.Physics.SphereCastAll(origin, radius.Value, dir, dist, mask);
                        return BuildArray3D(hits);
                    }
                    return UnityEngine.Physics.SphereCast(origin, radius.Value, dir, out var sh, dist, mask)
                        ? new[] { BuildRaycastResult(sh) }
                        : Array.Empty<RaycastResult>();

                case "capsule":
                    if (!radius.HasValue)
                        return ErrorArray("'radius' is required for 'capsule' shapecast.");
                    if (!capsulePoint2.HasValue)
                        return ErrorArray("'capsulePoint2' is required for 'capsule' shapecast (point1 = origin).");
                    if (returnAll)
                    {
                        var hits = UnityEngine.Physics.CapsuleCastAll(origin, capsulePoint2.Value, radius.Value, dir, dist, mask);
                        return BuildArray3D(hits);
                    }
                    return UnityEngine.Physics.CapsuleCast(origin, capsulePoint2.Value, radius.Value, dir, out var ch, dist, mask)
                        ? new[] { BuildRaycastResult(ch) }
                        : Array.Empty<RaycastResult>();

                default:
                    return ErrorArray(Error.InvalidShape3D(shape));
            }
        }

        private static RaycastResult[] Shapecast2D(
            string shape, Vector3 origin, Vector3 dir,
            Vector3? halfExtents, float? radius,
            Quaternion rot, float dist, int mask, bool returnAll)
        {
            var origin2 = new Vector2(origin.x, origin.y);
            var dir2 = new Vector2(dir.x, dir.y);
            var angle = rot.eulerAngles.z;

            switch (shape)
            {
                case "box":
                {
                    if (!halfExtents.HasValue)
                        return ErrorArray("'halfExtents' is required for 'box' shapecast.");
                    var size = new Vector2(halfExtents.Value.x * 2f, halfExtents.Value.y * 2f);
                    if (returnAll)
                    {
                        var hits = UnityEngine.Physics2D.BoxCastAll(origin2, size, angle, dir2, dist, mask);
                        return BuildArray2D(hits);
                    }
                    var hit = UnityEngine.Physics2D.BoxCast(origin2, size, angle, dir2, dist, mask);
                    return hit.collider != null ? new[] { BuildRaycastResult2D(hit) } : Array.Empty<RaycastResult>();
                }
                case "sphere":
                case "circle":
                {
                    if (!radius.HasValue)
                        return ErrorArray("'radius' is required for 'sphere'/'circle' shapecast.");
                    if (returnAll)
                    {
                        var hits = UnityEngine.Physics2D.CircleCastAll(origin2, radius.Value, dir2, dist, mask);
                        return BuildArray2D(hits);
                    }
                    var hit = UnityEngine.Physics2D.CircleCast(origin2, radius.Value, dir2, dist, mask);
                    return hit.collider != null ? new[] { BuildRaycastResult2D(hit) } : Array.Empty<RaycastResult>();
                }
                case "capsule":
                {
                    if (!radius.HasValue)
                        return ErrorArray("'radius' is required for 'capsule' shapecast.");
                    // 2D capsule needs a SIZE (Vector2) + direction (Horizontal/Vertical). We use halfExtents (x,y)
                    // when present to derive size; otherwise degenerate to a circle of the supplied radius.
                    var size = halfExtents.HasValue
                        ? new Vector2(Mathf.Max(halfExtents.Value.x * 2f, radius.Value * 2f),
                                      Mathf.Max(halfExtents.Value.y * 2f, radius.Value * 2f))
                        : new Vector2(radius.Value * 2f, radius.Value * 2f);
                    var capDir = size.x > size.y
                        ? CapsuleDirection2D.Horizontal
                        : CapsuleDirection2D.Vertical;
                    if (returnAll)
                    {
                        var hits = UnityEngine.Physics2D.CapsuleCastAll(origin2, size, capDir, angle, dir2, dist, mask);
                        return BuildArray2D(hits);
                    }
                    var hit = UnityEngine.Physics2D.CapsuleCast(origin2, size, capDir, angle, dir2, dist, mask);
                    return hit.collider != null ? new[] { BuildRaycastResult2D(hit) } : Array.Empty<RaycastResult>();
                }
                default:
                    return ErrorArray(Error.InvalidShape2D(shape));
            }
        }

        private static RaycastResult[] BuildArray3D(RaycastHit[] hits)
        {
            var list = new List<RaycastResult>(hits.Length);
            for (int i = 0; i < hits.Length; i++)
                list.Add(BuildRaycastResult(hits[i]));
            return list.ToArray();
        }

        private static RaycastResult[] BuildArray2D(RaycastHit2D[] hits)
        {
            var list = new List<RaycastResult>(hits.Length);
            for (int i = 0; i < hits.Length; i++)
            {
                var r = BuildRaycastResult2D(hits[i]);
                if (r.Hit) list.Add(r);
            }
            return list.ToArray();
        }

        private static RaycastResult[] ErrorArray(string message) =>
            new[] { new RaycastResult { Hit = false, Error = message } };
    }
}
