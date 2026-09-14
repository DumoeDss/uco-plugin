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
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildJobGetToolId = "build-job-get";

        [McpPluginTool
        (
            BuildJobGetToolId,
            Title = "Build / Get Job",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("Get the current state of a build job by ID. " +
            "Returns the same `BuildJobInfo` instance produced by '" + BuildPlayerToolId + "', with " +
            "`Status` updated as the build progresses (queued → running → succeeded|failed) and " +
            "completion fields (`CompletedAtUtc`, `DurationSeconds`, `TotalSizeBytes`, etc.) populated " +
            "once the build finishes. Returns `null` if the job ID is unknown.")]
        [McpPluginSkillBody("Poll a build job for status. " +
            "The registry is in-memory and survives for the life of the editor process; static state is " +
            "cleared on domain reload but `BuildPipeline.BuildPlayer` blocks the editor thread, so no " +
            "domain reload can occur mid-build.\n\n" +
            "## Returns\n\n" +
            "- The full `BuildJobInfo` record, or `null` when `jobId` does not exist.")]
        [Description("Get the current state of a build job by ID. Returns null when the job ID is unknown.")]
        public BuildJobInfo? GetJob
        (
            [Description("Job ID returned by '" + BuildPlayerToolId + "'.")]
            string jobId
        )
        {
            if (string.IsNullOrWhiteSpace(jobId))
                throw new ArgumentException("Job ID must be a non-empty string.", nameof(jobId));

            return BuildJobRegistry.Get(jobId);
        }
    }
}
