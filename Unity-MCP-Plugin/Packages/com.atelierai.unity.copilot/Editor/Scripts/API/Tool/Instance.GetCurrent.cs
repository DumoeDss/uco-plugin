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
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Instance
    {
        public const string InstanceGetCurrentToolId = "instance-get-current";

        [McpPluginTool
        (
            InstanceGetCurrentToolId,
            Title = "Instance / Get Current",
            ReadOnlyHint = true,
            IdempotentHint = true,
            Enabled = false
        )]
        [McpPluginSkillDescription("Return identity metadata of the calling Unity Editor: port, project path, " +
            "project name, Unity version, process id, and stable instance id. Useful when multiple Editors are " +
            "running on the same machine and the LLM needs to know which one it just connected to.")]
        [McpPluginSkillBody("Return identity metadata of the calling Unity Editor. " +
            "Multi-instance discovery is soft: this tool reports who 'I' am — pair with 'instance-list-all' " +
            "to see the sibling Editors.\n\n" +
            "## Behavior\n\n" +
            "Builds a fresh `UnityInstanceEntry` from the current Editor's state on the main thread. " +
            "`Port` is read from `UnityCopilotPluginEditor.Port`. `InstanceId` is " +
            "`{ProjectName}@{first 8 hex chars of SHA256(projectPath)}` — stable across Editor restarts.\n\n" +
            "## Limitation\n\n" +
            "Discovery only — there is no cross-instance routing. Sibling Editors are visible via " +
            "the `editor://unity-instances` resource and the 'instance-list-all' tool, but invoking a tool " +
            "against a sibling Editor's port requires server-side routing (not yet implemented).")]
        [Description("Return identity metadata of the calling Unity Editor: port, project path, project name, " +
            "Unity version, process id, and stable instance id.")]
        public UnityInstanceEntry GetCurrent()
        {
            return MainThread.Instance.Run(() => UnityInstanceRegistry.BuildSelfEntry());
        }
    }
}
