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
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;

namespace AIGD
{
    [Description("A single Unity Editor [MenuItem] entry discovered via reflection.")]
    public class MenuItemEntry
    {
        [Description("Full menu path as registered with UnityEditor.MenuItem (without shortcut modifiers).")]
        public string Path { get; set; } = string.Empty;

        [Description("Keyboard shortcut suffix parsed from the menu path (e.g. '&%a'). Null when not present.")]
        public string? Shortcut { get; set; }

        [Description("Fully-qualified name of the assembly that declares the [MenuItem] method.")]
        public string AssemblyName { get; set; } = string.Empty;

        [Description("Fully-qualified name of the type that declares the [MenuItem] method.")]
        public string DeclaringType { get; set; } = string.Empty;

        [Description("True when this entry is a validator method (the attribute's 'validate' flag is set).")]
        public bool HasValidate { get; set; }
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;
    using Consts = com.IvanMurzak.McpPlugin.Common.Consts;

    [McpPluginResourceType]
    public partial class Resource_MenuItems
    {
        public const string MenuItemsResourceUri = "editor://menu-items";

        [McpPluginResource
        (
            Name = "Menu Items",
            Route = MenuItemsResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "Enumerate every Unity Editor [MenuItem] discovered via reflection. " +
                "Use it to pick a canonical menu path before calling the 'editor-execute-menu-item' tool.",
            Enabled = true
        )]
        public ResponseResourceContent[] GetMenuItems(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var entries = CollectMenuItems();
                var json = SerializeEntries(entries);
                return ResponseResourceContent.CreateText(
                    uri: uri,
                    mimeType: Consts.MimeType.TextJson,
                    text: json
                ).MakeArray();
            });
        }

        public ResponseListResource[] ListAll() => new[]
        {
            new ResponseListResource(
                uri: MenuItemsResourceUri,
                name: "Menu Items",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };

        // ---------------------------------------------------------------------
        // Reflection helpers
        // ---------------------------------------------------------------------

        private static IReadOnlyList<MenuItemEntry> CollectMenuItems()
        {
            // TypeCache.GetMethodsWithAttribute is available in Unity 2019.2+ and indexes every loaded
            // assembly's methods bearing a [MenuItem]. Domain reload keeps the cache fresh, so calls
            // from MCP requests will always see the current project's menu items.
            var methods = TypeCache.GetMethodsWithAttribute<UnityEditor.MenuItem>();
            var result = new List<MenuItemEntry>(methods.Count);

            foreach (var method in methods)
            {
                var attrs = method.GetCustomAttributes<UnityEditor.MenuItem>(inherit: false);
                foreach (var attr in attrs)
                {
                    if (attr == null || string.IsNullOrEmpty(attr.menuItem))
                        continue;

                    SplitPathAndShortcut(attr.menuItem, out var path, out var shortcut);
                    var declaring = method.DeclaringType;
                    result.Add(new MenuItemEntry
                    {
                        Path = path,
                        Shortcut = shortcut,
                        DeclaringType = declaring?.FullName ?? string.Empty,
                        AssemblyName = declaring?.Assembly.GetName().Name ?? string.Empty,
                        HasValidate = attr.validate
                    });
                }
            }

            // Stable ordering: path, then validators after the executable entry for the same path.
            result.Sort((a, b) =>
            {
                var cmp = string.CompareOrdinal(a.Path, b.Path);
                if (cmp != 0) return cmp;
                return a.HasValidate.CompareTo(b.HasValidate);
            });
            return result;
        }

        /// <summary>
        /// MenuItem paths can carry a keyboard shortcut as a suffix separated by a space, e.g.
        /// <c>"My/Menu/Do Thing %#a"</c>. The shortcut tokens start with one of '&', '%', '#', '_',
        /// or the special <c>LEFT</c>/<c>RIGHT</c>/<c>UP</c>/<c>DOWN</c>/<c>F\d+</c> keywords. We
        /// detect the boundary by locating the last space whose right-hand side looks like a
        /// shortcut spec — this matches Unity's own behaviour without depending on internal APIs.
        /// </summary>
        private static void SplitPathAndShortcut(string raw, out string path, out string? shortcut)
        {
            path = raw;
            shortcut = null;
            if (string.IsNullOrEmpty(raw))
                return;

            var spaceIdx = raw.LastIndexOf(' ');
            if (spaceIdx <= 0 || spaceIdx >= raw.Length - 1)
                return;

            var tail = raw.Substring(spaceIdx + 1);
            if (LooksLikeShortcut(tail))
            {
                path = raw.Substring(0, spaceIdx).TrimEnd();
                shortcut = tail;
            }
        }

        private static bool LooksLikeShortcut(string tail)
        {
            // Modifier-prefixed: %, &, #, _
            if (tail.Length > 0)
            {
                var c = tail[0];
                if (c == '%' || c == '&' || c == '#' || c == '_')
                    return true;
            }
            // Special key names recognised by Unity's MenuItem parser
            switch (tail)
            {
                case "LEFT":
                case "RIGHT":
                case "UP":
                case "DOWN":
                case "HOME":
                case "END":
                case "PGUP":
                case "PGDN":
                    return true;
            }
            if (tail.Length >= 2 && tail[0] == 'F'
                && int.TryParse(tail.Substring(1), out var fn) && fn >= 1 && fn <= 24)
                return true;
            return false;
        }

        // ---------------------------------------------------------------------
        // Minimal JSON serializer for the resource payload.
        //   We avoid pulling in Newtonsoft / System.Text.Json runtime configuration
        //   here because the schema is tiny and fully under our control.
        // ---------------------------------------------------------------------
        private static string SerializeEntries(IReadOnlyList<MenuItemEntry> entries)
        {
            var sb = new System.Text.StringBuilder(entries.Count * 64);
            sb.Append('[');
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                sb.Append('{');
                sb.Append("\"path\":"); AppendJsonString(sb, e.Path);
                sb.Append(",\"shortcut\":"); AppendJsonStringOrNull(sb, e.Shortcut);
                sb.Append(",\"assemblyName\":"); AppendJsonString(sb, e.AssemblyName);
                sb.Append(",\"declaringType\":"); AppendJsonString(sb, e.DeclaringType);
                sb.Append(",\"hasValidate\":").Append(e.HasValidate ? "true" : "false");
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static void AppendJsonStringOrNull(System.Text.StringBuilder sb, string? value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }
            AppendJsonString(sb, value);
        }

        private static void AppendJsonString(System.Text.StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
