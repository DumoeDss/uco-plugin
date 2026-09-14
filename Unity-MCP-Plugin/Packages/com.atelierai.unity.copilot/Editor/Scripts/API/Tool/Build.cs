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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using com.IvanMurzak.McpPlugin;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [McpPluginToolType]
    public partial class Tool_Build
    {
        // -----------------------------------------------------------------
        // DTOs
        // -----------------------------------------------------------------

        public class BuildJobInfo
        {
            [Description("Unique job ID for polling via build-job-get.")]
            public string JobId { get; set; } = "";

            [Description("'queued' | 'running' | 'succeeded' | 'failed'")]
            public string Status { get; set; } = "";

            [Description("Target platform name.")]
            public string BuildTarget { get; set; } = "";

            [Description("Resolved output path.")]
            public string OutputPath { get; set; } = "";

            [Description("Started timestamp (ISO 8601 UTC).")]
            public string StartedAtUtc { get; set; } = "";

            [Description("Completed timestamp (ISO 8601 UTC). Null until done.")]
            public string? CompletedAtUtc { get; set; }

            [Description("Build duration in seconds. Null until done.")]
            public double? DurationSeconds { get; set; }

            [Description("BuildSummary.totalSize in bytes. Null until done.")]
            public long? TotalSizeBytes { get; set; }

            [Description("Number of errors from BuildSummary. Null until done.")]
            public int? TotalErrors { get; set; }

            [Description("Number of warnings from BuildSummary.")]
            public int? TotalWarnings { get; set; }

            [Description("Error message when Status=failed.")]
            public string? Error { get; set; }
        }

        public class PlatformSwitchResult
        {
            [Description("Whether the active build target now matches the requested target.")]
            public bool Switched { get; set; }

            [Description("Build target the editor was on before the switch.")]
            public string PreviousBuildTarget { get; set; } = "";

            [Description("Build target the editor is on after the switch.")]
            public string ActiveBuildTarget { get; set; } = "";

            [Description("Duration of the switch call in seconds (synchronous, may block for minutes).")]
            public double DurationSeconds { get; set; }
        }

        public class BuildSceneEntry
        {
            [Description("Project-relative path to the .unity scene asset.")]
            public string ScenePath { get; set; } = "";

            [Description("Whether the scene is enabled in EditorBuildSettings.")]
            public bool Enabled { get; set; }

            [Description("GUID of the scene asset (empty when the asset cannot be resolved).")]
            public string Guid { get; set; } = "";
        }

        // -----------------------------------------------------------------
        // Error message helpers (shared across all build-* tools).
        // -----------------------------------------------------------------

        public static class Error
        {
            public static string InvalidBuildTarget(string value, string validList)
                => $"Invalid build target '{value}'. Valid values: {validList}.";

            public static string SceneNotFound(string scenePath)
                => $"Scene asset not found at path '{scenePath}'. Provide a project-relative '.unity' path that exists on disk.";

            public static string ScenePathInvalid(string scenePath)
                => $"Scene path '{scenePath}' is invalid. It must be non-empty and end with '.unity'.";

            public static string JobNotFound(string jobId)
                => $"Build job '{jobId}' not found.";

            public static string OutputPathEmpty()
                => "Output path resolved to an empty string. Provide a non-empty 'outputPath' or ensure 'PlayerSettings.productName' is set.";
        }

        // -----------------------------------------------------------------
        // BuildTarget string mapping. Centralised so every build-* tool
        // accepts the same set of identifiers and surfaces the same error.
        // -----------------------------------------------------------------

        internal static class BuildTargetMap
        {
            // Canonical names exposed to the LLM. Aliases are accepted via TryResolve.
            public static readonly string[] ValidNames = new[]
            {
                "StandaloneWindows64",
                "StandaloneWindows",
                "StandaloneOSX",
                "StandaloneLinux64",
                "Android",
                "iOS",
                "WebGL",
                "WSAPlayer",
                "tvOS",
#if UNITY_2023_2_OR_NEWER
                "VisionOS",
#endif
            };

            public static bool TryResolve(string? name, out BuildTarget target)
            {
                if (string.IsNullOrEmpty(name))
                {
                    target = EditorUserBuildSettings.activeBuildTarget;
                    return true;
                }

                switch (name!.ToLowerInvariant())
                {
                    case "windows64":
                    case "standalonewindows64":
                        target = BuildTarget.StandaloneWindows64;
                        return true;
                    case "windows":
                    case "windows32":
                    case "standalonewindows":
                        target = BuildTarget.StandaloneWindows;
                        return true;
                    case "osx":
                    case "macos":
                    case "standaloneosx":
                        target = BuildTarget.StandaloneOSX;
                        return true;
                    case "linux64":
                    case "linux":
                    case "standalonelinux64":
                        target = BuildTarget.StandaloneLinux64;
                        return true;
                    case "android":
                        target = BuildTarget.Android;
                        return true;
                    case "ios":
                        target = BuildTarget.iOS;
                        return true;
                    case "webgl":
                        target = BuildTarget.WebGL;
                        return true;
                    case "uwp":
                    case "wsa":
                    case "wsaplayer":
                        target = BuildTarget.WSAPlayer;
                        return true;
                    case "tvos":
                        target = BuildTarget.tvOS;
                        return true;
#if UNITY_2023_2_OR_NEWER
                    case "visionos":
                        target = BuildTarget.VisionOS;
                        return true;
#endif
                    default:
                        if (Enum.TryParse(name, true, out target))
                            return true;
                        target = default;
                        return false;
                }
            }

            public static BuildTargetGroup GetTargetGroup(BuildTarget target)
            {
                switch (target)
                {
                    case BuildTarget.StandaloneWindows:
                    case BuildTarget.StandaloneWindows64:
                    case BuildTarget.StandaloneOSX:
                    case BuildTarget.StandaloneLinux64:
                        return BuildTargetGroup.Standalone;
                    case BuildTarget.iOS: return BuildTargetGroup.iOS;
                    case BuildTarget.Android: return BuildTargetGroup.Android;
                    case BuildTarget.WebGL: return BuildTargetGroup.WebGL;
                    case BuildTarget.WSAPlayer: return BuildTargetGroup.WSA;
                    case BuildTarget.tvOS: return BuildTargetGroup.tvOS;
#if UNITY_2023_2_OR_NEWER
                    case BuildTarget.VisionOS: return BuildTargetGroup.VisionOS;
#endif
                    default: return BuildTargetGroup.Unknown;
                }
            }

            public static string GetDefaultOutputPath(BuildTarget target, string productName)
            {
                var safeName = string.IsNullOrEmpty(productName) ? "Player" : productName;
                var basePath = $"Builds/{target}";
                switch (target)
                {
                    case BuildTarget.StandaloneWindows:
                    case BuildTarget.StandaloneWindows64:
                        return $"{basePath}/{safeName}.exe";
                    case BuildTarget.StandaloneOSX:
                        return $"{basePath}/{safeName}.app";
                    case BuildTarget.StandaloneLinux64:
                        return $"{basePath}/{safeName}.x86_64";
                    case BuildTarget.Android:
                        return EditorUserBuildSettings.buildAppBundle
                            ? $"{basePath}/{safeName}.aab"
                            : $"{basePath}/{safeName}.apk";
                    default:
                        return $"{basePath}/{safeName}";
                }
            }
        }

        // -----------------------------------------------------------------
        // In-memory job registry. Persists for the lifetime of the editor
        // process (cleared on domain reload, which is fine since
        // BuildPipeline.BuildPlayer blocks the editor thread and therefore
        // no reload can occur mid-build).
        // -----------------------------------------------------------------

        internal static class BuildJobRegistry
        {
            private static readonly ConcurrentDictionary<string, BuildJobInfo> s_jobs = new();

            public static string NewJobId() => Guid.NewGuid().ToString("N");

            public static BuildJobInfo Register(BuildJobInfo job)
            {
                s_jobs[job.JobId] = job;
                return job;
            }

            public static BuildJobInfo? Get(string id)
                => s_jobs.TryGetValue(id, out var j) ? j : null;

            public static BuildJobInfo[] All()
                => s_jobs.Values.ToArray();

            public static BuildJobInfo[] Filter(bool includeCompleted)
            {
                if (includeCompleted)
                    return s_jobs.Values.ToArray();

                return s_jobs.Values
                    .Where(j => j.Status == "queued" || j.Status == "running")
                    .ToArray();
            }
        }

        // -----------------------------------------------------------------
        // Shared helpers used by partial files.
        // -----------------------------------------------------------------

        internal static string FormatUtc(DateTime utc)
            => utc.ToUniversalTime().ToString("O");

        internal static string[] GetDefaultEnabledScenes()
        {
            return EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();
        }

        internal static List<BuildSceneEntry> SnapshotScenes()
        {
            var scenes = EditorBuildSettings.scenes;
            var result = new List<BuildSceneEntry>(scenes.Length);
            foreach (var s in scenes)
            {
                var guidStr = s.guid.ToString();
                if (guidStr == "00000000000000000000000000000000")
                    guidStr = string.Empty;

                result.Add(new BuildSceneEntry
                {
                    ScenePath = s.path ?? string.Empty,
                    Enabled = s.enabled,
                    Guid = guidStr,
                });
            }
            return result;
        }
    }
}
