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
using System.Collections.Generic;
using System.ComponentModel;
using AIGD;
using com.AtelierAI.Uco.Framework;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_ToolGroups
    {
        public const string ToolsListGroupsId = "tools-list-groups";

        [UcoTool
        (
            ToolsListGroupsId,
            Title = "Tools / List Groups",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("List all uco tool groups discovered via the [ToolGroupAttribute] marker, " +
            "with current enabled state and tool membership. Pair with 'tools-set-group-enabled' to gate the " +
            "LLM-visible tool surface and reduce per-request token cost.")]
        [UcoSkillBody("Snapshot of every tool group known to the registry.\n\n" +
            "## Output\n\n" +
            "Array of `ToolGroupSnapshot { Name, Enabled, DefaultEnabled, Description, Tools, ToolCount }`. " +
            "Sorted by `Name` ascending.\n\n" +
            "## Discovery\n\n" +
            "Groups are populated at domain reload via `TypeCache.GetTypesWithAttribute<ToolGroupAttribute>()`. " +
            "If you add `[ToolGroupAttribute(\"my-group\")]` to a `Tool_*` partial-class root, " +
            "the new group will appear here on the next compile/reload.\n\n" +
            "## Enforcement\n\n" +
            "`Enabled = false` is a *soft hint* in the current implementation. Tool calls are not blocked at " +
            "the upstream `IRunTool.RunCallTool` boundary — see the `Note` field on `tools-set-group-enabled` " +
            "for the rationale.")]
        [Description("List all uco tool groups with current enabled state and tool membership. " +
            "Pass includeTools=false to omit the per-group tool name arrays (ToolCount is still reported).")]
        public ToolGroupSnapshot[] ListGroups(
            [Description("Populate each group's Tools array with its member tool names (default true). " +
                "Set false for a compact overview — ToolCount still reports the membership size.")]
            bool includeTools = true)
        {
            return MainThread.Instance.Run(() =>
            {
                var groups = ToolGroupRegistry.AllGroups();
                var result = new List<ToolGroupSnapshot>(groups.Count);

                foreach (var name in groups)
                {
                    var tools = ToolGroupRegistry.ToolsInGroup(name);
                    var toolsArray = new string[tools.Count];
                    for (var i = 0; i < tools.Count; i++)
                        toolsArray[i] = tools[i];
                    var aliases = ToolGroupRegistry.AliasesForGroup(name);
                    var aliasesArray = new string[aliases.Count];
                    for (var i = 0; i < aliases.Count; i++)
                        aliasesArray[i] = aliases[i];
                    var requestedEnabled = ToolGroupRegistry.IsGroupEnabled(name);

                    result.Add(new ToolGroupSnapshot
                    {
                        Name = name,
                        Enabled = requestedEnabled,
                        RequestedEnabled = requestedEnabled,
                        EffectiveEnabled = ToolGroupRegistry.IsGroupEffectivelyEnabled(name),
                        Aliases = aliasesArray,
                        DefaultEnabled = ToolGroupRegistry.GetDefaultEnabled(name),
                        Description = ToolGroupRegistry.GetDescription(name),
                        Tools = includeTools ? toolsArray : Array.Empty<string>(),
                        ToolCount = toolsArray.Length
                    });
                }

                return result.ToArray();
            });
        }
    }
}
