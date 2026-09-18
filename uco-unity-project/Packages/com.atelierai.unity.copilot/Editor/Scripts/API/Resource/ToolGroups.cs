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
using System.Text;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    using Consts = com.AtelierAI.Uco.Framework.Common.Consts;

    /// <summary>
    /// Read-only resource that exposes the current tool-group registry
    /// snapshot at <c>editor://tool-groups</c>. The payload is identical in
    /// shape to <c>tools-list-groups</c> but delivered through the Uco
    /// resource channel, which lets clients subscribe / cache it cheaply
    /// without paying for a tool invocation.
    /// </summary>
    [UcoResourceType]
    public partial class Resource_ToolGroups
    {
        public const string ToolGroupsResourceUri = "editor://tool-groups";

        [UcoResource
        (
            Name = "Tool Groups",
            Route = ToolGroupsResourceUri,
            MimeType = Consts.MimeType.TextJson,
            ListResources = nameof(ListAll),
            Description = "Snapshot of every [ToolGroupAttribute] group known to the registry — " +
                "name, enabled state (soft hint), default-enabled flag, description, and tool ids. " +
                "Pair with 'tools-set-group-enabled' to flip a group at runtime.",
            Enabled = true
        )]
        public ResponseResourceContent[] Get(string uri)
        {
            return MainThread.Instance.Run(() =>
            {
                var json = SerializeGroups();
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
                uri: ToolGroupsResourceUri,
                name: "Tool Groups",
                enabled: true,
                mimeType: Consts.MimeType.TextJson)
        };

        // ---------------------------------------------------------------------
        // Local minimal JSON serializer.
        //   We avoid pulling in Newtonsoft / System.Text.Json runtime config
        //   here because the payload schema is tiny and fully under our control.
        //   Same approach as Resource_MenuItems / Resource_EditorWindows.
        // ---------------------------------------------------------------------
        static string SerializeGroups()
        {
            var groups = ToolGroupRegistry.AllGroups();
            var sb = new StringBuilder(groups.Count * 96 + 16);
            sb.Append('[');
            for (var i = 0; i < groups.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var name = groups[i];
                var enabled = ToolGroupRegistry.IsGroupEnabled(name);
                var effectiveEnabled = ToolGroupRegistry.IsGroupEffectivelyEnabled(name);
                var defaultEnabled = ToolGroupRegistry.GetDefaultEnabled(name);
                var description = ToolGroupRegistry.GetDescription(name);
                var tools = ToolGroupRegistry.ToolsInGroup(name);
                var aliases = ToolGroupRegistry.AliasesForGroup(name);

                sb.Append('{');
                sb.Append("\"name\":"); AppendJsonString(sb, name);
                sb.Append(",\"enabled\":").Append(enabled ? "true" : "false");
                sb.Append(",\"requestedEnabled\":").Append(enabled ? "true" : "false");
                sb.Append(",\"effectiveEnabled\":").Append(effectiveEnabled ? "true" : "false");
                sb.Append(",\"defaultEnabled\":").Append(defaultEnabled ? "true" : "false");
                sb.Append(",\"description\":"); AppendJsonStringOrNull(sb, description);
                sb.Append(",\"aliases\":[");
                for (var a = 0; a < aliases.Count; a++)
                {
                    if (a > 0) sb.Append(',');
                    AppendJsonString(sb, aliases[a]);
                }
                sb.Append(']');
                sb.Append(",\"toolCount\":").Append(tools.Count);
                sb.Append(",\"tools\":[");
                for (var t = 0; t < tools.Count; t++)
                {
                    if (t > 0) sb.Append(',');
                    AppendJsonString(sb, tools[t]);
                }
                sb.Append(']');
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        static void AppendJsonStringOrNull(StringBuilder sb, string? value)
        {
            if (value == null) { sb.Append("null"); return; }
            AppendJsonString(sb, value);
        }

        static void AppendJsonString(StringBuilder sb, string value)
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
