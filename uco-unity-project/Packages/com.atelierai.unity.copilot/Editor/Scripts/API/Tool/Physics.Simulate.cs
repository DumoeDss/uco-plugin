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
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Physics
    {
        /// <summary>
        /// Result of an Edit-Mode physics step.
        /// </summary>
        public class SimulateResult
        {
            [Description("True when the step ran without error.")]
            public bool Ok { get; set; }

            [Description("Actual time step that was applied (seconds).")]
            public float DeltaTimeUsed { get; set; }

            [Description("Dimension that was stepped: '3d' or '2d'.")]
            public string Dimension { get; set; } = "";

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        public const string PhysicsSimulateStepToolId = "physics-simulate-step";

        [UcoTool
        (
            PhysicsSimulateStepToolId,
            Title = "Physics / Simulate / Step",
            DestructiveHint = true
        )]
        [UcoSkillDescription("Advance the physics simulation by `deltaTime` seconds (Edit Mode friendly). " +
            "Wraps `Physics.Simulate` (3D) or `Physics2D.Simulate` (2D). On Unity 6+ the simulation mode is " +
            "temporarily flipped to `SimulationMode.Script` and restored after the step; on pre-6 the legacy " +
            "`Physics.autoSimulation` flag is toggled instead.")]
        [UcoSkillBody("Run a single physics step. Useful for headless Edit-Mode tests or for " +
            "deterministically stepping after `'physics-force-apply'`.\n\n" +
            "## Inputs\n\n" +
            "- `deltaTime` — step duration in seconds. Defaults to `Time.fixedDeltaTime` when null.\n" +
            "- `dimension` — `'3d'` (default) or `'2d'`.\n\n" +
            "## Behavior\n\n" +
            "The Unity 6+ branch (`UNITY_6000_0_OR_NEWER`) sets `Physics.simulationMode = SimulationMode.Script` " +
            "around the call. The legacy branch sets `Physics.autoSimulation = false`. The previous value is " +
            "restored via `try / finally`, even if `Simulate` throws.\n\n" +
            "For 2D, `Physics2D.simulationMode` is temporarily set to `SimulationMode2D.Script` (Unity 6+) " +
            "or `Physics2D.autoSimulation = false` (legacy).")]
        [Description("Advance the physics simulation by one step in Edit Mode (3D or 2D).")]
        public SimulateResult SimulateStep
        (
            [Description("Time step in seconds. Defaults to Time.fixedDeltaTime when null.")]
            float? deltaTime = null,
            [Description("Dimension: '3d' (default) or '2d'.")]
            string? dimension = null
        )
        {
            if (!TryParseDimension(dimension, out var dim, out var dimErr))
                return new SimulateResult
                {
                    Ok = false,
                    DeltaTimeUsed = deltaTime ?? 0f,
                    Dimension = dimension ?? "3d",
                    Error = dimErr
                };

            return MainThread.Instance.Run(() =>
            {
                var dt = deltaTime ?? Time.fixedDeltaTime;
                var dimLabel = DimensionToString(dim);

                if (dt <= 0f)
                    return new SimulateResult
                    {
                        Ok = false,
                        DeltaTimeUsed = dt,
                        Dimension = dimLabel,
                        Error = Error.SimulationStepSizeOutOfRange(dt)
                    };

                try
                {
                    if (Is2D(dim))
                        Simulate2D(dt);
                    else
                        Simulate3D(dt);

                    return new SimulateResult
                    {
                        Ok = true,
                        DeltaTimeUsed = dt,
                        Dimension = dimLabel
                    };
                }
                catch (Exception ex)
                {
                    return new SimulateResult
                    {
                        Ok = false,
                        DeltaTimeUsed = dt,
                        Dimension = dimLabel,
                        Error = ex.Message
                    };
                }
            });
        }

        // ---------- 3D ----------

        private static void Simulate3D(float dt)
        {
#if UNITY_6000_0_OR_NEWER
            var prev = UnityEngine.Physics.simulationMode;
            UnityEngine.Physics.simulationMode = SimulationMode.Script;
            try
            {
                UnityEngine.Physics.Simulate(dt);
            }
            finally
            {
                UnityEngine.Physics.simulationMode = prev;
            }
#else
            var prev = UnityEngine.Physics.autoSimulation;
            UnityEngine.Physics.autoSimulation = false;
            try
            {
                UnityEngine.Physics.Simulate(dt);
            }
            finally
            {
                UnityEngine.Physics.autoSimulation = prev;
            }
#endif
        }

        // ---------- 2D ----------

        private static void Simulate2D(float dt)
        {
#if UNITY_6000_0_OR_NEWER
            var prev = UnityEngine.Physics2D.simulationMode;
            UnityEngine.Physics2D.simulationMode = SimulationMode2D.Script;
            try
            {
                UnityEngine.Physics2D.Simulate(dt);
            }
            finally
            {
                UnityEngine.Physics2D.simulationMode = prev;
            }
#else
            var prev = UnityEngine.Physics2D.autoSimulation;
            UnityEngine.Physics2D.autoSimulation = false;
            try
            {
                UnityEngine.Physics2D.Simulate(dt);
            }
            finally
            {
                UnityEngine.Physics2D.autoSimulation = prev;
            }
#endif
        }
    }
}
