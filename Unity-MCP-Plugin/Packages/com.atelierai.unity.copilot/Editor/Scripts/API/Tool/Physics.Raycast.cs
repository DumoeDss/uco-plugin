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
        // ===============================================================
        // Subset-C shared DTOs and helpers
        //
        // The Physics root (Physics.cs) provides PhysicsDimension,
        // TryParseDimension, Is2D, ResolveLayer (string), and a centralized
        // Error class. This file adds the helpers / DTOs that are specific
        // to query / force / simulate operations and are reused by the
        // other five Subset-C partials. Other Subset-C files MUST NOT
        // re-declare any of the members in this region.
        // ===============================================================

        /// <summary>
        /// Result of a raycast / linecast / shapecast — normalized so that
        /// both 3D <see cref="RaycastHit"/> and 2D <see cref="RaycastHit2D"/>
        /// surface a uniform field set.
        /// </summary>
        public class RaycastResult
        {
            [Description("True when a collider was hit.")]
            public bool Hit { get; set; }

            [Description("World-space hit point. Null when Hit is false.")]
            public Vector3? Point { get; set; }

            [Description("Surface normal at the hit point. Null when Hit is false.")]
            public Vector3? Normal { get; set; }

            [Description("Distance from ray origin to hit point. Null when Hit is false.")]
            public float? Distance { get; set; }

            [Description("Slash-separated hierarchy path of the hit collider's GameObject.")]
            public string? ColliderGameObject { get; set; }

            [Description("Instance ID of the hit collider's GameObject.")]
#if UNITY_6000_5_OR_NEWER
            public UnityEngine.EntityId? ColliderInstanceId { get; set; }
#else
            public int? ColliderInstanceId { get; set; }
#endif

            [Description("Unity tag of the hit collider's GameObject.")]
            public string? Tag { get; set; }

            [Description("Layer index of the hit collider's GameObject.")]
            public int? Layer { get; set; }

            [Description("Slash-separated path of the rigidbody (3D or 2D) attached to the collider. " +
                "Null when no rigidbody is attached.")]
            public string? RigidbodyGameObject { get; set; }

            [Description("Error message — set only on internal failure (e.g. invalid shape arguments). " +
                "Null on a normal 'no hit' miss.")]
            public string? Error { get; set; }
        }

        /// <summary>
        /// Resolve a nullable / negative integer layer mask to a valid bitmask.
        /// Used for the int-mask flavour of physics queries; complements
        /// <see cref="ResolveLayer"/> in <c>Physics.cs</c> which resolves a single
        /// layer token (name or index) to a 0..31 layer index.
        /// </summary>
        internal static int ResolveLayerMask(int? layerMask)
        {
            if (!layerMask.HasValue) return ~0;
            return layerMask.Value < 0 ? ~0 : layerMask.Value;
        }

        /// <summary>
        /// Parse a <c>'UseGlobal' | 'Collide' | 'Ignore'</c> string (case-insensitive).
        /// Defaults to <see cref="QueryTriggerInteraction.UseGlobal"/>. Out-error is
        /// produced via <see cref="Error.InvalidQueryTriggerInteraction"/> when the
        /// caller wants strict validation.
        /// </summary>
        internal static QueryTriggerInteraction ParseQueryTriggerInteraction(string? value)
        {
            if (string.IsNullOrEmpty(value)) return QueryTriggerInteraction.UseGlobal;
            return Enum.TryParse<QueryTriggerInteraction>(value, ignoreCase: true, out var parsed)
                ? parsed
                : QueryTriggerInteraction.UseGlobal;
        }

        /// <summary>
        /// Walk a transform up to its root, producing a slash-separated hierarchy path.
        /// </summary>
        internal static string GetTransformPath(Transform? t)
        {
            if (t == null) return string.Empty;
            var path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return path;
        }

        // ---------- result builders ----------

        internal static RaycastResult BuildRaycastResult(RaycastHit hit)
        {
            if (hit.collider == null)
                return new RaycastResult { Hit = false };

            var col = hit.collider;
            var go = col.gameObject;
            var rb = col.attachedRigidbody;
            return new RaycastResult
            {
                Hit = true,
                Point = hit.point,
                Normal = hit.normal,
                Distance = hit.distance,
                ColliderGameObject = GetTransformPath(go.transform),
#if UNITY_6000_5_OR_NEWER
                ColliderInstanceId = go.GetEntityId(),
#else
                ColliderInstanceId = go.GetInstanceID(),
#endif
                Tag = go.tag,
                Layer = go.layer,
                RigidbodyGameObject = rb != null ? GetTransformPath(rb.transform) : null
            };
        }

        internal static RaycastResult BuildRaycastResult2D(RaycastHit2D hit)
        {
            if (hit.collider == null)
                return new RaycastResult { Hit = false };

            var col = hit.collider;
            var go = col.gameObject;
            var rb = col.attachedRigidbody;
            // RaycastHit2D.point is Vector2 — promote to Vector3 (z=0).
            var point = new Vector3(hit.point.x, hit.point.y, 0f);
            var normal = new Vector3(hit.normal.x, hit.normal.y, 0f);
            return new RaycastResult
            {
                Hit = true,
                Point = point,
                Normal = normal,
                Distance = hit.distance,
                ColliderGameObject = GetTransformPath(go.transform),
#if UNITY_6000_5_OR_NEWER
                ColliderInstanceId = go.GetEntityId(),
#else
                ColliderInstanceId = go.GetInstanceID(),
#endif
                Tag = go.tag,
                Layer = go.layer,
                RigidbodyGameObject = rb != null ? GetTransformPath(rb.transform) : null
            };
        }

        /// <summary>
        /// Scoped toggle of <see cref="Physics2D.queriesHitTriggers"/> so callers can opt-in
        /// to <c>QueryTriggerInteraction</c>-style behaviour for 2D queries
        /// (which natively only respect the global toggle).
        /// </summary>
        internal sealed class Physics2DTriggerScope : IDisposable
        {
            private readonly bool _hadOverride;
            private readonly bool _prev;

            public Physics2DTriggerScope(string? value)
            {
                if (string.IsNullOrEmpty(value) ||
                    value!.Equals("UseGlobal", StringComparison.OrdinalIgnoreCase))
                {
                    _hadOverride = false;
                    _prev = false;
                    return;
                }

                _hadOverride = true;
                _prev = UnityEngine.Physics2D.queriesHitTriggers;
                if (value.Equals("Collide", StringComparison.OrdinalIgnoreCase))
                    UnityEngine.Physics2D.queriesHitTriggers = true;
                else if (value.Equals("Ignore", StringComparison.OrdinalIgnoreCase))
                    UnityEngine.Physics2D.queriesHitTriggers = false;
                else
                    _hadOverride = false;
            }

            public void Dispose()
            {
                if (_hadOverride)
                    UnityEngine.Physics2D.queriesHitTriggers = _prev;
            }
        }

        // ===============================================================
        // physics-raycast / physics-raycast-all
        // ===============================================================

        public const string PhysicsRaycastToolId = "physics-raycast";
        public const string PhysicsRaycastAllToolId = "physics-raycast-all";

        [McpPluginTool
        (
            PhysicsRaycastToolId,
            Title = "Physics / Raycast",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Cast a ray from `origin` along `direction` and return the FIRST collider hit. " +
            "Supports 3D (`UnityEngine.Physics.Raycast`) and 2D (`UnityEngine.Physics2D.Raycast`) physics — " +
            "select via `dimension`. Layer mask defaults to all layers; trigger interaction defaults to global.")]
        [McpPluginSkillBody("Cast a ray and return the first hit.\n\n" +
            "## Inputs\n\n" +
            "- `origin` / `direction` — ray in world space. `direction` is normalized internally.\n" +
            "- `maxDistance` — optional cap; defaults to `Mathf.Infinity`.\n" +
            "- `layerMask` — bitmask of layers to test. `null` or `-1` -> all layers (`~0`).\n" +
            "- `dimension` — `'3d'` (default) or `'2d'`.\n" +
            "- `queryTriggerInteraction` — `'UseGlobal'` (default), `'Collide'`, or `'Ignore'`. " +
            "In 2D the global `Physics2D.queriesHitTriggers` toggle is honored; `Collide` / `Ignore` temporarily flip it.\n\n" +
            "## Output\n\n" +
            "A `RaycastResult` whose `Hit` is false when nothing was hit. On hit, `ColliderGameObject` is the " +
            "slash-separated hierarchy path of the collider and `RigidbodyGameObject` is the attached rigidbody " +
            "path when present.")]
        [Description("Cast a ray and return the first hit (3D or 2D).")]
        public RaycastResult Raycast
        (
            [Description("Ray origin in world space.")]
            Vector3 origin,
            [Description("Ray direction (will be normalized).")]
            Vector3 direction,
            [Description("Max distance. Defaults to Mathf.Infinity when null.")]
            float? maxDistance = null,
            [Description("Layer mask bitmask. Defaults to all (~0) when null or -1.")]
            int? layerMask = null,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null,
            [Description("Query trigger interaction: 'UseGlobal' (default) | 'Collide' | 'Ignore'.")]
            string? queryTriggerInteraction = null
        )
        {
            if (direction.sqrMagnitude <= 0f)
                return new RaycastResult { Hit = false, Error = Error.DirectionIsZero() };

            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new RaycastResult { Hit = false, Error = dimErr };

            return MainThread.Instance.Run(() =>
            {
                var mask = ResolveLayerMask(layerMask);
                var dist = maxDistance ?? Mathf.Infinity;
                var dir = direction.normalized;

                if (Is2D(dim))
                {
                    using (new Physics2DTriggerScope(queryTriggerInteraction))
                    {
                        var hit2d = UnityEngine.Physics2D.Raycast(origin, dir, dist, mask);
                        return hit2d.collider == null
                            ? new RaycastResult { Hit = false }
                            : BuildRaycastResult2D(hit2d);
                    }
                }

                var qti = ParseQueryTriggerInteraction(queryTriggerInteraction);
                if (UnityEngine.Physics.Raycast(origin, dir, out var hit, dist, mask, qti))
                    return BuildRaycastResult(hit);

                return new RaycastResult { Hit = false };
            });
        }

        [McpPluginTool
        (
            PhysicsRaycastAllToolId,
            Title = "Physics / Raycast / All",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Cast a ray from `origin` along `direction` and return ALL colliders hit " +
            "(`Physics.RaycastAll` / `Physics2D.RaycastAll`). Same parameter semantics as 'physics-raycast', " +
            "but returns an array; empty when nothing was hit.")]
        [McpPluginSkillBody("Cast a ray and return every hit along the ray (not just the first).\n\n" +
            "Same inputs as 'physics-raycast'. Each entry in the returned array is a `RaycastResult` with " +
            "`Hit = true`. Order is provider-defined — Unity does not document a sort order.")]
        [Description("Cast a ray and return every collider it hits (3D or 2D).")]
        public RaycastResult[] RaycastAll
        (
            [Description("Ray origin in world space.")]
            Vector3 origin,
            [Description("Ray direction (will be normalized).")]
            Vector3 direction,
            [Description("Max distance. Defaults to Mathf.Infinity when null.")]
            float? maxDistance = null,
            [Description("Layer mask bitmask. Defaults to all (~0) when null or -1.")]
            int? layerMask = null,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null,
            [Description("Query trigger interaction: 'UseGlobal' (default) | 'Collide' | 'Ignore'.")]
            string? queryTriggerInteraction = null
        )
        {
            if (direction.sqrMagnitude <= 0f)
                return new[] { new RaycastResult { Hit = false, Error = Error.DirectionIsZero() } };

            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new[] { new RaycastResult { Hit = false, Error = dimErr } };

            return MainThread.Instance.Run(() =>
            {
                var mask = ResolveLayerMask(layerMask);
                var dist = maxDistance ?? Mathf.Infinity;
                var dir = direction.normalized;

                if (Is2D(dim))
                {
                    using (new Physics2DTriggerScope(queryTriggerInteraction))
                    {
                        var hits2d = UnityEngine.Physics2D.RaycastAll(origin, dir, dist, mask);
                        var results2d = new List<RaycastResult>(hits2d.Length);
                        for (int i = 0; i < hits2d.Length; i++)
                        {
                            var r = BuildRaycastResult2D(hits2d[i]);
                            if (r.Hit) results2d.Add(r);
                        }
                        return results2d.ToArray();
                    }
                }

                var qti = ParseQueryTriggerInteraction(queryTriggerInteraction);
                var hits = UnityEngine.Physics.RaycastAll(origin, dir, dist, mask, qti);
                var results = new List<RaycastResult>(hits.Length);
                for (int i = 0; i < hits.Length; i++)
                    results.Add(BuildRaycastResult(hits[i]));
                return results.ToArray();
            });
        }
    }
}
