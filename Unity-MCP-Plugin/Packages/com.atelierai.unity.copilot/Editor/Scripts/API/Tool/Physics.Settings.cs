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

namespace AIGD
{
    [Description("Snapshot of Unity physics settings for the requested dimension.")]
    public class PhysicsSettingsResult
    {
        [Description("True when the operation completed without errors.")]
        public bool Ok { get; set; }

        [Description("Error message when Ok is false. Null on success.")]
        public string? Error { get; set; }

        [Description("Dimension this snapshot describes: '2d' or '3d'.")]
        public string Dimension { get; set; } = "3d";

        [Description("Gravity vector. 3D: (x,y,z). 2D: (x,y,0) with z always zero.")]
        public float[] Gravity { get; set; } = Array.Empty<float>();

        [Description("Default bounce threshold velocity.")]
        public float? BounceThreshold { get; set; }

        [Description("Sleep threshold (3D only).")]
        public float? SleepThreshold { get; set; }

        [Description("Default contact offset (3D only).")]
        public float? DefaultContactOffset { get; set; }

        [Description("Default solver iterations (3D only).")]
        public int? DefaultSolverIterations { get; set; }

        [Description("Default solver velocity iterations (3D only).")]
        public int? DefaultSolverVelocityIterations { get; set; }

        [Description("Default maximum angular speed (3D only).")]
        public float? DefaultMaxAngularSpeed { get; set; }

        [Description("Whether the physics simulation auto-steps. Maps to Physics.simulationMode != Script on 3D, " +
                     "and Physics2D.simulationMode != Script on 2D.")]
        public bool? AutoSimulation { get; set; }

        [Description("Queries hit triggers globally.")]
        public bool? QueriesHitTriggers { get; set; }

        [Description("Queries hit back-faces globally (3D only).")]
        public bool? QueriesHitBackfaces { get; set; }

        [Description("Number of velocity iterations per simulation step (2D only).")]
        public int? VelocityIterations { get; set; }

        [Description("Number of position iterations per simulation step (2D only).")]
        public int? PositionIterations { get; set; }

        [Description("Names of settings that were modified by the last set call (empty for get).")]
        public List<string> Changed { get; set; } = new List<string>();
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;

    public partial class Tool_Physics
    {
        public const string PhysicsSettingsGetToolId = "physics-settings-get";
        public const string PhysicsSettingsSetToolId = "physics-settings-set";

        [McpPluginTool
        (
            PhysicsSettingsGetToolId,
            Title = "Physics / Settings / Get",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Read the current Unity physics settings for the requested dimension " +
            "('3d' default or '2d'). Returns the gravity vector, solver iterations, thresholds, and global " +
            "query flags.")]
        [McpPluginSkillBody("Reads from `UnityEngine.Physics` (3D) or `UnityEngine.Physics2D` (2D).\n\n" +
            "## Inputs\n\n" +
            "- `dimension` (default `'3d'`) — `'2d'` or `'3d'`.\n\n" +
            "## Behavior\n\n" +
            "Returns a `PhysicsSettingsResult` populated with the fields applicable to the requested dimension. " +
            "Dimension-only fields are left at their default null value for the other dimension.")]
        [Description("Read current physics settings (gravity, solver iterations, thresholds, query flags) " +
            "for the requested dimension.")]
        public PhysicsSettingsResult GetSettings
        (
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null
        )
        {
            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new PhysicsSettingsResult { Ok = false, Error = dimErr };

            return MainThread.Instance.Run(() =>
            {
                if (Is2D(dim))
                    return Read2DSettings();
                return Read3DSettings();
            });
        }

        [McpPluginTool
        (
            PhysicsSettingsSetToolId,
            Title = "Physics / Settings / Set",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Mutate Unity physics settings for the requested dimension. Any argument left " +
            "null is ignored; only the non-null arguments are applied. Marks the corresponding ProjectSettings " +
            "asset dirty so the change persists.")]
        [McpPluginSkillBody("Writes to `UnityEngine.Physics` (3D) or `UnityEngine.Physics2D` (2D).\n\n" +
            "## Inputs\n\n" +
            "- `dimension` (default `'3d'`) — `'2d'` or `'3d'`.\n" +
            "- `gravity` — Vector3 (z is ignored for 2D).\n" +
            "- `bounceThreshold` — applies to both dimensions.\n" +
            "- `sleepThreshold`, `defaultContactOffset`, `defaultSolverIterations`, " +
            "`defaultSolverVelocityIterations`, `queriesHitBackfaces` — 3D only.\n" +
            "- `velocityIterations`, `positionIterations` — 2D only.\n" +
            "- `queriesHitTriggers` — applies to both dimensions.\n" +
            "- `autoSimulation` — toggles `Physics.simulationMode` / `Physics2D.simulationMode` between " +
            "FixedUpdate and Script.\n\n" +
            "## Behavior\n\n" +
            "Settings are applied on the main thread. The list of fields actually mutated is returned in " +
            "`result.Changed`. Supplying a 3D-only setting under `dimension: '2d'` (or vice versa) is silently " +
            "ignored — the operation only fails when *no* applicable setting was supplied.")]
        [Description("Mutate physics settings for the requested dimension (gravity, thresholds, solver counts, " +
            "global query flags). Only non-null arguments are applied.")]
        public PhysicsSettingsResult SetSettings
        (
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null,
            [Description("Gravity vector (x,y,z). For 2D the z component is ignored.")]
            Vector3? gravity = null,
            [Description("Bounce threshold velocity. Applies to both 2D and 3D.")]
            float? bounceThreshold = null,
            [Description("Sleep threshold (3D only).")]
            float? sleepThreshold = null,
            [Description("Default contact offset (3D only).")]
            float? defaultContactOffset = null,
            [Description("Default solver iterations (3D only).")]
            int? defaultSolverIterations = null,
            [Description("Default solver velocity iterations (3D only).")]
            int? defaultSolverVelocityIterations = null,
            [Description("Default maximum angular speed (3D only).")]
            float? defaultMaxAngularSpeed = null,
            [Description("Auto-simulation enabled. Maps to Physics.simulationMode (3D) and Physics2D.simulationMode (2D).")]
            bool? autoSimulation = null,
            [Description("Whether queries (raycast / overlap / cast) collide with triggers globally.")]
            bool? queriesHitTriggers = null,
            [Description("Whether queries hit back-faces globally (3D only).")]
            bool? queriesHitBackfaces = null,
            [Description("Number of velocity iterations per simulation step (2D only).")]
            int? velocityIterations = null,
            [Description("Number of position iterations per simulation step (2D only).")]
            int? positionIterations = null
        )
        {
            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new PhysicsSettingsResult { Ok = false, Error = dimErr };

            return MainThread.Instance.Run(() =>
            {
                var changed = new List<string>();
                bool anyApplicable = false;

                if (Is2D(dim))
                {
                    if (gravity.HasValue)
                    {
                        Physics2D.gravity = new Vector2(gravity.Value.x, gravity.Value.y);
                        changed.Add("gravity");
                        anyApplicable = true;
                    }
                    // Note: Physics2D has no static bounce threshold; the equivalent is per-material.
                    // `bounceThreshold` is silently ignored when dimension == 2d. If the caller supplied
                    // ONLY 2D-incompatible args, the call fails with NoSettingsApplied below.
                    if (velocityIterations.HasValue)
                    {
                        Physics2D.velocityIterations = velocityIterations.Value;
                        changed.Add("velocityIterations");
                        anyApplicable = true;
                    }
                    if (positionIterations.HasValue)
                    {
                        Physics2D.positionIterations = positionIterations.Value;
                        changed.Add("positionIterations");
                        anyApplicable = true;
                    }
                    if (queriesHitTriggers.HasValue)
                    {
                        Physics2D.queriesHitTriggers = queriesHitTriggers.Value;
                        changed.Add("queriesHitTriggers");
                        anyApplicable = true;
                    }
                    if (autoSimulation.HasValue)
                    {
#if UNITY_2022_2_OR_NEWER
                        Physics2D.simulationMode = autoSimulation.Value
                            ? SimulationMode2D.FixedUpdate
                            : SimulationMode2D.Script;
                        changed.Add("autoSimulation");
                        anyApplicable = true;
#else
                        // legacy fallback
                        Physics2D.autoSimulation = autoSimulation.Value;
                        changed.Add("autoSimulation");
                        anyApplicable = true;
#endif
                    }

                    if (!anyApplicable)
                        return new PhysicsSettingsResult { Ok = false, Error = Error.NoSettingsApplied(), Dimension = "2d" };

                    MarkProjectSettingsDirty("ProjectSettings/Physics2DSettings.asset");
                    var snapshot = Read2DSettings();
                    snapshot.Changed = changed;
                    return snapshot;
                }
                else
                {
                    if (gravity.HasValue)
                    {
                        UnityEngine.Physics.gravity = gravity.Value;
                        changed.Add("gravity");
                        anyApplicable = true;
                    }
                    if (bounceThreshold.HasValue)
                    {
                        UnityEngine.Physics.bounceThreshold = bounceThreshold.Value;
                        changed.Add("bounceThreshold");
                        anyApplicable = true;
                    }
                    if (sleepThreshold.HasValue)
                    {
                        UnityEngine.Physics.sleepThreshold = sleepThreshold.Value;
                        changed.Add("sleepThreshold");
                        anyApplicable = true;
                    }
                    if (defaultContactOffset.HasValue)
                    {
                        UnityEngine.Physics.defaultContactOffset = defaultContactOffset.Value;
                        changed.Add("defaultContactOffset");
                        anyApplicable = true;
                    }
                    if (defaultSolverIterations.HasValue)
                    {
                        UnityEngine.Physics.defaultSolverIterations = defaultSolverIterations.Value;
                        changed.Add("defaultSolverIterations");
                        anyApplicable = true;
                    }
                    if (defaultSolverVelocityIterations.HasValue)
                    {
                        UnityEngine.Physics.defaultSolverVelocityIterations = defaultSolverVelocityIterations.Value;
                        changed.Add("defaultSolverVelocityIterations");
                        anyApplicable = true;
                    }
                    if (defaultMaxAngularSpeed.HasValue)
                    {
                        UnityEngine.Physics.defaultMaxAngularSpeed = defaultMaxAngularSpeed.Value;
                        changed.Add("defaultMaxAngularSpeed");
                        anyApplicable = true;
                    }
                    if (queriesHitTriggers.HasValue)
                    {
                        UnityEngine.Physics.queriesHitTriggers = queriesHitTriggers.Value;
                        changed.Add("queriesHitTriggers");
                        anyApplicable = true;
                    }
                    if (queriesHitBackfaces.HasValue)
                    {
                        UnityEngine.Physics.queriesHitBackfaces = queriesHitBackfaces.Value;
                        changed.Add("queriesHitBackfaces");
                        anyApplicable = true;
                    }
                    if (autoSimulation.HasValue)
                    {
#if UNITY_2022_2_OR_NEWER
                        UnityEngine.Physics.simulationMode = autoSimulation.Value
                            ? SimulationMode.FixedUpdate
                            : SimulationMode.Script;
                        changed.Add("autoSimulation");
                        anyApplicable = true;
#else
                        UnityEngine.Physics.autoSimulation = autoSimulation.Value;
                        changed.Add("autoSimulation");
                        anyApplicable = true;
#endif
                    }

                    if (!anyApplicable)
                        return new PhysicsSettingsResult { Ok = false, Error = Error.NoSettingsApplied(), Dimension = "3d" };

                    MarkProjectSettingsDirty("ProjectSettings/DynamicsManager.asset");
                    var snapshot = Read3DSettings();
                    snapshot.Changed = changed;
                    return snapshot;
                }
            });
        }

        // -----------------------------------------------------------------
        // Internal helpers
        // -----------------------------------------------------------------

        private static PhysicsSettingsResult Read3DSettings()
        {
            var g = UnityEngine.Physics.gravity;

            bool? autoSim = null;
#if UNITY_2022_2_OR_NEWER
            autoSim = UnityEngine.Physics.simulationMode != SimulationMode.Script;
#else
            autoSim = UnityEngine.Physics.autoSimulation;
#endif

            return new PhysicsSettingsResult
            {
                Ok = true,
                Dimension = "3d",
                Gravity = new[] { g.x, g.y, g.z },
                BounceThreshold = UnityEngine.Physics.bounceThreshold,
                SleepThreshold = UnityEngine.Physics.sleepThreshold,
                DefaultContactOffset = UnityEngine.Physics.defaultContactOffset,
                DefaultSolverIterations = UnityEngine.Physics.defaultSolverIterations,
                DefaultSolverVelocityIterations = UnityEngine.Physics.defaultSolverVelocityIterations,
                DefaultMaxAngularSpeed = UnityEngine.Physics.defaultMaxAngularSpeed,
                AutoSimulation = autoSim,
                QueriesHitTriggers = UnityEngine.Physics.queriesHitTriggers,
                QueriesHitBackfaces = UnityEngine.Physics.queriesHitBackfaces
            };
        }

        private static PhysicsSettingsResult Read2DSettings()
        {
            var g = Physics2D.gravity;

            bool? autoSim = null;
#if UNITY_2022_2_OR_NEWER
            autoSim = Physics2D.simulationMode != SimulationMode2D.Script;
#else
            autoSim = Physics2D.autoSimulation;
#endif

            return new PhysicsSettingsResult
            {
                Ok = true,
                Dimension = "2d",
                Gravity = new[] { g.x, g.y, 0f },
                VelocityIterations = Physics2D.velocityIterations,
                PositionIterations = Physics2D.positionIterations,
                QueriesHitTriggers = Physics2D.queriesHitTriggers,
                AutoSimulation = autoSim
            };
        }

    }
}
