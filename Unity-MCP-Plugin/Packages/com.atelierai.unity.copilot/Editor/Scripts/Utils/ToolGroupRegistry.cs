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
using com.IvanMurzak.McpPlugin;
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

        static readonly Dictionary<string, string[]> s_aliasesByCanonical =
            new(StringComparer.Ordinal)
            {
                ["assets"] = new[] { "asset" },
                ["automation"] = new[] { "batch" },
                ["build"] = new[] { "builds" },
                ["camera"] = new[] { "cameras", "cinemachine" },
                ["diagnostics"] = new[] { "console", "logs" },
                ["docs"] = new[] { "documentation" },
                ["editor"] = new[] { "application" },
                ["gameobject"] = new[] { "game-object", "gameobjects" },
                ["graphics"] = new[] { "rendering", "frame-debugger" },
                ["instances"] = new[] { "instance" },
                ["objects"] = new[] { "object" },
                ["operations"] = new[] { "jobs", "editor-operations" },
                ["packages"] = new[] { "package" },
                ["physics"] = Array.Empty<string>(),
                ["profiler"] = new[] { "profiling" },
                ["reflection"] = new[] { "reflect", "type", "types", "schema" },
                ["scene"] = new[] { "scenes" },
                ["screenshot"] = new[] { "screenshots", "capture" },
                ["scripting"] = new[] { "script", "scripts" },
                ["tests"] = new[] { "test", "testing" },
                ["texture"] = new[] { "textures" },
                ["tools"] = new[] { "tool", "core" },
                ["ui"] = new[] { "user-interface" },
                ["vfx"] = new[] { "visual-effects" }
            };

        static readonly Dictionary<string, string> s_aliasToCanonical = BuildAliasIndex();

        static readonly Dictionary<string, string> s_prefixToCanonical =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["assets"] = "assets",
                ["batch"] = "automation",
                ["build"] = "build",
                ["camera"] = "camera",
                ["console"] = "diagnostics",
                ["docs"] = "docs",
                ["editor"] = "editor",
                ["frame"] = "graphics",
                ["gameobject"] = "gameobject",
                ["graphics"] = "graphics",
                ["instance"] = "instances",
                ["object"] = "objects",
                ["package"] = "packages",
                ["physics"] = "physics",
                ["profiler"] = "profiler",
                ["reflection"] = "reflection",
                ["scene"] = "scene",
                ["screenshot"] = "screenshot",
                ["script"] = "scripting",
                ["tests"] = "tests",
                ["texture"] = "texture",
                ["tool"] = "tools",
                ["tools"] = "tools",
                ["type"] = "reflection",
                ["ui"] = "ui",
                ["unity"] = "tools",
                ["vfx"] = "vfx"
            };

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

        public static string? ResolveCanonicalGroup(string? group)
        {
            if (string.IsNullOrWhiteSpace(group)) return null;
            var normalized = group.Trim();
            if (s_aliasToCanonical.TryGetValue(normalized, out var canonical) &&
                s_knownGroups.Contains(canonical))
                return canonical;
            return s_knownGroups.Contains(normalized) ? normalized : null;
        }

        public static IReadOnlyList<string> AliasesForGroup(string group)
        {
            var canonical = ResolveCanonicalGroup(group) ?? group;
            return s_aliasesByCanonical.TryGetValue(canonical, out var aliases)
                ? aliases.ToArray()
                : Array.Empty<string>();
        }

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
        {
            var canonical = ResolveCanonicalGroup(group) ?? group;
            return s_enabled.TryGetValue(canonical, out var enabled) ? enabled : true;
        }

        /// <summary>
        /// Actual callability at the current request boundary. Group state is still a
        /// soft preference, so a populated group remains effectively callable even
        /// when its requested state is disabled.
        /// </summary>
        public static bool IsGroupEffectivelyEnabled(string group)
        {
            var canonical = ResolveCanonicalGroup(group);
            return canonical != null && s_groupToTools.TryGetValue(canonical, out var tools) && tools.Count > 0;
        }

        /// <summary>
        /// Toggle a group's enabled state and persist the new state to
        /// EditorPrefs. Throws <see cref="ArgumentException"/> if the group
        /// name is not known to the registry.
        /// </summary>
        public static void SetGroupEnabled(string group, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(group))
                throw new ArgumentException("Group name must be non-empty.", nameof(group));
            var canonical = ResolveCanonicalGroup(group);
            if (canonical == null)
                throw new ArgumentException(
                    $"Unknown group '{group}'. Known: {string.Join(", ", s_knownGroups)}",
                    nameof(group));

            s_enabled[canonical] = enabled;
            SaveEnabledToPrefs();
        }

        /// <summary>Tool ids that belong to <paramref name="group"/>, or empty when unknown.</summary>
        public static IReadOnlyList<string> ToolsInGroup(string group)
            => s_groupToTools.TryGetValue(ResolveCanonicalGroup(group) ?? group, out var list)
                ? list.ToList()
                : new List<string>();

        /// <summary>True when the group is known to the registry.</summary>
        public static bool IsKnownGroup(string group) => ResolveCanonicalGroup(group) != null;

        /// <summary>The default-enabled flag declared by the attribute. Unknown groups → true.</summary>
        public static bool GetDefaultEnabled(string group)
            => s_defaultEnabled.TryGetValue(ResolveCanonicalGroup(group) ?? group, out var v) ? v : true;

        /// <summary>Optional human-readable description from the attribute, or null.</summary>
        public static string? GetDescription(string group)
            => s_descriptions.TryGetValue(ResolveCanonicalGroup(group) ?? group, out var d) ? d : null;

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
            // Production tool declarations predate ToolGroupAttribute, so discover the
            // actual McpPluginTool methods and classify their stable tool IDs. An explicit
            // ToolGroupAttribute on a declaring type remains the override seam.
            var methods = TypeCache.GetMethodsWithAttribute<McpPluginToolAttribute>();
            foreach (var method in methods)
            {
                var toolName = ExtractToolName(method);
                if (string.IsNullOrWhiteSpace(toolName)) continue;

                var groupAttr = method.DeclaringType?.GetCustomAttribute<ToolGroupAttribute>(inherit: false);
                var explicitGroup = groupAttr == null ? null : ResolveAliasWithoutKnownCheck(groupAttr.Group);
                var groupName = !string.IsNullOrWhiteSpace(explicitGroup)
                    ? explicitGroup!
                    : ClassifyTool(toolName!);
                RegisterGroup(groupName, groupAttr?.DefaultEnabled ?? true, groupAttr?.Description);
                RegisterTool(toolName!, groupName);
            }
        }

        static void RegisterGroup(string groupName, bool defaultEnabled, string? description)
        {
            s_knownGroups.Add(groupName);
            s_defaultEnabled[groupName] = defaultEnabled;
            if (description != null) s_descriptions[groupName] = description;
            else if (!s_descriptions.ContainsKey(groupName))
                s_descriptions[groupName] = $"Production tools in the '{groupName}' capability group.";
            if (!s_enabled.ContainsKey(groupName)) s_enabled[groupName] = defaultEnabled;
        }

        static void RegisterTool(string toolName, string groupName)
        {
            if (!s_groupToTools.TryGetValue(groupName, out var list))
            {
                list = new List<string>();
                s_groupToTools[groupName] = list;
            }
            if (!list.Contains(toolName)) list.Add(toolName);

            if (s_toolToGroup.TryGetValue(toolName, out var existing) && existing != groupName)
            {
                UnityEngine.Debug.LogWarning(
                    $"[ToolGroupRegistry] tool '{toolName}' already mapped to group " +
                    $"'{existing}'; ignoring duplicate mapping to '{groupName}'.");
                return;
            }
            s_toolToGroup[toolName] = groupName;
        }

        static string ClassifyTool(string toolName)
        {
            if (toolName.StartsWith("editor-operation-", StringComparison.OrdinalIgnoreCase))
                return "operations";
            var separator = toolName.IndexOf('-');
            var prefix = separator < 0 ? toolName : toolName.Substring(0, separator);
            return s_prefixToCanonical.TryGetValue(prefix, out var canonical)
                ? canonical
                : prefix.ToLowerInvariant();
        }

        static Dictionary<string, string> BuildAliasIndex()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in s_aliasesByCanonical)
            {
                result[pair.Key] = pair.Key;
                foreach (var alias in pair.Value) result[alias] = pair.Key;
            }
            return result;
        }

        static string ResolveAliasWithoutKnownCheck(string group)
        {
            if (string.IsNullOrWhiteSpace(group)) return string.Empty;
            return s_aliasToCanonical.TryGetValue(group.Trim(), out var canonical)
                ? canonical
                : group.Trim().ToLowerInvariant();
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
