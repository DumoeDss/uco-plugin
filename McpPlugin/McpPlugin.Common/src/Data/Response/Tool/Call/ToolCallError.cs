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

using System;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    /// <summary>Stable machine-readable error codes for controlled calls.</summary>
    public static class ToolCallErrorCodes
    {
        public const string InvalidControl = "invalid_control";
        public const string UnsupportedControlVersion = "unsupported_control_version";
        public const string DeadlineExceeded = "deadline_exceeded";
        public const string Cancelled = "cancelled";
        public const string MiddlewareRejected = "middleware_rejected";
        public const string ToolExecutionFailed = "tool_execution_failed";
    }

    /// <summary>Singular-name compatibility alias for the stable code constants.</summary>
    public static class ToolCallErrorCode
    {
        public const string InvalidControl = ToolCallErrorCodes.InvalidControl;
        public const string UnsupportedControlVersion = ToolCallErrorCodes.UnsupportedControlVersion;
        public const string DeadlineExceeded = ToolCallErrorCodes.DeadlineExceeded;
        public const string Cancelled = ToolCallErrorCodes.Cancelled;
        public const string MiddlewareRejected = ToolCallErrorCodes.MiddlewareRejected;
        public const string ToolExecutionFailed = ToolCallErrorCodes.ToolExecutionFailed;
    }

    /// <summary>
    /// Additive structured error carried by controlled responses/RPC error data.
    /// Messages are safe machine-contract text; raw exception details do not
    /// belong here.
    /// </summary>
    public class ToolCallError
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = ToolCallErrorCodes.ToolExecutionFailed;

        [JsonPropertyName("message")]
        public string Message { get; set; } = "Tool execution failed.";

        [JsonPropertyName("retryable")]
        public bool Retryable { get; set; }

        [JsonPropertyName("callId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CallId { get; set; }

        [JsonPropertyName("correlationId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; set; }

        [JsonPropertyName("details")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonNode? Details { get; set; }

        public ToolCallError() { }

        public ToolCallError(
            string code,
            string message,
            bool retryable = false,
            string? callId = null,
            string? correlationId = null,
            JsonNode? details = null)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Retryable = retryable;
            CallId = callId;
            CorrelationId = correlationId;
            Details = details;
        }
    }

    /// <summary>Descriptive alias for code that names the wire value explicitly.</summary>
    public class StructuredToolCallError : ToolCallError
    {
        public StructuredToolCallError() { }

        public StructuredToolCallError(
            string code,
            string message,
            bool retryable = false,
            string? callId = null,
            string? correlationId = null,
            JsonNode? details = null)
            : base(code, message, retryable, callId, correlationId, details) { }
    }

    /// <summary>Alias matching the Node type name.</summary>
    public class ToolCallStructuredError : ToolCallError
    {
        public ToolCallStructuredError() { }

        public ToolCallStructuredError(
            string code,
            string message,
            bool retryable = false,
            string? callId = null,
            string? correlationId = null,
            JsonNode? details = null)
            : base(code, message, retryable, callId, correlationId, details) { }
    }

    /// <summary>Compatibility alias for callers that use an error suffix.</summary>
    public class ToolCallControlError : ToolCallControlException
    {
        public ToolCallControlError(
            string code,
            string message,
            bool retryable = false,
            string? callId = null,
            string? correlationId = null,
            JsonNode? details = null,
            Exception? innerException = null)
            : base(code, message, retryable, callId, correlationId, details, innerException) { }
    }

    /// <summary>
    /// Exception used by common normalization code before a runner is called.
    /// Its <see cref="Code"/> maps directly to <see cref="ToolCallError"/>.
    /// </summary>
    public class ToolCallControlException : Exception
    {
        public string Code { get; }
        public bool Retryable { get; }
        public string? CallId { get; }
        public string? CorrelationId { get; }
        public JsonNode? Details { get; }

        public ToolCallControlException(
            string code,
            string message,
            bool retryable = false,
            string? callId = null,
            string? correlationId = null,
            JsonNode? details = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Retryable = retryable;
            CallId = callId;
            CorrelationId = correlationId;
            Details = details;
        }

        public ToolCallError ToError()
            => new ToolCallError(Code, Message, Retryable, CallId, CorrelationId, Details);
    }
}
