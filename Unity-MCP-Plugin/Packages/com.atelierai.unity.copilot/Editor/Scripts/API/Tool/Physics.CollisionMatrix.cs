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
using System.Collections.Generic;
using System.ComponentModel;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace AIGD
{
    [Description("A single populated layer (index + name).")]
    public class PhysicsLayerInfo
    {
        [Description("Layer index in 0..31.")]
        public int Index { get; set; }
        [Description("Display name of the layer.")]
        public string Name { get; set; } = string.Empty;
    }

    [Description("A pair (layerA, layerB, collide) entry in the collision matrix.")]
    public class PhysicsCollisionMatrixEntry
    {
        [Description("Layer A index (0..31).")]
        public int LayerA { get; set; }
        [Description("Layer A display name.")]
        public string LayerAName { get; set; } = string.Empty;
        [Description("Layer B index (0..31).")]
        public int LayerB { get; set; }
        [Description("Layer B display name.")]
        public string LayerBName { get; set; } = string.Empty;
        [Description("True when the two layers collide. False when collision is ignored.")]
        public bool Collide { get; set; }
    }

    [Description("Result of a 'physics-collision-matrix-get' or 'physics-collision-matrix-set' call.")]
    public class CollisionMatrixResult
    {
        [Description("True when the operation completed without errors.")]
        public bool Ok { get; set; }
        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }
        [Description("Dimension this snapshot describes: '2d' or '3d'.")]
        public string Dimension { get; set; } = "3d";
        [Description("Layers that are populated (have a non-empty name). Empty when reading from a project " +
                     "without custom layer names.")]
        public List<PhysicsLayerInfo> Layers { get; set; } = new List<PhysicsLayerInfo>();
        [Description("Upper-triangular collision matrix entries (one per unordered pair including self-pairs).")]
        public List<PhysicsCollisionMatrixEntry> Entries { get; set; } = new List<PhysicsCollisionMatrixEntry>();
        [Description("For 'set': echo of the requested layer A (resolved index).")]
        public int? AppliedLayerA { get; set; }
        [Description("For 'set': echo of the requested layer B (resolved index).")]
        public int? AppliedLayerB { get; set; }
        [Description("For 'set': echo of the resulting collide flag.")]
        public bool? AppliedCollide { get; set; }
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;

    public partial class Tool_Physics
    {
        public const string PhysicsCollisionMatrixGetToolId = "physics-collision-matrix-get";
        public const string PhysicsCollisionMatrixSetToolId = "physics-collision-matrix-set";

        [UcoTool
        (
            PhysicsCollisionMatrixGetToolId,
            Title = "Physics / Collision Matrix / Get",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("Snapshot the layer-vs-layer collision matrix for the requested dimension " +
            "('3d' default or '2d'). Only populated layers (non-empty name) are returned to keep the response " +
            "small.")]
        [UcoSkillBody("Reads `Physics.GetIgnoreLayerCollision` (3D) or `Physics2D.GetIgnoreLayerCollision` (2D).\n\n" +
            "## Inputs\n\n" +
            "- `dimension` (default `'3d'`) — `'2d'` or `'3d'`.\n\n" +
            "## Behavior\n\n" +
            "Walks layers 0..31, reports the ones with a non-empty name in `Layers`, and emits one " +
            "`PhysicsCollisionMatrixEntry` per unordered pair (including self-pairs) in `Entries`. " +
            "`Collide` is `true` when the layers collide and `false` when collision is ignored.")]
        [Description("Read the layer-vs-layer collision matrix for the requested dimension.")]
        public CollisionMatrixResult GetCollisionMatrix
        (
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null
        )
        {
            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new CollisionMatrixResult { Ok = false, Error = dimErr };

            return MainThread.Instance.Run(() => ReadMatrix(dim));
        }

        [UcoTool
        (
            PhysicsCollisionMatrixSetToolId,
            Title = "Physics / Collision Matrix / Set",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Toggle whether two layers should collide for the requested dimension. " +
            "Layers can be referenced by name or by integer index (0..31).")]
        [UcoSkillBody("Calls `Physics.IgnoreLayerCollision(a, b, !collide)` (3D) or the matching " +
            "`Physics2D` API. Marks the corresponding ProjectSettings asset dirty so the change persists.\n\n" +
            "## Inputs\n\n" +
            "- `layerA` / `layerB` — layer name or numeric index (0..31).\n" +
            "- `collide` — `true` to enable collision, `false` to disable.\n" +
            "- `dimension` (default `'3d'`) — `'2d'` or `'3d'`.\n\n" +
            "## Behavior\n\n" +
            "Self-pairs (layerA == layerB) are accepted; the toggle still affects engine-wide rules. After " +
            "applying the change the full updated matrix is returned in `result.Entries`.")]
        [Description("Toggle collision between two layers for the requested dimension. Layer arguments accept " +
            "either a layer name or a numeric index.")]
        public CollisionMatrixResult SetCollisionMatrix
        (
            [Description("Layer A: name or integer index 0..31.")]
            string layerA,
            [Description("Layer B: name or integer index 0..31.")]
            string layerB,
            [Description("Whether the two layers should collide. Internally calls IgnoreLayerCollision(!collide).")]
            bool collide,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null
        )
        {
            if (string.IsNullOrEmpty(layerA))
                return new CollisionMatrixResult { Ok = false, Error = Error.LayerAUnresolved(layerA ?? "") };
            if (string.IsNullOrEmpty(layerB))
                return new CollisionMatrixResult { Ok = false, Error = Error.LayerBUnresolved(layerB ?? "") };

            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new CollisionMatrixResult { Ok = false, Error = dimErr };

            int a = ResolveLayer(layerA);
            int b = ResolveLayer(layerB);
            if (a < 0) return new CollisionMatrixResult { Ok = false, Error = Error.LayerAUnresolved(layerA) };
            if (b < 0) return new CollisionMatrixResult { Ok = false, Error = Error.LayerBUnresolved(layerB) };

            return MainThread.Instance.Run(() =>
            {
                if (Is2D(dim))
                {
                    Physics2D.IgnoreLayerCollision(a, b, !collide);
                    MarkProjectSettingsDirty("ProjectSettings/Physics2DSettings.asset");
                }
                else
                {
                    UnityEngine.Physics.IgnoreLayerCollision(a, b, !collide);
                    MarkProjectSettingsDirty("ProjectSettings/DynamicsManager.asset");
                }

                var snapshot = ReadMatrix(dim);
                snapshot.AppliedLayerA = a;
                snapshot.AppliedLayerB = b;
                snapshot.AppliedCollide = collide;
                return snapshot;
            });
        }

        // -----------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------

        private static CollisionMatrixResult ReadMatrix(PhysicsDimension dim)
        {
            var populated = new List<int>();
            var layers = new List<PhysicsLayerInfo>();

            for (int i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name)) continue;
                populated.Add(i);
                layers.Add(new PhysicsLayerInfo { Index = i, Name = name });
            }

            var entries = new List<PhysicsCollisionMatrixEntry>(populated.Count * (populated.Count + 1) / 2);
            for (int ii = 0; ii < populated.Count; ii++)
            {
                int i = populated[ii];
                string nameI = LayerMask.LayerToName(i);
                for (int jj = 0; jj <= ii; jj++)
                {
                    int j = populated[jj];
                    string nameJ = LayerMask.LayerToName(j);
                    bool collides = Is2D(dim)
                        ? !Physics2D.GetIgnoreLayerCollision(i, j)
                        : !UnityEngine.Physics.GetIgnoreLayerCollision(i, j);
                    entries.Add(new PhysicsCollisionMatrixEntry
                    {
                        LayerA = i,
                        LayerAName = nameI,
                        LayerB = j,
                        LayerBName = nameJ,
                        Collide = collides
                    });
                }
            }

            return new CollisionMatrixResult
            {
                Ok = true,
                Dimension = DimensionToString(dim),
                Layers = layers,
                Entries = entries
            };
        }
    }
}
