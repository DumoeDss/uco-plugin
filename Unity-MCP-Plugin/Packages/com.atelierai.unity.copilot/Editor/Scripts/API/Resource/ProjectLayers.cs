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
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace AIGD
{
    [Description("A single named Unity physics/render layer (indices 0..31). Unnamed slots are omitted.")]
    public class LayerEntry
    {
        [Description("Layer index in the range 0..31, as used by GameObject.layer and LayerMask bits.")]
        public int Index { get; set; }

        [Description("Layer name as defined in TagManager.asset. Always non-empty for entries returned by this resource.")]
        public string Name { get; set; } = string.Empty;
    }
}

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using AIGD;
    using Consts = com.AtelierAI.Uco.Framework.Common.Consts;

    [UcoResourceType]
    public partial class Resource_ProjectLayers
    {
        public const string ProjectLayersResourceUri = "project://layers";

        [UcoResource
        (
            Name = "Project Layers",
            Route = ProjectLayersResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "List of named Unity layers (index + name). Skips unnamed slots in the 32-bit layer table. " +
                "Use it to pick a valid layer name before setting GameObject.layer or building a LayerMask.",
            Enabled = true
        )]
        public ResponseResourceContent[] GetLayers(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var entries = CollectLayers();
                var json = SerializeLayers(entries);
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
                uri: ProjectLayersResourceUri,
                name: "Project Layers",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static List<LayerEntry> CollectLayers()
        {
            // Unity exposes 32 layer slots (bit width of LayerMask). LayerToName returns "" for
            // unconfigured slots — we skip those so the payload only lists actionable names.
            var layers = new List<LayerEntry>(capacity: 32);
            for (var i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(name))
                    continue;
                layers.Add(new LayerEntry { Index = i, Name = name });
            }
            return layers;
        }

        private static string SerializeLayers(IReadOnlyList<LayerEntry> entries)
        {
            var sb = new System.Text.StringBuilder(entries.Count * 32 + 16);
            sb.Append('[');
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var e = entries[i];
                sb.Append('{');
                sb.Append("\"index\":").Append(e.Index);
                sb.Append(",\"name\":"); ResourceJson.AppendString(sb, e.Name);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
