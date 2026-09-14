/*
┌──────────────────────────────────────────────────────────────────┐
│  Adapted from MCP for Unity (https://github.com/CoplayDev/unity-mcp)
│  Copyright (c) 2025 Coplay Inc.                                  │
│  Licensed under the MIT License.                                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using AIGD;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UProfiler = UnityEngine.Profiling.Profiler;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Profiler
    {
        public const string ProfilerGetFrameTimingToolId = "profiler-get-frame-timing";
        [McpPluginTool
        (
            ProfilerGetFrameTimingToolId,
            Title = "Profiler / Get Frame Timing",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Capture the latest frame timing from Unity's `FrameTimingManager` (CPU / GPU / " +
            "main-thread / render-thread costs). Requires Frame Timing Stats to be enabled in Player settings or a " +
            "Development Build.")]
        [McpPluginSkillBody("Calls `FrameTimingManager.CaptureFrameTimings()` then `GetLatestTimings(1, _)`. " +
            "Returns CPU frame time, GPU frame time, main-thread and render-thread costs (where supported), present-wait, " +
            "frame-complete, and scale factors.\n\n" +
            "## Caveats\n\n" +
            "- Frame Timing Stats must be enabled (Project Settings > Player > Other Settings) or you must be in a " +
            "Development Build — otherwise the tool throws.\n" +
            "- Right after enabling, a few frames may be needed before data becomes available — `Available` will be " +
            "false until then.")]
        [Description("Capture the latest FrameTimingManager frame timing data.")]
        public FrameTimingResult GetFrameTiming(string? nothing = null)
        {
            return MainThread.Instance.Run(() =>
            {
#if UNITY_2022_2_OR_NEWER
                if (!FrameTimingManager.IsFeatureEnabled())
                    throw new InvalidOperationException(Error.FrameTimingFeatureDisabled);
#endif

                FrameTimingManager.CaptureFrameTimings();
                var timings = new FrameTiming[1];
                var count = FrameTimingManager.GetLatestTimings(1, timings);

                if (count == 0)
                {
                    return new FrameTimingResult { Available = false };
                }

                var t = timings[0];
                return new FrameTimingResult
                {
                    Available = true,
                    CpuFrameTimeMs = t.cpuFrameTime,
#if UNITY_2022_2_OR_NEWER
                    CpuMainThreadFrameTimeMs = t.cpuMainThreadFrameTime,
                    CpuMainThreadPresentWaitTimeMs = t.cpuMainThreadPresentWaitTime,
                    CpuRenderThreadFrameTimeMs = t.cpuRenderThreadFrameTime,
                    FrameStartTimestamp = t.frameStartTimestamp,
                    FirstSubmitTimestamp = t.firstSubmitTimestamp,
#endif
                    GpuFrameTimeMs = t.gpuFrameTime,
                    CpuTimePresentCalled = t.cpuTimePresentCalled,
                    CpuTimeFrameComplete = t.cpuTimeFrameComplete,
                    HeightScale = t.heightScale,
                    WidthScale = t.widthScale,
                    SyncInterval = t.syncInterval
                };
            });
        }

        public const string ProfilerGetCountersToolId = "profiler-get-counters";
        [McpPluginTool
        (
            ProfilerGetCountersToolId,
            Title = "Profiler / Get Counters",
            ReadOnlyHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Sample `ProfilerRecorder` counters in a category (Render, Scripts, Memory, " +
            "Physics, Animation, Audio, ...). Pass explicit counter names to read a subset, or omit to enumerate all " +
            "counters in the category via `ProfilerRecorderHandle.GetAvailable`.")]
        [McpPluginSkillBody("Sample one or more `ProfilerRecorder` counters from a `ProfilerCategory`. " +
            "When `counters` is omitted, every counter currently available in the category is discovered via " +
            "`ProfilerRecorderHandle.GetAvailable`. Starts a recorder per counter, waits one editor tick for samples " +
            "to accumulate, then disposes each recorder.\n\n" +
            "## Inputs\n\n" +
            "- `category` — name of a `ProfilerCategory` (e.g. `Render`, `Scripts`, `Memory`, `Physics`, " +
            "`Physics2D`, `Animation`, `Audio`, `Lighting`, `Network`, `Gui` / `UI`, `Ai`, `Video`, `Loading`, " +
            "`Input`, `Vr`, `Internal`, `Particles`, `FileIO`, `VirtualTexturing`).\n" +
            "- `counters` (optional) — explicit counter names to sample.\n\n" +
            "## Frame-delay note\n\n" +
            "`ProfilerRecorder.CurrentValue` is often `0` on the first frame after `StartNew`. The tool waits one " +
            "editor tick (`EditorApplication.update` callback) and prefers `GetSample(0).Value` from the last " +
            "completed frame; if no samples accumulated it falls back to `CurrentValue`.")]
        [Description("Sample ProfilerRecorder counters in a category. Pass explicit names or omit to enumerate all in the category.")]
        public async Task<CounterReadResult> GetCounters
        (
            [Description("ProfilerCategory name. Examples: Render, Scripts, Memory, Physics, Animation, Audio.")]
            string category,
            [Description("Optional list of explicit counter names. Omit to read every counter available in the category.")]
            string[]? counters = null
        )
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException(Error.ParameterRequired(nameof(category)));

            var resolved = ResolveCategory(category);
            // Snapshot inputs on caller side before hopping to the main thread.
            var explicitCounters = counters?.Where(c => !string.IsNullOrWhiteSpace(c)).ToArray();

            return await MainThread.Instance.RunAsync(async () =>
            {
                var counterNames = explicitCounters is { Length: > 0 }
                    ? explicitCounters.ToList()
                    : DiscoverCountersInCategory(resolved);

                var result = new CounterReadResult
                {
                    Category = resolved.Name,
                    Counters = new List<CounterSample>()
                };

                if (counterNames.Count == 0)
                    return result;

                var recorders = new List<ProfilerRecorder>(counterNames.Count);
                try
                {
                    foreach (var name in counterNames)
                        recorders.Add(ProfilerRecorder.StartNew(resolved, name));

                    // Wait one editor tick so the recorders can accumulate a sample.
                    await WaitOneEditorTickAsync();

                    for (int i = 0; i < recorders.Count; i++)
                    {
                        var rec = recorders[i];
                        long value = 0;
                        if (rec.Valid && rec.Count > 0)
                            value = rec.GetSample(0).Value;
                        else if (rec.Valid)
                            value = rec.CurrentValue;

                        result.Counters.Add(new CounterSample
                        {
                            Name = counterNames[i],
                            Value = value,
                            Valid = rec.Valid,
                            UnitType = rec.Valid ? rec.UnitType.ToString() : "Unknown"
                        });
                    }
                }
                finally
                {
                    for (int i = 0; i < recorders.Count; i++)
                    {
                        try { recorders[i].Dispose(); } catch { /* swallow */ }
                    }
                }

                return result;
            }).Unwrap();
        }

        public const string ProfilerGetObjectMemoryToolId = "profiler-get-object-memory";
        [McpPluginTool
        (
            ProfilerGetObjectMemoryToolId,
            Title = "Profiler / Get Object Memory",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Return the runtime memory size of a single `UnityEngine.Object` resolved by a " +
            "scene hierarchy path or an asset path. Wraps `Profiler.GetRuntimeMemorySizeLong`.")]
        [McpPluginSkillBody("Resolve an object by scene hierarchy path first via `GameObject.Find`, falling back " +
            "to `AssetDatabase.LoadAssetAtPath`, then report the runtime memory size via " +
            "`Profiler.GetRuntimeMemorySizeLong`. Throws if the object cannot be resolved.")]
        [Description("Return runtime memory size for a scene or asset object via Profiler.GetRuntimeMemorySizeLong.")]
        public ObjectMemoryResult GetObjectMemory
        (
            [Description("Scene hierarchy path (e.g. '/Player/Mesh') or asset path (e.g. 'Assets/Textures/hero.png').")]
            string objectPath
        )
        {
            if (string.IsNullOrWhiteSpace(objectPath))
                throw new ArgumentException(Error.ParameterRequired(nameof(objectPath)));

            return MainThread.Instance.Run(() =>
            {
                var go = GameObject.Find(objectPath);
                if (go != null)
                {
                    var bytes = UProfiler.GetRuntimeMemorySizeLong(go);
                    return new ObjectMemoryResult
                    {
                        ObjectName = go.name,
                        ObjectType = go.GetType().Name,
                        SizeBytes = bytes,
                        SizeMb = Math.Round(bytes / (1024.0 * 1024.0), 3),
                        Source = "scene_hierarchy"
                    };
                }

                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(objectPath);
                if (asset != null)
                {
                    var bytes = UProfiler.GetRuntimeMemorySizeLong(asset);
                    return new ObjectMemoryResult
                    {
                        ObjectName = asset.name,
                        ObjectType = asset.GetType().Name,
                        SizeBytes = bytes,
                        SizeMb = Math.Round(bytes / (1024.0 * 1024.0), 3),
                        Source = "asset_database"
                    };
                }

                throw new InvalidOperationException(Error.ObjectNotFound(objectPath));
            });
        }

        // ----- helpers ---------------------------------------------------------------

        private static readonly string[] s_ValidCategories =
        {
            "Render", "Scripts", "Memory", "Physics",
#if UNITY_2022_2_OR_NEWER
            "Physics2D",
#endif
            "Animation", "Audio", "Lighting", "Network", "Gui", "UI", "Ai", "Video",
            "Loading", "Input", "Vr", "Internal", "Particles", "FileIO", "VirtualTexturing"
        };

        private static ProfilerCategory ResolveCategory(string name)
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "render": return ProfilerCategory.Render;
                case "scripts": return ProfilerCategory.Scripts;
                case "memory": return ProfilerCategory.Memory;
                case "physics": return ProfilerCategory.Physics;
#if UNITY_2022_2_OR_NEWER
                case "physics2d": return ProfilerCategory.Physics2D;
#endif
                case "animation": return ProfilerCategory.Animation;
                case "audio": return ProfilerCategory.Audio;
                case "lighting": return ProfilerCategory.Lighting;
                case "network": return ProfilerCategory.Network;
                case "gui":
                case "ui": return ProfilerCategory.Gui;
                case "ai": return ProfilerCategory.Ai;
                case "video": return ProfilerCategory.Video;
                case "loading": return ProfilerCategory.Loading;
                case "input": return ProfilerCategory.Input;
                case "vr": return ProfilerCategory.Vr;
                case "internal": return ProfilerCategory.Internal;
                case "particles": return ProfilerCategory.Particles;
                case "fileio": return ProfilerCategory.FileIO;
                case "virtualtexturing": return ProfilerCategory.VirtualTexturing;
                default:
                    throw new ArgumentException(Error.UnknownProfilerCategory(name, string.Join(", ", s_ValidCategories)));
            }
        }

        private static List<string> DiscoverCountersInCategory(ProfilerCategory category)
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            return handles
                .Select(h => ProfilerRecorderHandle.GetDescription(h))
                .Where(d => string.Equals(d.Category.Name, category.Name, StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Wait one Editor tick so that `ProfilerRecorder.CurrentValue` / `GetSample(0)` has data.
        /// Without this, every counter reads as 0 on the same frame it was started.
        /// </summary>
        private static Task WaitOneEditorTickAsync()
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int remaining = 2; // Two ticks to give the player loop a chance to run between editor updates.

            void Tick()
            {
                if (--remaining > 0) return;
                EditorApplication.update -= Tick;
                tcs.TrySetResult(true);
            }

            EditorApplication.update += Tick;
            try { EditorApplication.QueuePlayerLoopUpdate(); } catch { /* throttled editor — ignore */ }
            return tcs.Task;
        }
    }
}

namespace AIGD
{
    [System.ComponentModel.Description("Single Unity FrameTimingManager frame timing sample.")]
    public class FrameTimingResult
    {
        [System.ComponentModel.Description("True when FrameTimingManager produced at least one timing sample.")]
        public bool Available { get; set; }

        [System.ComponentModel.Description("Total CPU frame time in milliseconds.")]
        public double CpuFrameTimeMs { get; set; }

        [System.ComponentModel.Description("Main-thread CPU time in milliseconds (Unity 2022.2+).")]
        public double CpuMainThreadFrameTimeMs { get; set; }

        [System.ComponentModel.Description("Main-thread present-wait time in milliseconds (Unity 2022.2+).")]
        public double CpuMainThreadPresentWaitTimeMs { get; set; }

        [System.ComponentModel.Description("Render-thread CPU time in milliseconds (Unity 2022.2+).")]
        public double CpuRenderThreadFrameTimeMs { get; set; }

        [System.ComponentModel.Description("GPU frame time in milliseconds.")]
        public double GpuFrameTimeMs { get; set; }

        [System.ComponentModel.Description("CPU timestamp when frame submission started (Unity 2022.2+).")]
        public ulong FrameStartTimestamp { get; set; }

        [System.ComponentModel.Description("CPU timestamp of first GPU submit (Unity 2022.2+).")]
        public ulong FirstSubmitTimestamp { get; set; }

        [System.ComponentModel.Description("CPU timestamp when Present was called.")]
        public ulong CpuTimePresentCalled { get; set; }

        [System.ComponentModel.Description("CPU timestamp when frame completed.")]
        public ulong CpuTimeFrameComplete { get; set; }

        [System.ComponentModel.Description("Dynamic-resolution height scale.")]
        public float HeightScale { get; set; }

        [System.ComponentModel.Description("Dynamic-resolution width scale.")]
        public float WidthScale { get; set; }

        [System.ComponentModel.Description("VSync sync interval.")]
        public uint SyncInterval { get; set; }
    }

    [System.ComponentModel.Description("A single ProfilerRecorder counter sample.")]
    public class CounterSample
    {
        [System.ComponentModel.Description("Counter name.")]
        public string Name { get; set; } = string.Empty;

        [System.ComponentModel.Description("Sample value from the last completed frame.")]
        public long Value { get; set; }

        [System.ComponentModel.Description("True when the underlying ProfilerRecorder reports Valid.")]
        public bool Valid { get; set; }

        [System.ComponentModel.Description("ProfilerMarkerDataUnit name of the sample (e.g. TimeNanoseconds, Bytes, Count).")]
        public string UnitType { get; set; } = "Unknown";
    }

    [System.ComponentModel.Description("Batch result for 'profiler-get-counters'.")]
    public class CounterReadResult
    {
        [System.ComponentModel.Description("ProfilerCategory the counters were sampled from.")]
        public string Category { get; set; } = string.Empty;

        [System.ComponentModel.Description("Per-counter samples.")]
        public System.Collections.Generic.List<CounterSample> Counters { get; set; }
            = new System.Collections.Generic.List<CounterSample>();
    }

    [System.ComponentModel.Description("Runtime memory size for a single resolved Unity object.")]
    public class ObjectMemoryResult
    {
        [System.ComponentModel.Description("Resolved object name.")]
        public string ObjectName { get; set; } = string.Empty;

        [System.ComponentModel.Description("Concrete runtime type name of the resolved object.")]
        public string ObjectType { get; set; } = string.Empty;

        [System.ComponentModel.Description("Runtime memory size in bytes as reported by Profiler.GetRuntimeMemorySizeLong.")]
        public long SizeBytes { get; set; }

        [System.ComponentModel.Description("Same value as SizeBytes converted to megabytes (rounded to 3 decimals).")]
        public double SizeMb { get; set; }

        [System.ComponentModel.Description("Where the object was resolved: 'scene_hierarchy' or 'asset_database'.")]
        public string Source { get; set; } = string.Empty;
    }
}
