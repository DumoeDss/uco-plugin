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
using System.Linq;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;

namespace AIGD
{
    [Description("A single Unity tag entry as defined in TagManager.asset.")]
    public class TagEntry
    {
        [Description("Tag name (e.g. 'Untagged', 'Player'). Matches the strings accepted by GameObject.CompareTag.")]
        public string Name { get; set; } = string.Empty;
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;
    using Consts = com.AtelierAI.Uco.Framework.Common.Consts;

    [UcoResourceType]
    public partial class Resource_ProjectTags
    {
        public const string ProjectTagsResourceUri = "project://tags";

        [UcoResource
        (
            Name = "Project Tags",
            Route = ProjectTagsResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "List of every Unity tag registered in TagManager.asset (including built-ins like 'Untagged', " +
                "'Respawn', 'Finish', 'EditorOnly', 'MainCamera', 'Player', 'GameController'). Use it to pick a valid " +
                "tag string before calling GameObject.tag or CompareTag.",
            Enabled = true
        )]
        public ResponseResourceContent[] GetTags(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var entries = CollectTags();
                var json = SerializeTags(entries);
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
                uri: ProjectTagsResourceUri,
                name: "Project Tags",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Reads <c>UnityEditorInternal.InternalEditorUtility.tags</c>. The namespace is "Internal"
        /// but the property has been a public static string[] surface since Unity 5.x and is still
        /// present in Unity 6 — we go through reflection only as a safety net in case Unity ever
        /// relocates it, so a missing API degrades to an empty list rather than a hard crash.
        /// </summary>
        private static List<TagEntry> CollectTags()
        {
            string[]? tags = null;
            try
            {
                tags = UnityEditorInternal.InternalEditorUtility.tags;
            }
            catch
            {
                tags = null;
            }

            if (tags == null || tags.Length == 0)
            {
                // Reflection fallback. Some future Unity version might rename the type but keep the
                // string[] tags surface — try to locate it before giving up.
                tags = ReflectFallbackTags();
            }

            if (tags == null)
                return new List<TagEntry>(0);

            var list = new List<TagEntry>(tags.Length);
            foreach (var name in tags)
            {
                if (!string.IsNullOrEmpty(name))
                    list.Add(new TagEntry { Name = name });
            }
            return list;
        }

        private static string[]? ReflectFallbackTags()
        {
            try
            {
                var asm = typeof(UnityEditor.Editor).Assembly;
                var type = asm.GetType("UnityEditorInternal.InternalEditorUtility");
                var prop = type?.GetProperty("tags", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return prop?.GetValue(null) as string[];
            }
            catch
            {
                return null;
            }
        }

        private static string SerializeTags(IReadOnlyList<TagEntry> entries)
        {
            var sb = new System.Text.StringBuilder(entries.Count * 24 + 16);
            sb.Append('[');
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"name\":"); ResourceJson.AppendString(sb, entries[i].Name);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
