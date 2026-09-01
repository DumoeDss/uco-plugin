/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    /// <summary>
    /// Serializable version-one metadata for a regular or system tool call.
    /// Unknown members are retained so an adapter can forward metadata from a
    /// newer peer without interpreting it.
    /// </summary>
    public class ToolCallControl
    {
        public const int CurrentVersion = 1;

        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("callId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CallId { get; set; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; set; }

        [JsonPropertyName("parentCallId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ParentCallId { get; set; }

        [JsonPropertyName("deadlineUnixMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? DeadlineUnixMs { get; set; }

        [JsonPropertyName("cancellationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CancellationId { get; set; }

        [JsonPropertyName("idempotencyKey")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IdempotencyKey { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> UnknownMembers { get; set; }
            = new Dictionary<string, JsonElement>();

        public ToolCallControl Clone()
        {
            var clone = new ToolCallControl
            {
                Version = Version,
                CallId = CallId,
                CorrelationId = CorrelationId,
                ParentCallId = ParentCallId,
                DeadlineUnixMs = DeadlineUnixMs,
                CancellationId = CancellationId,
                IdempotencyKey = IdempotencyKey,
            };

            foreach (var member in UnknownMembers)
                clone.UnknownMembers[member.Key] = member.Value.Clone();

            return clone;
        }
    }
}
