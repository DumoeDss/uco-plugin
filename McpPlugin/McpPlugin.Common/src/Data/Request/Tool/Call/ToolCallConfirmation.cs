/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the MIT License.                                      │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    /// <summary>
    /// Opaque confirmation material issued by a policy planner.  The common
    /// transport layer validates the required shape but deliberately does not
    /// interpret the binding; Unity's authoring policy remains the owner.
    /// </summary>
    public sealed class ToolCallConfirmation
    {
        [JsonPropertyName("planId")]
        public string? PlanId { get; set; }

        [JsonPropertyName("planHash")]
        public string? PlanHash { get; set; }

        [JsonPropertyName("expiresAtUnixMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? ExpiresAtUnixMs { get; set; }

        /// <summary>Unknown token members are retained for forward compatibility.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement> UnknownMembers { get; set; }
            = new Dictionary<string, JsonElement>();

        public ToolCallConfirmation Clone()
        {
            var clone = new ToolCallConfirmation
            {
                PlanId = PlanId,
                PlanHash = PlanHash,
                ExpiresAtUnixMs = ExpiresAtUnixMs,
            };

            foreach (var member in UnknownMembers)
                clone.UnknownMembers[member.Key] = member.Value.Clone();

            return clone;
        }
    }
}
