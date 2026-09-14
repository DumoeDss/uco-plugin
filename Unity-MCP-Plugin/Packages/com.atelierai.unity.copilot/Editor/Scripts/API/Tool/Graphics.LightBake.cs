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
using System.IO;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Graphics
    {
        // ---------------------------------------------------------------
        // DTOs
        // ---------------------------------------------------------------

        /// <summary>
        /// Result returned by lightmap bake commands (start / cancel / clear).
        /// </summary>
        public class LightBakeResult
        {
            [Description("True when the operation completed without an error.")]
            public bool Ok { get; set; }

            [Description("Status discriminator: 'started' | 'cancelled' | 'cleared' | 'no-op'. " +
                "Null when Ok=false because of an error.")]
            public string? Status { get; set; }

            [Description("Error message when Ok is false. Null on success.")]
            public string? Error { get; set; }
        }

        /// <summary>
        /// Current Lightmapping pipeline state plus a tally of generated lightmap assets.
        /// </summary>
        public class LightBakeStatus
        {
            [Description("True when Unity is currently baking lightmaps (Lightmapping.isRunning).")]
            public bool IsBaking { get; set; }

            [Description("Current bake progress in [0..1] from Lightmapping.buildProgress. " +
                "Stays at 0 when no bake is running.")]
            public float Progress { get; set; }

            [Description("Number of generated lightmap textures discovered via LightmapSettings.lightmaps. " +
                "Null when the API call failed.")]
            public int? LightmapCount { get; set; }

            [Description("Approximate total bytes of generated lightmap textures on disk. " +
                "Computed by summing the file sizes of the texture assets resolved from LightmapSettings.lightmaps. " +
                "Null when the disk lookup failed.")]
            public long? TotalSizeBytes { get; set; }
        }

        // ---------------------------------------------------------------
        // graphics-lightbake-start
        // ---------------------------------------------------------------

        public const string GraphicsLightBakeStartToolId = "graphics-lightbake-start";

        [McpPluginTool
        (
            GraphicsLightBakeStartToolId,
            Title = "Graphics / Lightmap Bake / Start",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Start an asynchronous lightmap bake via `Lightmapping.BakeAsync`. " +
            "Selects between BakedGI / RealtimeGI / BakedAndRealtimeGI by adjusting " +
            "`LightmapEditorSettings`-equivalent fields on the active `LightingSettings`. " +
            "Use 'graphics-lightbake-status' to monitor progress and 'graphics-lightbake-cancel' to abort.")]
        [McpPluginSkillBody("Kicks off a lightmap bake asynchronously.\n\n" +
            "## Inputs\n\n" +
            "- `bakeMode` — `BakedGI` (default) | `RealtimeGI` | `BakedAndRealtimeGI`. " +
            "Maps to the active `LightingSettings.bakedGI` / `realtimeGI` flags before the bake.\n" +
            "- `clearExisting` — when true, `Lightmapping.Clear()` is called first to wipe existing maps.\n\n" +
            "## Behavior\n\n" +
            "Runs on the Unity main thread. Refuses to start a new bake while " +
            "`Lightmapping.isRunning` is true (returns Ok=false + Status='no-op').")]
        [Description("Start an asynchronous lightmap bake (Lightmapping.BakeAsync).")]
        public LightBakeResult StartBake
        (
            [Description("Bake mode: 'BakedGI' | 'RealtimeGI' | 'BakedAndRealtimeGI'. Default 'BakedGI'.")]
            string? bakeMode = null,
            [Description("Whether to clear existing lightmaps first. Default false.")]
            bool clearExisting = false
        )
        {
            var mode = (bakeMode ?? "BakedGI").Trim();
            bool wantBaked, wantRealtime;
            switch (mode.ToLowerInvariant())
            {
                case "bakedgi":
                    wantBaked = true;  wantRealtime = false; break;
                case "realtimegi":
                    wantBaked = false; wantRealtime = true;  break;
                case "bakedandrealtimegi":
                    wantBaked = true;  wantRealtime = true;  break;
                default:
                    return new LightBakeResult
                    {
                        Ok = false,
                        Error = $"Unknown bakeMode '{mode}'. Expected one of: BakedGI, RealtimeGI, BakedAndRealtimeGI."
                    };
            }

            return MainThread.Instance.Run(() =>
            {
                try
                {
                    if (Lightmapping.isRunning)
                        return new LightBakeResult
                        {
                            Ok = false,
                            Status = "no-op",
                            Error = "A lightmap bake is already running. Cancel it first with 'graphics-lightbake-cancel'."
                        };

                    if (clearExisting)
                    {
                        try { Lightmapping.Clear(); }
                        catch { /* best-effort — the subsequent bake call is the real test */ }
                    }

                    // Apply bake-mode toggles on the active LightingSettings. The API was renamed
                    // around Unity 2020.1 — fall back to legacy property names via reflection so
                    // we keep compiling against 2022.3+ where 'LightingSettings' is the canonical entry.
                    TryApplyBakeMode(wantBaked, wantRealtime);

                    var started = Lightmapping.BakeAsync();
                    if (!started)
                        return new LightBakeResult
                        {
                            Ok = false,
                            Status = "no-op",
                            Error = "Lightmapping.BakeAsync returned false (Unity declined to start the bake). " +
                                    "Open Window > Rendering > Lighting to inspect the active settings."
                        };

                    return new LightBakeResult { Ok = true, Status = "started" };
                }
                catch (Exception ex)
                {
                    return new LightBakeResult { Ok = false, Error = ex.Message };
                }
            });
        }

        // ---------------------------------------------------------------
        // graphics-lightbake-cancel
        // ---------------------------------------------------------------

        public const string GraphicsLightBakeCancelToolId = "graphics-lightbake-cancel";

        [McpPluginTool
        (
            GraphicsLightBakeCancelToolId,
            Title = "Graphics / Lightmap Bake / Cancel",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Cancel the in-progress lightmap bake via `Lightmapping.Cancel()`. " +
            "Idempotent — returns Ok=true with Status='no-op' when no bake is running.")]
        [McpPluginSkillBody("Cancels the active bake. Safe to call when no bake is in progress. " +
            "Pass failIfNotRunning=true in unattended flows to distinguish 'cancelled something' from 'nothing happened'.")]
        [Description("Cancel the in-progress lightmap bake (Lightmapping.Cancel).")]
        public LightBakeResult CancelBake(
            [Description("Fail with an error instead of returning a 'no-op' when no bake is running (default false). " +
                "Useful for automation that must assert a bake was actually cancelled.")]
            bool failIfNotRunning = false)
        {
            return MainThread.Instance.Run(() =>
            {
                try
                {
                    if (!Lightmapping.isRunning)
                    {
                        return failIfNotRunning
                            ? new LightBakeResult { Ok = false, Status = "no-op", Error = "No bake is in progress." }
                            : new LightBakeResult { Ok = true, Status = "no-op" };
                    }

                    Lightmapping.Cancel();
                    return new LightBakeResult { Ok = true, Status = "cancelled" };
                }
                catch (Exception ex)
                {
                    return new LightBakeResult { Ok = false, Error = ex.Message };
                }
            });
        }

        // ---------------------------------------------------------------
        // graphics-lightbake-clear
        // ---------------------------------------------------------------

        public const string GraphicsLightBakeClearToolId = "graphics-lightbake-clear";

        [McpPluginTool
        (
            GraphicsLightBakeClearToolId,
            Title = "Graphics / Lightmap Bake / Clear",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Clear baked lightmaps for the open scenes via `Lightmapping.Clear()`. " +
            "Refuses to run while a bake is in progress unless cancelRunningBake=true.")]
        [McpPluginSkillBody("Wipes lightmap data from the open scenes. Throws no exception when no data " +
            "exists — Unity simply does nothing. By default a running bake blocks the clear; pass " +
            "cancelRunningBake=true to cancel it first and then clear.")]
        [Description("Clear baked lightmaps for the open scenes (Lightmapping.Clear).")]
        public LightBakeResult ClearLightmaps(
            [Description("Cancel a running bake before clearing instead of refusing (default false).")]
            bool cancelRunningBake = false)
        {
            return MainThread.Instance.Run(() =>
            {
                try
                {
                    if (Lightmapping.isRunning)
                    {
                        if (!cancelRunningBake)
                            return new LightBakeResult
                            {
                                Ok = false,
                                Status = "no-op",
                                Error = "Cannot clear lightmaps while a bake is running. Cancel it first or pass cancelRunningBake=true."
                            };

                        Lightmapping.Cancel();
                        return new LightBakeResult { Ok = true, Status = "cancelled-then-cleared" };
                    }

                    Lightmapping.Clear();
                    return new LightBakeResult { Ok = true, Status = "cleared" };
                }
                catch (Exception ex)
                {
                    return new LightBakeResult { Ok = false, Error = ex.Message };
                }
            });
        }

        // ---------------------------------------------------------------
        // graphics-lightbake-status
        // ---------------------------------------------------------------

        public const string GraphicsLightBakeStatusToolId = "graphics-lightbake-status";

        [McpPluginTool
        (
            GraphicsLightBakeStatusToolId,
            Title = "Graphics / Lightmap Bake / Status",
            ReadOnlyHint = true
        )]
        [McpPluginSkillDescription("Report the live bake status (`Lightmapping.isRunning`, " +
            "`Lightmapping.buildProgress`) plus the count and approximate disk size of generated lightmap textures.")]
        [McpPluginSkillBody("Returns a `LightBakeStatus` describing the live bake pipeline. " +
            "`LightmapCount` comes from `LightmapSettings.lightmaps`. `TotalSizeBytes` is computed by " +
            "summing the file sizes of the lightmap texture assets on disk; failures collapse to null. " +
            "Pass includeDiskSize=false for a cheap is-baking/progress probe that skips the disk walk.")]
        [Description("Get current lightmap bake status and generated lightmap stats.")]
        public LightBakeStatus GetBakeStatus(
            [Description("Sum the on-disk sizes of the lightmap textures into TotalSizeBytes (default true). " +
                "Set false for a cheap IsBaking/Progress/LightmapCount probe without touching the disk.")]
            bool includeDiskSize = true)
        {
            return MainThread.Instance.Run(() =>
            {
                var status = new LightBakeStatus
                {
                    IsBaking = Lightmapping.isRunning,
                    Progress = Lightmapping.buildProgress
                };

                if (!includeDiskSize)
                    return status;

                try
                {
                    var maps = LightmapSettings.lightmaps;
                    if (maps != null)
                    {
                        status.LightmapCount = maps.Length;

                        long total = 0L;
                        for (int i = 0; i < maps.Length; i++)
                        {
                            var ld = maps[i];
                            total += SafeAssetFileSize(ld.lightmapColor);
                            total += SafeAssetFileSize(ld.lightmapDir);
                            total += SafeAssetFileSize(ld.shadowMask);
                        }
                        status.TotalSizeBytes = total;
                    }
                }
                catch
                {
                    // Best-effort — leave LightmapCount / TotalSizeBytes null on failure.
                }

                return status;
            });
        }

        // ---------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------

        /// <summary>
        /// Mutate the active `LightingSettings` so that subsequent `Lightmapping.BakeAsync` uses the
        /// requested baked / realtime GI flags. Best-effort: failures are swallowed because some
        /// project setups do not expose a writable LightingSettings.
        /// </summary>
        private static void TryApplyBakeMode(bool wantBaked, bool wantRealtime)
        {
            try
            {
                var settings = Lightmapping.lightingSettings;
                if (settings == null)
                {
                    settings = new LightingSettings();
                    Lightmapping.lightingSettings = settings;
                }
                settings.bakedGI = wantBaked;
                settings.realtimeGI = wantRealtime;
            }
            catch
            {
                /* Best-effort — Unity may refuse on a brand-new scene with no LightingSettings asset. */
            }
        }

        /// <summary>
        /// Return the on-disk byte size of a Unity texture asset, or 0 when the texture is null,
        /// not an asset, or the file cannot be stat-ed.
        /// </summary>
        private static long SafeAssetFileSize(Texture? tex)
        {
            if (tex == null) return 0L;
            try
            {
                var path = AssetDatabase.GetAssetPath(tex);
                if (string.IsNullOrEmpty(path)) return 0L;
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0L;
            }
            catch
            {
                return 0L;
            }
        }
    }
}
