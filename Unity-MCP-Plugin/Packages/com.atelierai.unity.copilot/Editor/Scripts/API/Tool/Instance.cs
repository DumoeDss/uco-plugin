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
using com.AtelierAI.Unity.Copilot.Editor.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// Partial-class root for the <c>instance-*</c> tool family — multi-Editor
    /// discovery and self-identification.
    /// </summary>
    /// <remarks>
    /// These tools are <b>discovery-only</b>. They report which Unity Editors
    /// are currently registered on this machine and identify the caller; they
    /// do <b>not</b> route or proxy tool calls between Editors. True
    /// cross-instance routing requires server-side support in the external
    /// NuGet <c>com.IvanMurzak.McpPlugin</c> package and is intentionally out
    /// of scope for this layer.
    /// </remarks>
    [McpPluginToolType]
    public partial class Tool_Instance
    {
        public class UnityInstanceListResult
        {
            [Description("All Unity Editor instances discovered via the on-disk registry.")]
            public UnityInstanceEntry[] Instances { get; set; } = System.Array.Empty<UnityInstanceEntry>();

            [Description("Stable instance id of the calling Editor (the one whose row has IsSelf=true).")]
            public string CurrentInstanceId { get; set; } = string.Empty;

            [Description("Note about future routing capabilities not yet implemented.")]
            public string Note { get; set; } =
                "Discovery only. Cross-instance tool routing requires server-side support and is not yet implemented.";
        }
    }
}
