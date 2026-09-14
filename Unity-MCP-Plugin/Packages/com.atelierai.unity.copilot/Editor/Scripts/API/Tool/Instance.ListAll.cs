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
using System.Linq;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Instance
    {
        public const string InstanceListAllToolId = "instance-list-all";

        [McpPluginTool
        (
            InstanceListAllToolId,
            Title = "Instance / List All",
            ReadOnlyHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("List all Unity Editor instances registered on this machine plus identify the " +
            "calling Editor. Each entry carries port, project path, Unity version, PID, instance id and a liveness " +
            "flag (verified by checking that the PID is still running).")]
        [McpPluginSkillBody("Soft discovery of every Unity Editor that has registered itself with this machine's " +
            "MCP plugin. Each entry includes port, project path, Unity version, PID, stable instance id and an " +
            "`IsAlive` flag.\n\n" +
            "## Inputs\n\n" +
            "- `includeStale` (default `false`) — include rows whose registry file has not been refreshed in over " +
            "5 minutes. Useful for diagnosing crashed Editors that did not get a chance to clean up their entry.\n\n" +
            "## Behavior\n\n" +
            "Reads every `*.json` file under `%TEMP%/unity-mcp-instances/`. The calling Editor's row is marked " +
            "with `IsSelf=true`. `IsAlive` is determined by `Process.GetProcessById(pid)`.\n\n" +
            "## Limitation\n\n" +
            "This is discovery only — you can see which sibling Editors exist, but you cannot route tool calls to " +
            "them. Cross-instance routing requires server-side changes in the external NuGet " +
            "`com.IvanMurzak.McpPlugin` package and is not yet implemented.")]
        [Description("List all Unity Editor instances registered on this machine plus identify the calling Editor.")]
        public UnityInstanceListResult ListAll
        (
            [Description("Include stale entries (Editor exited without cleanup). Default false.")]
            bool includeStale = false
        )
        {
            return MainThread.Instance.Run(() =>
            {
                var entries = UnityInstanceRegistry.List(includeStale: includeStale).ToArray();
                var self = entries.FirstOrDefault(e => e.IsSelf);
                var currentId = self?.InstanceId
                                ?? UnityInstanceRegistry.BuildSelfEntry().InstanceId;
                return new UnityInstanceListResult
                {
                    Instances = entries,
                    CurrentInstanceId = currentId,
                };
            });
        }
    }
}
