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
using System.Linq;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildPlayerToolId = "build-player";

        [McpPluginTool
        (
            BuildPlayerToolId,
            Title = "Build / Player",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Start a Unity player build for the requested target platform. " +
            "Returns a `BuildJobInfo` carrying a `JobId`. Inspect progress via '" + BuildJobGetToolId + "' " +
            "or list all jobs via '" + BuildJobListToolId + "'. Scenes default to `EditorBuildSettings.scenes` " +
            "(enabled rows) but can be overridden with the `scenes` argument.")]
        [McpPluginSkillBody("Start a Unity player build. " +
            "The build is dispatched on the next editor update tick, so the tool returns immediately with " +
            "`Status='queued'`. Poll '" + BuildJobGetToolId + "' to observe state transitions: queued → running → " +
            "succeeded|failed. Because `BuildPipeline.BuildPlayer` runs synchronously on the editor main thread, " +
            "the editor will be frozen for the duration of the build — Unity itself cannot interleave updates while " +
            "the build is in flight.\n\n" +
            "## Inputs\n\n" +
            "- `buildTarget` — one of `StandaloneWindows64`, `StandaloneOSX`, `StandaloneLinux64`, `Android`, " +
            "`iOS`, `WebGL`, plus aliases such as `windows64`, `osx`, `linux64`, `webgl`.\n" +
            "- `outputPath` — optional. Defaults to `Builds/{target}/{productName}` with the platform-appropriate extension.\n" +
            "- `buildOptions` — bitmask: 0=None, 1=Development, 2=AutoRunPlayer, 4=ShowBuiltPlayer, 8=ScriptsOnlyBuild.\n" +
            "- `scenes` — optional override. When omitted the tool uses every enabled row of `EditorBuildSettings.scenes`.\n\n" +
            "## Result\n\n" +
            "Returns the initial `BuildJobInfo` (`Status='queued'`). The same record is mutated in place as the " +
            "build progresses, so subsequent polls via '" + BuildJobGetToolId + "' return the latest snapshot.")]
        [Description("Start a Unity player build for the requested target platform and return a poll-able BuildJobInfo.")]
        public BuildJobInfo StartBuild
        (
            [Description("Target platform: 'StandaloneWindows64' | 'StandaloneOSX' | 'StandaloneLinux64' | 'Android' | 'iOS' | 'WebGL'.")]
            string buildTarget,
            [Description("Output path. Default 'Builds/{target}/{productName}'.")]
            string? outputPath = null,
            [Description("Build options bitmask. 0=None, 1=Development, 2=AutoRunPlayer, 4=ShowBuiltPlayer, 8=ScriptsOnlyBuild. Default 0.")]
            int buildOptions = 0,
            [Description("Override scenes list (project-relative .unity paths). Default uses EditorBuildSettings.scenes.")]
            string[]? scenes = null
        )
        {
            return MainThread.Instance.Run(() =>
            {
                if (!BuildTargetMap.TryResolve(buildTarget, out var target))
                    throw new ArgumentException(Error.InvalidBuildTarget(buildTarget,
                        string.Join(", ", BuildTargetMap.ValidNames)), nameof(buildTarget));

                var resolvedScenes = (scenes != null && scenes.Length > 0)
                    ? scenes
                    : GetDefaultEnabledScenes();

                // Validate every scene path exists as an asset to fail fast before scheduling.
                foreach (var scenePath in resolvedScenes)
                {
                    if (string.IsNullOrWhiteSpace(scenePath) || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException(Error.ScenePathInvalid(scenePath ?? "(null)"), nameof(scenes));
                    if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(scenePath) == null)
                        throw new ArgumentException(Error.SceneNotFound(scenePath), nameof(scenes));
                }

                var resolvedOutput = string.IsNullOrWhiteSpace(outputPath)
                    ? BuildTargetMap.GetDefaultOutputPath(target, PlayerSettings.productName)
                    : outputPath!;

                if (string.IsNullOrWhiteSpace(resolvedOutput))
                    throw new ArgumentException(Error.OutputPathEmpty(), nameof(outputPath));

                var job = new BuildJobInfo
                {
                    JobId = BuildJobRegistry.NewJobId(),
                    Status = "queued",
                    BuildTarget = target.ToString(),
                    OutputPath = resolvedOutput,
                    StartedAtUtc = FormatUtc(DateTime.UtcNow),
                };
                BuildJobRegistry.Register(job);

                var options = new BuildPlayerOptions
                {
                    target = target,
                    targetGroup = BuildTargetMap.GetTargetGroup(target),
                    locationPathName = resolvedOutput,
                    scenes = resolvedScenes,
                    options = (BuildOptions)buildOptions,
                };

                // BuildPipeline.BuildPlayer must run on the main thread and blocks until done.
                // Defer to the next editor tick so this tool returns immediately with Status='queued'
                // and the caller can poll via build-job-get.
                EditorApplication.delayCall += () => RunBuildOnMainThread(job, options);

                if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Info))
                    Debug.Log($"[Build] Queued job '{job.JobId}' for {target} -> {resolvedOutput}");

                return job;
            });
        }

        static void RunBuildOnMainThread(BuildJobInfo job, BuildPlayerOptions options)
        {
            job.Status = "running";
            job.StartedAtUtc = FormatUtc(DateTime.UtcNow);
            var startedTicks = DateTime.UtcNow;

            try
            {
                // Ensure the output directory exists before invoking BuildPipeline so the
                // build pipeline does not fail on a missing parent folder.
                try
                {
                    var dir = Path.GetDirectoryName(options.locationPathName);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch
                {
                    // Non-fatal — let BuildPipeline surface the real error if the path is unusable.
                }

                var report = BuildPipeline.BuildPlayer(options);
                var completed = DateTime.UtcNow;
                var summary = report.summary;

                job.CompletedAtUtc = FormatUtc(completed);
                job.DurationSeconds = (completed - startedTicks).TotalSeconds;
                job.TotalSizeBytes = (long)summary.totalSize;
                job.TotalErrors = summary.totalErrors;
                job.TotalWarnings = summary.totalWarnings;

                if (summary.result == BuildResult.Succeeded)
                {
                    job.Status = "succeeded";
                    if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Info))
                    {
                        Debug.Log($"[Build] Job '{job.JobId}' succeeded ({summary.totalSize} bytes, " +
                                  $"{job.DurationSeconds:F1}s) -> {job.OutputPath}");
                    }
                }
                else
                {
                    job.Status = "failed";
                    job.Error = $"BuildResult={summary.result}, errors={summary.totalErrors}";
                    if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Error))
                        Debug.LogError($"[Build] Job '{job.JobId}' failed: {job.Error}");
                }
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.CompletedAtUtc = FormatUtc(DateTime.UtcNow);
                job.DurationSeconds = (DateTime.UtcNow - startedTicks).TotalSeconds;
                job.Error = ex.Message;

                if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Error))
                    Debug.LogError($"[Build] Job '{job.JobId}' threw: {ex}");
            }
        }
    }
}
