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
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Physics
    {
        public const string PhysicsLinecastToolId = "physics-linecast";

        [McpPluginTool
        (
            PhysicsLinecastToolId,
            Title = "Physics / Linecast",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Test whether any collider intersects the line segment between `from` and `to`. " +
            "Wraps `UnityEngine.Physics.Linecast` (3D) or `UnityEngine.Physics2D.Linecast` (2D). " +
            "Returns a `RaycastResult` with `Hit=false` when the segment is clear.")]
        [McpPluginSkillBody("Linecast between two world-space points and return the first collider hit.\n\n" +
            "## Inputs\n\n" +
            "- `from` / `to` — start and end of the segment in world space.\n" +
            "- `layerMask` — bitmask of layers to test. `null` or `-1` -> all layers (`~0`).\n" +
            "- `dimension` — `'3d'` (default) or `'2d'`.\n\n" +
            "## Output\n\n" +
            "A `RaycastResult` (same shape as 'physics-raycast'); `Hit` is false when no collider lies on the segment.")]
        [Description("Linecast between two world-space points (3D or 2D).")]
        public RaycastResult Linecast
        (
            [Description("Segment start in world space.")]
            Vector3 from,
            [Description("Segment end in world space.")]
            Vector3 to,
            [Description("Layer mask bitmask. Defaults to all (~0) when null or -1.")]
            int? layerMask = null,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null
        )
        {
            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new RaycastResult { Hit = false, Error = dimErr };

            return MainThread.Instance.Run(() =>
            {
                var mask = ResolveLayerMask(layerMask);

                if (Is2D(dim))
                {
                    var hit2d = UnityEngine.Physics2D.Linecast(from, to, mask);
                    return hit2d.collider == null
                        ? new RaycastResult { Hit = false }
                        : BuildRaycastResult2D(hit2d);
                }

                if (UnityEngine.Physics.Linecast(from, to, out var hit, mask))
                    return BuildRaycastResult(hit);

                return new RaycastResult { Hit = false };
            });
        }
    }
}
