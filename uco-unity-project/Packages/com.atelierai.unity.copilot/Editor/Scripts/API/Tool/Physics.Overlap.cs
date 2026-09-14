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
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Physics
    {
        /// <summary>
        /// One collider returned by an overlap query.
        /// </summary>
        public class OverlapResult
        {
            [Description("Slash-separated hierarchy path of the collider's GameObject.")]
            public string GameObject { get; set; } = "";

            [Description("Instance ID of the collider's GameObject.")]
#if UNITY_6000_5_OR_NEWER
            public UnityEngine.EntityId InstanceId { get; set; }
#else
            public int InstanceId { get; set; }
#endif

            [Description("Unity tag of the collider's GameObject.")]
            public string Tag { get; set; } = "";

            [Description("Layer index of the collider's GameObject.")]
            public int Layer { get; set; }

            [Description("Slash-separated path of the rigidbody (3D or 2D) attached to the collider, or null.")]
            public string? RigidbodyGameObject { get; set; }
        }

        public const string PhysicsOverlapToolId = "physics-overlap";

        [UcoTool
        (
            PhysicsOverlapToolId,
            Title = "Physics / Overlap",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("Find all colliders overlapping a primitive volume (`box` | `sphere`) at `center`. " +
            "Wraps `Physics.OverlapBox` / `Physics.OverlapSphere` (3D) or `Physics2D.OverlapBoxAll` / " +
            "`Physics2D.OverlapCircleAll` (2D). Returns an empty array when nothing overlaps.")]
        [UcoSkillBody("Find every collider currently overlapping a primitive volume.\n\n" +
            "## Inputs\n\n" +
            "- `shape` — `'box'` or `'sphere'` (case-insensitive). For 2D, `'sphere'` and `'circle'` both map to `OverlapCircleAll`.\n" +
            "- `center` — volume center in world space.\n" +
            "- `halfExtents` — required for `'box'`. For 2D the full SIZE used by `Physics2D.OverlapBoxAll` " +
            "is computed as `halfExtents * 2`.\n" +
            "- `radius` — required for `'sphere'`.\n" +
            "- `rotation` — box-only rotation (Quaternion); identity when null. " +
            "For 2D, the Z-axis Euler angle (degrees) is used.\n" +
            "- `layerMask` — bitmask of layers. `null` or `-1` -> all layers (`~0`).\n" +
            "- `dimension` — `'3d'` (default) or `'2d'`.\n\n" +
            "## Output\n\n" +
            "An array of `OverlapResult`. Empty when no collider overlaps the volume.")]
        [Description("Find all colliders overlapping a primitive volume (3D or 2D).")]
        public OverlapResult[] Overlap
        (
            [Description("Shape: 'box' | 'sphere'.")]
            string shape,
            [Description("Volume center in world space.")]
            Vector3 center,
            [Description("Box only: half-extents (Vector3).")]
            Vector3? halfExtents = null,
            [Description("Sphere only: radius.")]
            float? radius = null,
            [Description("Box only: rotation (Quaternion). Identity when null.")]
            Quaternion? rotation = null,
            [Description("Layer mask bitmask. Defaults to all (~0) when null or -1.")]
            int? layerMask = null,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null
        )
        {
            if (string.IsNullOrEmpty(shape))
                return Array.Empty<OverlapResult>();

            if (!TryParseDimension(dimension, out var dim, out _))
                return Array.Empty<OverlapResult>();

            return MainThread.Instance.Run(() =>
            {
                var mask = ResolveLayerMask(layerMask);
                var rot = rotation ?? Quaternion.identity;
                var shapeLower = shape.Trim().ToLowerInvariant();

                if (Is2D(dim))
                    return Overlap2D(shapeLower, center, halfExtents, radius, rot, mask);

                return Overlap3D(shapeLower, center, halfExtents, radius, rot, mask);
            });
        }

        private static OverlapResult[] Overlap3D(string shape, Vector3 center, Vector3? halfExtents, float? radius, Quaternion rot, int mask)
        {
            switch (shape)
            {
                case "box":
                {
                    if (!halfExtents.HasValue) return Array.Empty<OverlapResult>();
                    var cols = UnityEngine.Physics.OverlapBox(center, halfExtents.Value, rot, mask);
                    return BuildOverlapArray(cols);
                }
                case "sphere":
                case "circle":
                {
                    if (!radius.HasValue) return Array.Empty<OverlapResult>();
                    var cols = UnityEngine.Physics.OverlapSphere(center, radius.Value, mask);
                    return BuildOverlapArray(cols);
                }
                default:
                    return Array.Empty<OverlapResult>();
            }
        }

        private static OverlapResult[] Overlap2D(string shape, Vector3 center, Vector3? halfExtents, float? radius, Quaternion rot, int mask)
        {
            var center2 = new Vector2(center.x, center.y);
            var angle = rot.eulerAngles.z;

            switch (shape)
            {
                case "box":
                {
                    if (!halfExtents.HasValue) return Array.Empty<OverlapResult>();
                    var size = new Vector2(halfExtents.Value.x * 2f, halfExtents.Value.y * 2f);
                    var cols = UnityEngine.Physics2D.OverlapBoxAll(center2, size, angle, mask);
                    return BuildOverlapArray2D(cols);
                }
                case "sphere":
                case "circle":
                {
                    if (!radius.HasValue) return Array.Empty<OverlapResult>();
                    var cols = UnityEngine.Physics2D.OverlapCircleAll(center2, radius.Value, mask);
                    return BuildOverlapArray2D(cols);
                }
                default:
                    return Array.Empty<OverlapResult>();
            }
        }

        private static OverlapResult[] BuildOverlapArray(Collider[] cols)
        {
            if (cols == null || cols.Length == 0) return Array.Empty<OverlapResult>();
            var list = new List<OverlapResult>(cols.Length);
            for (int i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col == null) continue;
                var go = col.gameObject;
                var rb = col.attachedRigidbody;
                list.Add(new OverlapResult
                {
                    GameObject = GetTransformPath(go.transform),
#if UNITY_6000_5_OR_NEWER
                    InstanceId = go.GetEntityId(),
#else
                    InstanceId = go.GetInstanceID(),
#endif
                    Tag = go.tag,
                    Layer = go.layer,
                    RigidbodyGameObject = rb != null ? GetTransformPath(rb.transform) : null
                });
            }
            return list.ToArray();
        }

        private static OverlapResult[] BuildOverlapArray2D(Collider2D[] cols)
        {
            if (cols == null || cols.Length == 0) return Array.Empty<OverlapResult>();
            var list = new List<OverlapResult>(cols.Length);
            for (int i = 0; i < cols.Length; i++)
            {
                var col = cols[i];
                if (col == null) continue;
                var go = col.gameObject;
                var rb = col.attachedRigidbody;
                list.Add(new OverlapResult
                {
                    GameObject = GetTransformPath(go.transform),
#if UNITY_6000_5_OR_NEWER
                    InstanceId = go.GetEntityId(),
#else
                    InstanceId = go.GetInstanceID(),
#endif
                    Tag = go.tag,
                    Layer = go.layer,
                    RigidbodyGameObject = rb != null ? GetTransformPath(rb.transform) : null
                });
            }
            return list.ToArray();
        }
    }
}
