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
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Profiler
    {
        public const string ProfilerMemorySnapshotTakeToolId = "profiler-memory-snapshot-take";
        [McpPluginTool
        (
            ProfilerMemorySnapshotTakeToolId,
            Title = "Profiler / Memory Snapshot / Take",
            DestructiveHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Capture a Unity memory snapshot to a `.snap` file. Requires the " +
            "'com.unity.memoryprofiler' package — if it is not installed, the tool returns a failure result " +
            "(it does NOT throw). Reflection-only invocation so we don't take a hard dependency.")]
        [McpPluginSkillBody("Take a memory snapshot via the `com.unity.memoryprofiler` package, invoking " +
            "`MemoryProfiler.TakeSnapshot` (or its variant with screenshot callback) by reflection. If no " +
            "`snapshotPath` is supplied, a timestamped file is created under " +
            "`Application.temporaryCachePath/MemoryCaptures/`.\n\n" +
            "## Outputs\n\n" +
            "Returns a `MemorySnapshotResult` with `Ok=true` and the resolved path on success, or `Ok=false` and " +
            "an `Error` message on failure (including 'package not installed').\n\n" +
            "## Async + timeout\n\n" +
            "Waits up to 30 seconds for the underlying snapshot callback to fire.")]
        [Description("Capture a Unity memory snapshot to a .snap file (requires com.unity.memoryprofiler).")]
        public async Task<MemorySnapshotResult> TakeMemorySnapshot
        (
            [Description("Output path for the .snap file. Optional — when empty, writes to Application.temporaryCachePath/MemoryCaptures/ with a timestamped name.")]
            string? snapshotPath = null
        )
        {
            if (!IsMemoryProfilerPackageAvailable())
                return new MemorySnapshotResult { Ok = false, Error = Error.MemoryProfilerPackageMissing };

            return await MainThread.Instance.RunAsync(async () =>
            {
                var targetPath = snapshotPath;
                if (string.IsNullOrEmpty(targetPath))
                {
                    var dir = Path.Combine(Application.temporaryCachePath, "MemoryCaptures");
                    Directory.CreateDirectory(dir);
                    targetPath = Path.Combine(dir, $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.snap");
                }

                MethodInfo? takeMethod;
                Type? memoryProfilerType;
                try
                {
                    (takeMethod, memoryProfilerType) = ResolveTakeSnapshotMethod();
                }
                catch (Exception ex)
                {
                    return new MemorySnapshotResult { Ok = false, Error = $"Failed to resolve MemoryProfiler.TakeSnapshot: {ex.Message}" };
                }

                if (takeMethod == null || memoryProfilerType == null)
                    return new MemorySnapshotResult { Ok = false, Error = "Could not find MemoryProfiler.TakeSnapshot via reflection. Package API may have changed." };

                var tcs = new TaskCompletionSource<MemorySnapshotResult>(TaskCreationOptions.RunContinuationsAsynchronously);

                Action<string, bool> callback = (path, success) =>
                {
                    if (success)
                    {
                        var fi = new FileInfo(path);
                        tcs.TrySetResult(new MemorySnapshotResult
                        {
                            Ok = true,
                            Path = path,
                            SizeBytes = fi.Exists ? fi.Length : 0,
                            SizeMb = fi.Exists ? Math.Round(fi.Length / (1024.0 * 1024.0), 2) : 0
                        });
                    }
                    else
                    {
                        tcs.TrySetResult(new MemorySnapshotResult
                        {
                            Ok = false,
                            Path = path,
                            Error = $"MemoryProfiler.TakeSnapshot reported failure for path: {path}"
                        });
                    }
                };

                try
                {
                    var paramInfos = takeMethod.GetParameters();
                    if (paramInfos.Length == 4)
                        takeMethod.Invoke(null, new object?[] { targetPath, callback, null, 0u });
                    else if (paramInfos.Length == 2)
                        takeMethod.Invoke(null, new object?[] { targetPath, callback });
                    else
                        return new MemorySnapshotResult { Ok = false, Error = $"MemoryProfiler.TakeSnapshot has unexpected {paramInfos.Length} parameters." };
                }
                catch (Exception ex)
                {
                    return new MemorySnapshotResult { Ok = false, Error = $"MemoryProfiler.TakeSnapshot invocation threw: {ex.InnerException?.Message ?? ex.Message}" };
                }

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30));
                var completed = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completed == timeoutTask)
                    return new MemorySnapshotResult { Ok = false, Error = "MemoryProfiler.TakeSnapshot timed out after 30 seconds." };

                return await tcs.Task;
            }).Unwrap();
        }

        public const string ProfilerMemorySnapshotListToolId = "profiler-memory-snapshot-list";
        [McpPluginTool
        (
            ProfilerMemorySnapshotListToolId,
            Title = "Profiler / Memory Snapshot / List",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("List `.snap` memory snapshot files. When `searchPath` is empty, " +
            "scans `Application.temporaryCachePath/MemoryCaptures/` and `<projectRoot>/MemoryCaptures/`. " +
            "Requires com.unity.memoryprofiler.")]
        [McpPluginSkillBody("Enumerate every `.snap` file under the provided `searchPath` (or the default " +
            "snapshot directories when omitted). Returns size and creation time for each file.\n\n" +
            "Returns `Ok=false` with `Error` set when the memory profiler package is missing.")]
        [Description("List .snap memory snapshot files in known or supplied directories.")]
        public MemorySnapshotListResult ListMemorySnapshots
        (
            [Description("Optional directory to search for .snap files. When empty, scans the default snapshot directories.")]
            string? searchPath = null
        )
        {
            if (!IsMemoryProfilerPackageAvailable())
                return new MemorySnapshotListResult { Ok = false, Error = Error.MemoryProfilerPackageMissing };

            // Filesystem only — no need to bounce to main thread.
            var dirs = new List<string>();
            if (!string.IsNullOrEmpty(searchPath))
            {
                dirs.Add(searchPath!);
            }
            else
            {
                dirs.Add(Path.Combine(Application.temporaryCachePath, "MemoryCaptures"));
                dirs.Add(Path.Combine(Application.dataPath, "..", "MemoryCaptures"));
            }

            var snapshots = new List<MemorySnapshotInfo>();
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.snap"))
                {
                    var fi = new FileInfo(file);
                    snapshots.Add(new MemorySnapshotInfo
                    {
                        Path = fi.FullName,
                        SizeBytes = fi.Length,
                        SizeMb = Math.Round(fi.Length / (1024.0 * 1024.0), 2),
                        CreatedUtc = fi.CreationTimeUtc.ToString("o")
                    });
                }
            }

            return new MemorySnapshotListResult
            {
                Ok = true,
                Snapshots = snapshots,
                SearchedDirs = dirs
            };
        }

        public const string ProfilerMemorySnapshotCompareToolId = "profiler-memory-snapshot-compare";
        [McpPluginTool
        (
            ProfilerMemorySnapshotCompareToolId,
            Title = "Profiler / Memory Snapshot / Compare",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Compare two `.snap` files at the file-metadata level (size delta, creation-time " +
            "delta). For object-level diff, open both snapshots in the Memory Profiler window. Requires " +
            "com.unity.memoryprofiler.")]
        [McpPluginSkillBody("File-level snapshot diff. Reads `FileInfo` for both paths and returns sizes plus the " +
            "delta. The package gating is enforced so this stays consistent with `take` / `list`, even though the " +
            "comparison itself is filesystem-only.")]
        [Description("Compare two .snap files at file-metadata level (size and creation-time delta).")]
        public SnapshotCompareResult CompareMemorySnapshots
        (
            [Description("Path to the first .snap file.")]
            string snapshotA,
            [Description("Path to the second .snap file.")]
            string snapshotB
        )
        {
            if (!IsMemoryProfilerPackageAvailable())
                return new SnapshotCompareResult { Ok = false, Error = Error.MemoryProfilerPackageMissing };

            if (string.IsNullOrWhiteSpace(snapshotA))
                return new SnapshotCompareResult { Ok = false, Error = Error.ParameterRequired(nameof(snapshotA)) };
            if (string.IsNullOrWhiteSpace(snapshotB))
                return new SnapshotCompareResult { Ok = false, Error = Error.ParameterRequired(nameof(snapshotB)) };

            if (!File.Exists(snapshotA))
                return new SnapshotCompareResult { Ok = false, Error = Error.SnapshotFileNotFound(snapshotA) };
            if (!File.Exists(snapshotB))
                return new SnapshotCompareResult { Ok = false, Error = Error.SnapshotFileNotFound(snapshotB) };

            var fiA = new FileInfo(snapshotA);
            var fiB = new FileInfo(snapshotB);

            return new SnapshotCompareResult
            {
                Ok = true,
                SnapshotA = new MemorySnapshotInfo
                {
                    Path = fiA.FullName,
                    SizeBytes = fiA.Length,
                    SizeMb = Math.Round(fiA.Length / (1024.0 * 1024.0), 2),
                    CreatedUtc = fiA.CreationTimeUtc.ToString("o")
                },
                SnapshotB = new MemorySnapshotInfo
                {
                    Path = fiB.FullName,
                    SizeBytes = fiB.Length,
                    SizeMb = Math.Round(fiB.Length / (1024.0 * 1024.0), 2),
                    CreatedUtc = fiB.CreationTimeUtc.ToString("o")
                },
                SizeDeltaBytes = fiB.Length - fiA.Length,
                SizeDeltaMb = Math.Round((fiB.Length - fiA.Length) / (1024.0 * 1024.0), 2),
                TimeDeltaSeconds = (fiB.CreationTimeUtc - fiA.CreationTimeUtc).TotalSeconds,
                Note = "For object-level comparison, open both snapshots in the Memory Profiler window."
            };
        }

        // ----- reflection helpers ----------------------------------------------------

        /// <summary>
        /// Locate `Unity.MemoryProfiler.MemoryProfiler.TakeSnapshot` (or the older entry-point) and pick whichever
        /// overload we know how to call. We try the 4-parameter overload first (path, callback, screenshotCallback,
        /// captureFlags) before falling back to the 2-parameter one.
        /// </summary>
        private static (MethodInfo? Method, Type? OwnerType) ResolveTakeSnapshotMethod()
        {
            var type = Type.GetType("Unity.MemoryProfiler.MemoryProfiler, Unity.MemoryProfiler.Editor", throwOnError: false);
            if (type == null) return (null, null);

            // 2-parameter overload — always present in supported versions.
            var simple = type.GetMethod("TakeSnapshot",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string), typeof(Action<string, bool>) },
                modifiers: null);

            // 4-parameter overload — present in versions exposing DebugScreenCapture.
            var debugScreenCaptureType = Type.GetType(
                "Unity.Profiling.Memory.Experimental.DebugScreenCapture, Unity.MemoryProfiler.Editor",
                throwOnError: false);

            MethodInfo? full = null;
            if (debugScreenCaptureType != null)
            {
                var screenshotCallbackType = typeof(Action<,,>).MakeGenericType(
                    typeof(string), typeof(bool), debugScreenCaptureType);
                full = type.GetMethod("TakeSnapshot",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string), typeof(Action<string, bool>), screenshotCallbackType, typeof(uint) },
                    modifiers: null);
            }

            return (full ?? simple, type);
        }
    }
}

namespace AIGD
{
    [System.ComponentModel.Description("Metadata for a single .snap memory snapshot file.")]
    public class MemorySnapshotInfo
    {
        [System.ComponentModel.Description("Absolute path to the .snap file.")]
        public string Path { get; set; } = string.Empty;

        [System.ComponentModel.Description("File size in bytes.")]
        public long SizeBytes { get; set; }

        [System.ComponentModel.Description("File size in megabytes (rounded to 2 decimals).")]
        public double SizeMb { get; set; }

        [System.ComponentModel.Description("File creation time in ISO-8601 UTC ('o' round-trip format).")]
        public string CreatedUtc { get; set; } = string.Empty;
    }

    [System.ComponentModel.Description("Result for 'profiler-memory-snapshot-take'.")]
    public class MemorySnapshotResult
    {
        [System.ComponentModel.Description("True when the snapshot was captured successfully.")]
        public bool Ok { get; set; }

        [System.ComponentModel.Description("Resolved snapshot file path.")]
        public string? Path { get; set; }

        [System.ComponentModel.Description("File size in bytes when Ok is true.")]
        public long SizeBytes { get; set; }

        [System.ComponentModel.Description("File size in megabytes when Ok is true.")]
        public double SizeMb { get; set; }

        [System.ComponentModel.Description("Failure description when Ok is false.")]
        public string? Error { get; set; }
    }

    [System.ComponentModel.Description("Result for 'profiler-memory-snapshot-list'.")]
    public class MemorySnapshotListResult
    {
        [System.ComponentModel.Description("True when the listing completed (package present).")]
        public bool Ok { get; set; }

        [System.ComponentModel.Description("Failure description when Ok is false.")]
        public string? Error { get; set; }

        [System.ComponentModel.Description("Snapshot files discovered.")]
        public System.Collections.Generic.List<MemorySnapshotInfo> Snapshots { get; set; }
            = new System.Collections.Generic.List<MemorySnapshotInfo>();

        [System.ComponentModel.Description("Directories that were scanned.")]
        public System.Collections.Generic.List<string> SearchedDirs { get; set; }
            = new System.Collections.Generic.List<string>();
    }

    [System.ComponentModel.Description("Result for 'profiler-memory-snapshot-compare'.")]
    public class SnapshotCompareResult
    {
        [System.ComponentModel.Description("True when the comparison completed successfully.")]
        public bool Ok { get; set; }

        [System.ComponentModel.Description("Failure description when Ok is false.")]
        public string? Error { get; set; }

        [System.ComponentModel.Description("Metadata for the first snapshot.")]
        public MemorySnapshotInfo? SnapshotA { get; set; }

        [System.ComponentModel.Description("Metadata for the second snapshot.")]
        public MemorySnapshotInfo? SnapshotB { get; set; }

        [System.ComponentModel.Description("Bytes-level size delta (SnapshotB.SizeBytes - SnapshotA.SizeBytes).")]
        public long SizeDeltaBytes { get; set; }

        [System.ComponentModel.Description("Megabyte-level size delta.")]
        public double SizeDeltaMb { get; set; }

        [System.ComponentModel.Description("Creation-time delta in seconds (SnapshotB - SnapshotA).")]
        public double TimeDeltaSeconds { get; set; }

        [System.ComponentModel.Description("Human-readable note about the scope of the comparison.")]
        public string Note { get; set; } = string.Empty;
    }
}
