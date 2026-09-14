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
using System.ComponentModel;
using com.IvanMurzak.McpPlugin;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildJobListToolId = "build-job-list";

        [McpPluginTool
        (
            BuildJobListToolId,
            Title = "Build / List Jobs",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [McpPluginSkillDescription("List every build job registered in this editor session. " +
            "Set `includeCompleted=false` to drop succeeded/failed jobs and return only active " +
            "(`queued` or `running`) entries.")]
        [McpPluginSkillBody("List build jobs. " +
            "The job registry lives in-memory for the editor process lifetime and is cleared on domain " +
            "reload. Each entry is the same `BuildJobInfo` produced by '" + BuildPlayerToolId + "', so " +
            "callers can rely on the same fields and lifecycle semantics.\n\n" +
            "## Inputs\n\n" +
            "- `includeCompleted` (default `true`) — when `false`, only jobs with `Status='queued'` or " +
            "`Status='running'` are returned.")]
        [Description("List every build job currently tracked by the editor.")]
        public BuildJobInfo[] ListJobs
        (
            [Description("When false, only return jobs with Status='queued' or 'running'. Default: true.")]
            bool includeCompleted = true
        )
        {
            return BuildJobRegistry.Filter(includeCompleted);
        }
    }
}
