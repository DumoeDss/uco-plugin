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
using System.Globalization;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;
using UnityEngine;

namespace AIGD
{
    [Description("A single EditorWindow instance currently loaded in the Editor.")]
    public class EditorWindowEntry
    {
        [Description("Fully-qualified type name of the EditorWindow subclass (e.g. 'UnityEditor.SceneView').")]
        public string TypeName { get; set; } = string.Empty;

        [Description("Window title text shown in the tab (titleContent.text). May be empty for internal windows.")]
        public string Title { get; set; } = string.Empty;

        [Description("True when this window currently owns keyboard focus (EditorWindow.hasFocus).")]
        public bool HasFocus { get; set; }

        [Description("Window position in Editor screen-space — X (pixels).")]
        public float X { get; set; }

        [Description("Window position in Editor screen-space — Y (pixels).")]
        public float Y { get; set; }

        [Description("Window width in pixels.")]
        public float Width { get; set; }

        [Description("Window height in pixels.")]
        public float Height { get; set; }
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;
    using Consts = com.AtelierAI.Uco.Framework.Common.Consts;

    [UcoResourceType]
    public partial class Resource_EditorWindows
    {
        public const string EditorWindowsResourceUri = "editor://windows";

        /// <summary>
        /// Default cap on returned windows. Resources.FindObjectsOfTypeAll&lt;EditorWindow&gt; can surface
        /// well over 100 internal / hidden windows; we trim to keep the LLM context payload bounded.
        /// </summary>
        private const int DefaultMaxWindows = 100;

        [UcoResource
        (
            Name = "Editor Windows",
            Route = EditorWindowsResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "Enumerate the EditorWindow instances currently loaded in the Editor (type, title, focus, " +
                "screen-space rect). Hidden / zero-sized windows are filtered out by default to keep the payload " +
                "actionable for the LLM. Result count is capped to 100.",
            Enabled = true
        )]
        public ResponseResourceContent[] GetWindows(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var entries = CollectWindows(includeInvisible: false, max: DefaultMaxWindows);
                var json = SerializeWindows(entries);
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
                uri: EditorWindowsResourceUri,
                name: "Editor Windows",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Snapshots every loaded <see cref="EditorWindow"/> via <see cref="Resources.FindObjectsOfTypeAll{T}"/>.
        /// That call includes hidden / not-yet-shown windows, so a visibility filter is applied
        /// before the cap is enforced. Ordering is: focused first, then by type name for stability.
        /// </summary>
        private static List<EditorWindowEntry> CollectWindows(bool includeInvisible, int max)
        {
            var all = Resources.FindObjectsOfTypeAll<EditorWindow>();
            var list = new List<EditorWindowEntry>(all.Length);

            foreach (var w in all)
            {
                if (w == null)
                    continue;

                var rect = w.position;
                if (!includeInvisible && (rect.width <= 0f || rect.height <= 0f))
                    continue;

                string title;
                try
                {
                    title = w.titleContent?.text ?? string.Empty;
                }
                catch
                {
                    title = string.Empty;
                }

                bool hasFocus;
                try
                {
                    hasFocus = w.hasFocus;
                }
                catch
                {
                    hasFocus = false;
                }

                list.Add(new EditorWindowEntry
                {
                    TypeName = w.GetType().FullName ?? string.Empty,
                    Title = title,
                    HasFocus = hasFocus,
                    X = rect.x,
                    Y = rect.y,
                    Width = rect.width,
                    Height = rect.height,
                });
            }

            // Stable, useful ordering: focused first, then alphabetical by type.
            list.Sort((a, b) =>
            {
                if (a.HasFocus != b.HasFocus)
                    return a.HasFocus ? -1 : 1;
                return string.CompareOrdinal(a.TypeName, b.TypeName);
            });

            if (max > 0 && list.Count > max)
                list.RemoveRange(max, list.Count - max);

            return list;
        }

        private static string SerializeWindows(IReadOnlyList<EditorWindowEntry> entries)
        {
            var sb = new System.Text.StringBuilder(entries.Count * 96 + 16);
            sb.Append('[');
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                sb.Append('{');
                sb.Append("\"typeName\":"); ResourceJson.AppendString(sb, e.TypeName);
                sb.Append(",\"title\":"); ResourceJson.AppendString(sb, e.Title);
                sb.Append(",\"hasFocus\":").Append(e.HasFocus ? "true" : "false");
                sb.Append(",\"x\":").Append(FormatFloat(e.X));
                sb.Append(",\"y\":").Append(FormatFloat(e.Y));
                sb.Append(",\"width\":").Append(FormatFloat(e.Width));
                sb.Append(",\"height\":").Append(FormatFloat(e.Height));
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static string FormatFloat(float value)
        {
            // Invariant culture so commas in 'de-DE' style locales do not corrupt the JSON payload.
            // "R" preserves enough precision to round-trip the float; the Editor never gives sub-pixel
            // rects in practice, but the cost is negligible.
            if (float.IsNaN(value) || float.IsInfinity(value))
                return "null";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
