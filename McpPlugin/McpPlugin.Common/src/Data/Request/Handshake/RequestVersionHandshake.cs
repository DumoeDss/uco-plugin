/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/
using System;
using System.Text.Json.Serialization;

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    public class RequestVersionHandshake : IRequestID
    {
        [JsonPropertyName("requestId")]
        public string RequestID { get; set; } = string.Empty;

        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = string.Empty;

        [JsonPropertyName("pluginVersion")]
        public string PluginVersion { get; set; } = string.Empty;

        [JsonPropertyName("environment")]
        public string Environment { get; set; } = string.Empty;

        [JsonPropertyName("capabilities")]
        public string[] Capabilities { get; set; } = Array.Empty<string>();

        [JsonPropertyName("generation")]
        public int Generation { get; set; }

        // ── bridge-identity-v1 members (additive, capability-gated) ─────────
        // Populated only when the host supplies an IHandshakeIdentity; older
        // servers ignore them and older peers of this client never set them.
        [JsonPropertyName("projectPath")]
        public string? ProjectPath { get; set; }

        [JsonPropertyName("editorPid")]
        public int? EditorPid { get; set; }

        [JsonPropertyName("unityVersion")]
        public string? UnityVersion { get; set; }

        [JsonPropertyName("instanceId")]
        public string? InstanceId { get; set; }
    }
}