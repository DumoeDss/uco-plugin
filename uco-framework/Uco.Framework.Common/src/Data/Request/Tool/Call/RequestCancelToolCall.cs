/*
┌────────────────────────────────────────────────────────────────────────┐
│  Additive same-generation cancellation for one in-flight tool call.    │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System.Text.Json.Serialization;

namespace com.AtelierAI.Uco.Framework.Common.Model
{
    public class RequestCancelToolCall
    {
        [JsonPropertyName("requestID")]
        public string RequestID { get; set; } = string.Empty;

        [JsonPropertyName("callId")]
        public string CallId { get; set; } = string.Empty;

        [JsonPropertyName("cancellationId")]
        public string CancellationId { get; set; } = string.Empty;

        [JsonPropertyName("generation")]
        public int Generation { get; set; }

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    public class ResponseCancelToolCall
    {
        [JsonPropertyName("accepted")]
        public bool Accepted { get; set; }

        [JsonPropertyName("code")]
        public string Code { get; set; } = "cancellation_unavailable";

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }
}
