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
using System.Reflection;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        /// <summary>
        /// Snapshot of the most recently rendered frame's stats, sampled from
        /// `UnityEditor.UnityStats`. Values are best-effort and rely on the Game / Scene view
        /// having rendered at least one frame in the current Editor session.
        /// </summary>
        public class RenderingStatsResult
        {
            [Description("UnityStats.drawCalls — number of draw calls last frame.")]
            public int DrawCalls { get; set; }

            [Description("UnityStats.batches — number of static + dynamic batches last frame.")]
            public int Batches { get; set; }

            [Description("UnityStats.triangles — triangle count last frame.")]
            public int Triangles { get; set; }

            [Description("UnityStats.vertices — vertex count last frame.")]
            public int Vertices { get; set; }

            [Description("UnityStats.setPassCalls — shader pass switches last frame.")]
            public int SetPassCalls { get; set; }

            [Description("UnityStats.shadowCasters — number of shadow casters last frame.")]
            public int ShadowCasters { get; set; }

            [Description("UnityStats.usedTextureMemorySize divided by 1024*1024 — megabytes of texture memory in use.")]
            public long TotalMemoryMB { get; set; }

            [Description("UnityStats.usedTextureCount — number of textures currently loaded.")]
            public int TextureCount { get; set; }

            [Description("Error message when the underlying UnityStats API was unavailable. Null on success.")]
            public string? Error { get; set; }
        }

        public const string GraphicsRenderingStatsToolId = "graphics-rendering-stats";

        [McpPluginTool
        (
            GraphicsRenderingStatsToolId,
            Title = "Graphics / Rendering Stats",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Sample Unity's renderer stats (`UnityEditor.UnityStats`) — draw calls, " +
            "batches, triangles, vertices, set-pass calls, shadow casters, texture memory and count. " +
            "Reads values rendered most recently by the Editor (Game / Scene view).")]
        [McpPluginSkillBody("Wraps the editor-only `UnityEditor.UnityStats` static class.\n\n" +
            "## Caveats\n\n" +
            "- Values reflect the last rendered frame. Open and focus the Game or Scene view to refresh them.\n" +
            "- All getters are read via reflection so missing fields in a given Unity version do not break the call.")]
        [Description("Sample UnityEditor.UnityStats for the most-recent frame's renderer counters.")]
        public RenderingStatsResult GetRenderingStats()
        {
            return MainThread.Instance.Run(() =>
            {
                try
                {
                    long usedTextureMemoryBytes = ReadStatLong("usedTextureMemorySize");

                    return new RenderingStatsResult
                    {
                        DrawCalls       = ReadStatInt("drawCalls"),
                        Batches         = ReadStatInt("batches"),
                        Triangles       = ReadStatInt("triangles"),
                        Vertices        = ReadStatInt("vertices"),
                        SetPassCalls    = ReadStatInt("setPassCalls"),
                        ShadowCasters   = ReadStatInt("shadowCasters"),
                        TotalMemoryMB   = usedTextureMemoryBytes > 0
                            ? usedTextureMemoryBytes / (1024L * 1024L)
                            : 0L,
                        TextureCount    = ReadStatInt("usedTextureCount"),
                    };
                }
                catch (Exception ex)
                {
                    return new RenderingStatsResult { Error = ex.Message };
                }
            });
        }

        // ---------------------------------------------------------------
        // reflection helpers — UnityEditor.UnityStats is editor-only and
        // some members differ between Unity versions. Read via reflection
        // and silently coerce to 0 when missing so the tool never throws.
        // ---------------------------------------------------------------

        private static readonly Type? s_UnityStatsType =
            Type.GetType("UnityEditor.UnityStats, UnityEditor", throwOnError: false);

        private static int ReadStatInt(string memberName)
        {
            var v = ReadStat(memberName);
            return v switch
            {
                int i => i,
                long l => (int)l,
                short s => s,
                uint ui => (int)ui,
                ulong ul => (int)ul,
                _ => 0
            };
        }

        private static long ReadStatLong(string memberName)
        {
            var v = ReadStat(memberName);
            return v switch
            {
                long l => l,
                int i => i,
                short s => s,
                uint ui => ui,
                ulong ul => (long)ul,
                _ => 0L
            };
        }

        private static object? ReadStat(string memberName)
        {
            if (s_UnityStatsType == null) return null;
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                var prop = s_UnityStatsType.GetProperty(memberName, flags);
                if (prop != null) return prop.GetValue(null);
                var field = s_UnityStatsType.GetField(memberName, flags);
                if (field != null) return field.GetValue(null);
            }
            catch
            {
                /* swallow — caller treats null as 0 */
            }
            return null;
        }
    }
}
