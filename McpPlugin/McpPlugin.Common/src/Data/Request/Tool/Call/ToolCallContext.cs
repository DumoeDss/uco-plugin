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
using System.Threading;

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    /// <summary>
    /// Runtime call context derived from <see cref="ToolCallControl"/>.
    /// The cancellation token and legacy marker are runtime-only; the token
    /// is never serialized onto the wire.
    /// </summary>
    public class ToolCallContext
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = ToolCallControl.CurrentVersion;

        [JsonPropertyName("requestID")]
        public string RequestID { get; set; } = string.Empty;

        /// <summary>Lower-camel compatibility alias; it is runtime-only.</summary>
        [JsonIgnore]
        public string RequestId
        {
            get => RequestID;
            set => RequestID = value;
        }

        [JsonPropertyName("callId")]
        public string CallId { get; set; } = string.Empty;

        [JsonPropertyName("correlationId")]
        public string CorrelationId { get; set; } = string.Empty;

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

        [JsonIgnore]
        public CancellationToken CancellationToken { get; set; }

        [JsonIgnore]
        public bool Legacy { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> UnknownMembers { get; set; }
            = new Dictionary<string, JsonElement>();

        /// <summary>Build the serializable control envelope for forwarding.</summary>
        public ToolCallControl ToControl()
        {
            var control = new ToolCallControl
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
                control.UnknownMembers[member.Key] = member.Value.Clone();

            return control;
        }

        public ToolCallContext Clone()
        {
            var clone = new ToolCallContext
            {
                Version = Version,
                RequestID = RequestID,
                CallId = CallId,
                CorrelationId = CorrelationId,
                ParentCallId = ParentCallId,
                DeadlineUnixMs = DeadlineUnixMs,
                CancellationId = CancellationId,
                IdempotencyKey = IdempotencyKey,
                CancellationToken = CancellationToken,
                Legacy = Legacy,
            };

            foreach (var member in UnknownMembers)
                clone.UnknownMembers[member.Key] = member.Value.Clone();

            return clone;
        }
    }
}
