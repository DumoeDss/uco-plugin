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
using System.Linq;
using System.Reflection;
using System.Text;
using com.AtelierAI.Unity.Copilot.Runtime.Attributes;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    /// <summary>
    /// Editor-side static registry that indexes <c>Tool_*</c> partial-class roots
    /// decorated with <see cref="ToolGroupAttribute"/> and tracks the
    /// enabled state for each discovered group.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Initialized on <c>[InitializeOnLoad]</c> domain reload. The scan walks
    /// <c>TypeCache.GetTypesWithAttribute&lt;ToolGroupAttribute&gt;()</c>,
    /// and for each marked class enumerates public/non-public instance and static
    /// methods, reflecting any attribute whose simple type-name is
    /// <c>McpPluginToolAttribute</c> (the external NuGet attribute we are
    /// deliberately *not* taking a hard compile-time dependency on, so the
    /// upstream package can evolve without breaking this file).
    /// </para>
    /// <para>
    /// Enabled state is persisted to <see cref="EditorPrefs"/> as a compact
    /// pipe-separated <c>group=0|group=1</c> string under
    /// <see cref="EditorPrefsKey"/>. We avoid JSON here to keep this file
    /// dependency-free; the schema is tiny and fully under our control.
    /// </para>
    /// <para>
    /// <b>Enforcement note.</b> This class is purely a registry +
    /// persistence layer. Currently <c>IsToolEnabled</c> is informational —
    /// the upstream <c>IRunTool.RunCallTool</c> pipeline is *not* hooked,
    /// so soft-disabled tools remain callable. Enforcement would require
    /// either a request-pipeline interceptor or hooking the
    /// <c>ToolDisabled</c> branch on the upstream tool-manager.
    /// </para>
    /// </remarks>
    [InitializeOnLoad]
    public static class ToolGroupRegistry
    {
        // ---------------------------------------------------------------------
        // Indices (rebuilt every domain reload via the static ctor).
        // ---------------------------------------------------------------------
        static readonly Dictionary<string, List<string>> s_groupToTools =
            new(StringComparer.Ordinal);
        static readonly Dictionary<string, string> s_toolToGroup =
            new(StringComparer.Ordinal);
        static readonly Dictionary<string, bool> s_enabled =
            new(StringComparer.Ordinal);
        static readonly Dictionary<string, bool> s_defaultEnabled =
            new(StringComparer.Ordinal);
        static readonly Dictionary<string, string?> s_descriptions =
            new(StringComparer.Ordinal);
        static readonly HashSet<string> s_knownGroups =
            new(StringComparer.Ordinal);

        /// <summary>
        /// EditorPrefs key used to persist the per-group enabled flags.
        /// The stored value is a pipe-separated list of <c>group=0|group=1</c>
        /// tokens. Unknown groups in the stored value are ignored on load.
        /// </summary>
        public const string EditorPrefsKey = "UnityMcp.ToolGroups.Enabled";

        // ---------------------------------------------------------------------
        // Static initialization.
        // ---------------------------------------------------------------------
        static ToolGroupRegistry()
        {
            try
            {
                Scan();
                LoadEnabledFromPrefs();
            }
            catch (Exception ex)
            {
                // Never let registry construction take down a domain reload.
                // Log via UnityEngine.Debug only — the plugin logger is not
                // guaranteed to be initialized this early in startup.
                UnityEngine.Debug.LogWarning(
                    $"[ToolGroupRegistry] initialization failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------------------------------------------------------------
        // Public API.
        // ---------------------------------------------------------------------

        /// <summary>All known group names, sorted ascending.</summary>
        public static IReadOnlyList<string> AllGroups()
            => s_knownGroups.OrderBy(s => s, StringComparer.Ordinal).ToList();

        /// <summary>
        /// True if the tool is in an enabled group, or is ungrouped (ungrouped
        /// tools are always considered enabled — they were not opted into the
        /// group system).
        /// </summary>
        public static bool IsToolEnabled(string toolName)
        {
            if (string.IsNullOrEmpty(toolName))
                return true;
            if (!s_toolToGroup.TryGetValue(toolName, out var group))
                return true;
            return s_enabled.TryGetValue(group, out var enabled) ? enabled : true;
        }

        /// <summary>True if the group is currently enabled.</summary>
        public static bool IsGroupEnabled(string group)
            => s_enabled.TryGetValue(group, out var enabled) ? enabled : true;

        /// <summary>
        /// Toggle a group's enabled state and persist the new state to
        /// EditorPrefs. Throws <see cref="ArgumentException"/> if the group
        /// name is not known to the registry.
        /// </summary>
        public static void SetGroupEnabled(string group, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(group))
                throw new ArgumentException("Group name must be non-empty.", nameof(group));
            if (!s_knownGroups.Contains(group))
                throw new ArgumentException(
                    $"Unknown group '{group}'. Known: {string.Join(", ", s_knownGroups)}",
                    nameof(group));

            s_enabled[group] = enabled;
            SaveEnabledToPrefs();
        }

        /// <summary>Tool ids that belong to <paramref name="group"/>, or empty when unknown.</summary>
        public static IReadOnlyList<string> ToolsInGroup(string group)
            => s_groupToTools.TryGetValue(group, out var list)
                ? list.ToList()
                : new List<string>();

        /// <summary>True when the group is known to the registry.</summary>
        public static bool IsKnownGroup(string group) => s_knownGroups.Contains(group);

        /// <summary>The default-enabled flag declared by the attribute. Unknown groups → true.</summary>
        public static bool GetDefaultEnabled(string group)
            => s_defaultEnabled.TryGetValue(group, out var v) ? v : true;

        /// <summary>Optional human-readable description from the attribute, or null.</summary>
        public static string? GetDescription(string group)
            => s_descriptions.TryGetValue(group, out var d) ? d : null;

        /// <summary>
        /// Force a re-scan of the loaded assemblies. Useful in tests; the
        /// registry self-initializes on domain reload so production code does
        /// not need to call this.
        /// </summary>
        public static void Rebuild()
        {
            s_groupToTools.Clear();
            s_toolToGroup.Clear();
            s_enabled.Clear();
            s_defaultEnabled.Clear();
            s_descriptions.Clear();
            s_knownGroups.Clear();
            Scan();
            LoadEnabledFromPrefs();
        }

        // ---------------------------------------------------------------------
        // Implementation.
        // ---------------------------------------------------------------------

        static void Scan()
        {
            // TypeCache walks all loaded assemblies cheaply and is refreshed
            // on every domain reload, so we get an up-to-date picture without
            // paying for a full AppDomain assembly enumeration.
            var types = TypeCache.GetTypesWithAttribute<ToolGroupAttribute>();
            foreach (var t in types)
            {
                var groupAttr = t.GetCustomAttribute<ToolGroupAttribute>(inherit: false);
                if (groupAttr == null) continue;
                if (string.IsNullOrWhiteSpace(groupAttr.Group)) continue;

                var groupName = groupAttr.Group;
                s_knownGroups.Add(groupName);

                // Last-attribute-wins for default-enabled / description if
                // multiple partial-class roots share the same group name
                // (rare; partials usually only have one root with the marker).
                s_defaultEnabled[groupName] = groupAttr.DefaultEnabled;
                if (groupAttr.Description != null)
                    s_descriptions[groupName] = groupAttr.Description;

                if (!s_enabled.ContainsKey(groupName))
                    s_enabled[groupName] = groupAttr.DefaultEnabled;

                CollectToolMethodsInto(t, groupName);
            }
        }

        static void CollectToolMethodsInto(Type t, string groupName)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.DeclaredOnly;

            // Partial-class methods may live on the same Type across multiple
            // files — Reflection sees them as one Type, so a single pass is
            // sufficient. We use DeclaredOnly to avoid spilling into base
            // System.Object methods.
            var methods = t.GetMethods(flags);
            foreach (var m in methods)
            {
                var toolName = ExtractToolName(m);
                if (string.IsNullOrEmpty(toolName))
                    continue;

                if (!s_groupToTools.TryGetValue(groupName, out var list))
                {
                    list = new List<string>();
                    s_groupToTools[groupName] = list;
                }
                if (!list.Contains(toolName!))
                    list.Add(toolName!);

                // First-write-wins: if a tool somehow appears in two groups
                // via partial-class shenanigans, the first scan wins. This is
                // logged so it can be debugged but does not throw.
                if (s_toolToGroup.TryGetValue(toolName!, out var existing) && existing != groupName)
                {
                    UnityEngine.Debug.LogWarning(
                        $"[ToolGroupRegistry] tool '{toolName}' already mapped to group " +
                        $"'{existing}'; ignoring duplicate mapping to '{groupName}'.");
                    continue;
                }
                s_toolToGroup[toolName!] = groupName;
            }
        }

        /// <summary>
        /// Reflect a method's custom attributes looking for one whose simple
        /// type name is <c>McpPluginToolAttribute</c>, and pull out the tool
        /// id via its <c>Name</c> property. We intentionally avoid a typed
        /// reference to the external NuGet attribute so this file does not
        /// require linking against <c>McpPlugin.dll</c> at compile time —
        /// the asmdef already references it for the rest of the codebase,
        /// but loose coupling here keeps the registry resilient to upstream
        /// renames of the property surface.
        /// </summary>
        static string? ExtractToolName(MethodInfo method)
        {
            object[] attrs;
            try { attrs = method.GetCustomAttributes(inherit: false); }
            catch { return null; }

            foreach (var attr in attrs)
            {
                if (attr == null) continue;
                var typeName = attr.GetType().Name;
                if (typeName != "McpPluginToolAttribute")
                    continue;

                // Probe a small set of likely member names. The upstream
                // attribute uses 'Name' (verified via TestProject), but we
                // try a fallback 'Id' to stay forward-compatible.
                var nameValue = ReadStringMember(attr, "Name")
                             ?? ReadStringMember(attr, "Id");
                if (!string.IsNullOrWhiteSpace(nameValue))
                    return nameValue;
            }
            return null;
        }

        static string? ReadStringMember(object instance, string memberName)
        {
            var type = instance.GetType();
            var prop = type.GetProperty(memberName,
                BindingFlags.Public | BindingFlags.Instance);
            if (prop != null)
            {
                try { return prop.GetValue(instance) as string; }
                catch { return null; }
            }
            var field = type.GetField(memberName,
                BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                try { return field.GetValue(instance) as string; }
                catch { return null; }
            }
            return null;
        }

        // ---------------------------------------------------------------------
        // Persistence.
        // ---------------------------------------------------------------------

        static void LoadEnabledFromPrefs()
        {
            var raw = EditorPrefs.GetString(EditorPrefsKey, string.Empty);
            if (string.IsNullOrEmpty(raw))
                return;

            // Format: group=0|group=1|...
            foreach (var entry in raw.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var sep = entry.IndexOf('=');
                if (sep <= 0 || sep >= entry.Length - 1) continue;

                var name = entry.Substring(0, sep);
                var flag = entry.Substring(sep + 1);
                if (!s_knownGroups.Contains(name)) continue;          // ignore stale
                if (flag == "1") s_enabled[name] = true;
                else if (flag == "0") s_enabled[name] = false;
                // anything else: ignore (use the attribute default)
            }
        }

        static void SaveEnabledToPrefs()
        {
            if (s_enabled.Count == 0)
            {
                EditorPrefs.DeleteKey(EditorPrefsKey);
                return;
            }

            var sb = new StringBuilder(s_enabled.Count * 12);
            var first = true;
            foreach (var kv in s_enabled.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append('|');
                sb.Append(kv.Key).Append('=').Append(kv.Value ? '1' : '0');
                first = false;
            }
            EditorPrefs.SetString(EditorPrefsKey, sb.ToString());
        }
    }
}
