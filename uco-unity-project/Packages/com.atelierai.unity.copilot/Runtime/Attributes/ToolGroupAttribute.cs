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

namespace com.AtelierAI.Unity.Copilot.Runtime.Attributes
{
    /// <summary>
    /// Marks a <c>Tool_*</c> partial class root as belonging to a logical group.
    /// Groups let LLM-facing tooling toggle high-frequency vs low-frequency tool
    /// surfaces at runtime (e.g. <c>core</c> + <c>scripting</c> always on,
    /// <c>physics</c> / <c>profiler</c> / <c>vfx</c> off until needed) to reduce
    /// the per-request tool-list token cost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Place this attribute on the partial-class root file (the same file that
    /// carries <c>[UcoToolType]</c>). It is read once at Editor startup by
    /// <c>ToolGroupRegistry</c> via reflection / <c>TypeCache</c>; the registry
    /// then walks every <c>[UcoTool]</c>-decorated method on the class and
    /// indexes <c>toolName → groupName</c>.
    /// </para>
    /// <para>
    /// The attribute is deliberately decoupled from the external NuGet
    /// <c>com.AtelierAI.Uco.Framework</c> attributes — it does not derive from or
    /// reference any external type, so it can be added/removed without touching
    /// the upstream package.
    /// </para>
    /// <para>
    /// Group enforcement is currently a soft hint: disabling a group only marks
    /// the tool as "soft-disabled" in the registry. Tool calls are not blocked
    /// because the upstream <c>IRunTool.RunCallTool</c> pipeline is not modified.
    /// See <c>ToolGroupRegistry</c> XML docs for the planned enforcement path.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ToolGroupAttribute : Attribute
    {
        /// <summary>
        /// Logical group name. Convention: lower-case, hyphen-free single word
        /// (e.g. <c>core</c>, <c>scripting</c>, <c>docs</c>, <c>physics</c>,
        /// <c>profiler</c>, <c>graphics</c>, <c>ui</c>, <c>build</c>, <c>vfx</c>).
        /// </summary>
        public string Group { get; }

        /// <summary>
        /// Whether the group is enabled by default on a fresh install (no
        /// EditorPrefs override). High-frequency groups default <c>true</c>,
        /// niche groups default <c>false</c>.
        /// </summary>
        public bool DefaultEnabled { get; }

        /// <summary>
        /// Optional human-readable group description surfaced to the LLM
        /// through <c>tools-list-groups</c> and the
        /// <c>editor://tool-groups</c> resource. Null is acceptable.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Construct a group marker. The group name is required and must be
        /// non-empty; an empty group name will be filtered out by the
        /// registry during scan.
        /// </summary>
        /// <param name="group">Logical group name (see <see cref="Group"/>).</param>
        /// <param name="defaultEnabled">
        /// Initial enabled state when no EditorPrefs override exists.
        /// </param>
        public ToolGroupAttribute(string group, bool defaultEnabled = true)
        {
            Group = group;
            DefaultEnabled = defaultEnabled;
        }
    }
}
