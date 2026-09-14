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
using com.AtelierAI.Uco.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Build
    {
        public const string BuildJobListToolId = "build-job-list";

        [UcoTool
        (
            BuildJobListToolId,
            Title = "Build / List Jobs",
            ReadOnlyHint = true,
            IdempotentHint = true,
            ExecutionAffinity = ToolExecutionAffinity.Background,
            ThreadSafeRead = true
        )]
        [UcoSkillDescription("List every build job registered in this editor session. " +
            "Set `includeCompleted=false` to drop succeeded/failed jobs and return only active " +
            "(`queued` or `running`) entries.")]
        [UcoSkillBody("List build jobs. " +
            "The list projects the project-local durable operation registry. Each entry is the same " +
            "`BuildJobInfo` produced by '" + BuildPlayerToolId + "', so " +
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
