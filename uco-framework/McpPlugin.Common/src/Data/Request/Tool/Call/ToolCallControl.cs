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

namespace com.AtelierAI.Uco.Framework.Common.Model
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

        /// <summary>
        /// Explicit approval directive.  It is intentionally nullable so a
        /// missing member keeps the legacy/normalization distinction.
        /// </summary>
        [JsonPropertyName("confirm")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Confirm { get; set; }

        /// <summary>
        /// Exact g-005 vocabulary.  Null means the member was omitted and is
        /// normalized to <c>none</c>; explicit malformed values are rejected
        /// by <see cref="ToolCallContextNormalizer"/>.
        /// </summary>
        [JsonPropertyName("dryRun")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DryRun { get; set; }

        [JsonPropertyName("confirmation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolCallConfirmation? Confirmation { get; set; }

        [JsonIgnore]
        public bool IssueConfirmationOnly { get; set; }

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
                Confirm = Confirm,
                DryRun = DryRun,
                Confirmation = Confirmation?.Clone(),
                IssueConfirmationOnly = IssueConfirmationOnly,
            };

            foreach (var member in UnknownMembers)
                clone.UnknownMembers[member.Key] = member.Value.Clone();

            return clone;
        }
    }
}
