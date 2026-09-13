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

namespace AIGD
{
    /// <summary>
    /// One row in the <c>tools-list-groups</c> response and the
    /// <c>editor://tool-groups</c> resource payload.
    /// </summary>
    [Description("A logical tool group declared via [ToolGroupAttribute].")]
    public class ToolGroupSnapshot
    {
        [Description("Group name (lower-case identifier, e.g. 'core', 'physics', 'vfx').")]
        public string Name { get; set; } = string.Empty;

        [Description("True when the group is currently enabled. Note: enforcement is " +
            "currently a soft hint — disabled tools remain callable through the upstream " +
            "request pipeline. Use 'tools-set-group-enabled' to flip state and persist it.")]
        public bool Enabled { get; set; }

        [Description("Requested group state persisted by the user. Same value as Enabled for compatibility.")]
        public bool RequestedEnabled { get; set; }

        [Description("Actual callability at the current tool boundary. This remains true for populated groups while enforcement is soft.")]
        public bool EffectiveEnabled { get; set; }

        [Description("Accepted aliases that resolve to this canonical group name.")]
        public string[] Aliases { get; set; } = System.Array.Empty<string>();

        [Description("Default-enabled flag declared by the attribute. EditorPrefs may " +
            "override this; the diff between Enabled and DefaultEnabled tells you whether " +
            "the user has customised the group.")]
        public bool DefaultEnabled { get; set; }

        [Description("Optional human-readable description from the attribute. May be null.")]
        public string? Description { get; set; }

        [Description("Tool ids that belong to this group (the values exposed as MCP tool names).")]
        public string[] Tools { get; set; } = System.Array.Empty<string>();

        [Description("Number of tools in this group. Equal to Tools.Length; provided for " +
            "quick LLM-side filtering without iterating the array.")]
        public int ToolCount { get; set; }
    }

    /// <summary>
    /// Compact result returned by <c>tools-set-group-enabled</c>.
    /// </summary>
    [Description("Result of a 'tools-set-group-enabled' call.")]
    public class ToolGroupResult
    {
        [Description("The group name as resolved by the registry.")]
        public string Group { get; set; } = string.Empty;

        [Description("Group enabled state after the call.")]
        public bool Enabled { get; set; }

        [Description("Requested state after the call.")]
        public bool RequestedEnabled { get; set; }

        [Description("Actual callability after the call.")]
        public bool EffectiveEnabled { get; set; }

        [Description("Accepted aliases for the canonical group.")]
        public string[] Aliases { get; set; } = System.Array.Empty<string>();

        [Description("Group enabled state before the call. When equal to Enabled the call " +
            "was a no-op and no EditorPrefs write happened.")]
        public bool PreviousEnabled { get; set; }

        [Description("True when the call changed the state (and therefore touched EditorPrefs).")]
        public bool Changed { get; set; }

        [Description("Number of tool ids that belong to the affected group.")]
        public int ToolCount { get; set; }

        [Description("Informational note. Currently always carries the soft-hint disclaimer " +
            "explaining that enforcement is not wired into the request pipeline yet.")]
        public string Note { get; set; } = string.Empty;
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// Domain root for the <c>tools-*</c> group-management tools and the
    /// <c>editor://tool-groups</c> resource.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class is intentionally a thin façade over
    /// <see cref="com.AtelierAI.Unity.Copilot.Editor.Utils.ToolGroupRegistry"/>.
    /// All scanning, enabled-state tracking, and EditorPrefs persistence
    /// happens inside the registry; the tools below only project that state
    /// for the LLM.
    /// </para>
    /// <para>
    /// <b>Opting tools into a group.</b> Add
    /// <c>[ToolGroup("vfx")]</c> (or any other group name) to the
    /// partial-class root of a <c>Tool_*</c> type — the same file that carries
    /// <c>[McpPluginToolType]</c>. The registry will pick it up on the next
    /// domain reload. No existing tool files are touched in this implementation;
    /// callers are expected to add the marker themselves on the tools they
    /// want to gate.
    /// </para>
    /// <para>
    /// <b>Soft hint, not hard enforcement.</b> Toggling a group via
    /// <c>tools-set-group-enabled</c> updates the registry + EditorPrefs but
    /// does <i>not</i> cause the upstream <c>IRunTool.RunCallTool</c> to reject
    /// calls into disabled tools. That would require modifying the external
    /// NuGet package, which is out of scope for this change. Downstream
    /// consumers (e.g. a future <c>batch-execute</c> filter or a request
    /// interceptor) can read <c>ToolGroupRegistry.IsToolEnabled</c> to honour
    /// the user's choice.
    /// </para>
    /// </remarks>
    [McpPluginToolType]
    public partial class Tool_ToolGroups
    {
        public static class Error
        {
            public static string GroupNameIsEmpty()
                => "Group name is null or empty. Call 'tools-list-groups' to discover valid group names.";

            public static string UnknownGroup(string group)
                => $"Unknown group '{group}'. Call 'tools-list-groups' to discover valid group names.";
        }

        internal const string SoftHintNote =
            "Soft hint only: state is persisted via EditorPrefs, but disabled tools remain " +
            "callable through the upstream IRunTool.RunCallTool pipeline. Enforcement would " +
            "require hooking the request pipeline — not in scope for this change.";
    }
}
