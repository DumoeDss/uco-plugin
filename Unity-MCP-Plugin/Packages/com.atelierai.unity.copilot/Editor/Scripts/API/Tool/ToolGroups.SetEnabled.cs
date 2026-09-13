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
using AIGD;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_ToolGroups
    {
        public const string ToolsSetGroupEnabledId = "tools-set-group-enabled";

        [McpPluginTool
        (
            ToolsSetGroupEnabledId,
            Title = "Tools / Set Group Enabled",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Enable or disable an MCP tool group at runtime, persisting the choice to EditorPrefs. " +
            "Use this together with 'tools-list-groups' to keep the LLM tool-list lean: turn off niche groups " +
            "(physics, profiler, vfx) until you need them. " +
            "NOTE: currently a soft hint — disabled tools remain callable; enforcement requires hooking the request pipeline.")]
        [McpPluginSkillBody("Toggle a logical tool group. " +
            "Groups are declared on `Tool_*` partial-class roots via `[ToolGroupAttribute(\"name\")]`. " +
            "This tool only updates the registry + EditorPrefs; it does not modify the upstream NuGet package " +
            "or hook `IRunTool.RunCallTool`, so toggling is informational only at the moment.\n\n" +
            "## Inputs\n\n" +
            "- `group` — group name (case-sensitive). Use `tools-list-groups` to discover valid names.\n" +
            "- `enabled` — true to enable the group, false to disable.\n\n" +
            "## Persistence\n\n" +
            "Stored under EditorPrefs key `UnityMcp.ToolGroups.Enabled` as a pipe-separated " +
            "`group=0|group=1` string. Stale entries (for groups that no longer exist) are ignored on load.")]
        [Description("Enable or disable an MCP tool group at runtime, persisting the choice to EditorPrefs. " +
            "Soft hint only — disabled tools remain callable through the upstream request pipeline.")]
        public ToolGroupResult SetGroupEnabled
        (
            [Description("Group name. Use 'tools-list-groups' to discover valid group names.")]
            string group,
            [Description("True to enable the group, false to disable it.")]
            bool enabled
        )
        {
            if (string.IsNullOrWhiteSpace(group))
                throw new ArgumentException(Error.GroupNameIsEmpty(), nameof(group));

            return MainThread.Instance.Run(() =>
            {
                var canonicalGroup = ToolGroupRegistry.ResolveCanonicalGroup(group);
                if (canonicalGroup == null)
                    throw new ArgumentException(Error.UnknownGroup(group), nameof(group));

                var previous = ToolGroupRegistry.IsGroupEnabled(canonicalGroup);
                if (previous != enabled)
                    ToolGroupRegistry.SetGroupEnabled(canonicalGroup, enabled);

                var tools = ToolGroupRegistry.ToolsInGroup(canonicalGroup);
                var aliases = ToolGroupRegistry.AliasesForGroup(canonicalGroup);
                var aliasesArray = new string[aliases.Count];
                for (var i = 0; i < aliases.Count; i++) aliasesArray[i] = aliases[i];

                return new ToolGroupResult
                {
                    Group = canonicalGroup,
                    Enabled = enabled,
                    RequestedEnabled = enabled,
                    EffectiveEnabled = ToolGroupRegistry.IsGroupEffectivelyEnabled(canonicalGroup),
                    Aliases = aliasesArray,
                    PreviousEnabled = previous,
                    Changed = previous != enabled,
                    ToolCount = tools.Count,
                    Note = SoftHintNote
                };
            });
        }
    }
}
