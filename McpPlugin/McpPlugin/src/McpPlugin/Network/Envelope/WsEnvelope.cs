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
using System.Text.Json;

namespace com.IvanMurzak.McpPlugin
{
    // ── Envelope types (match child 1 design.md D2 exactly) ──────────────

    /// <summary>
    /// RPC request: <c>{"id":"...", "method":"...", "params":{...}}</c>.
    /// </summary>
    public class WsRequest
    {
        public string Id { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public JsonElement? Params { get; set; }
    }

    /// <summary>
    /// RPC response: <c>{"id":"...", "result":{...}}</c> or
    /// <c>{"id":"...", "error":{"code":N,"message":"...","data":{...}}}</c>.
    /// </summary>
    public class WsResponse
    {
        public string Id { get; set; } = string.Empty;
        public JsonElement? Result { get; set; }
        public WsError? Error { get; set; }
    }

    /// <summary>
    /// Fire-and-forget notification (no id):
    /// <c>{"method":"...", "params":{...}}</c>.
    /// </summary>
    public class WsNotification
    {
        public string Method { get; set; } = string.Empty;
        public JsonElement? Params { get; set; }
    }

    /// <summary>
    /// JSON-RPC-like error object.
    /// </summary>
    public class WsError
    {
        public int Code { get; set; }
        public string Message { get; set; } = string.Empty;
        public JsonElement? Data { get; set; }
    }

    /// <summary>
    /// Discriminator for parsed WebSocket messages.
    /// </summary>
    public enum WsMessageType
    {
        Request,
        Response,
        Notification
    }

    /// <summary>
    /// Unified parsed message from the receive loop.
    /// Discriminate by <see cref="Type"/>, then access the relevant fields.
    /// </summary>
    public sealed class WsParsedMessage
    {
        public WsMessageType Type { get; set; }
        public string? Id { get; set; }
        public string? Method { get; set; }
        public JsonElement? Params { get; set; }
        public JsonElement? Result { get; set; }
        public WsError? Error { get; set; }
    }

    // ── Error codes (child 1 design.md D2) ───────────────────────────────

    public static class WsErrorCodes
    {
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;
        public const int PluginNotConnected = -32000;
        public const int PluginTimeout = -32001;
        public const int AuthRejected = -32002;
    }

    // ── Serialization helpers ────────────────────────────────────────────

    /// <summary>
    /// JSON envelope serialization helpers for the WebSocket transport.
    /// All messages are UTF-8 JSON text frames matching child 1 design.md D2-D4.
    /// </summary>
    public static class WsEnvelope
    {
        /// <summary>
        /// Serialize a request to UTF-8 bytes.
        /// </summary>
        public static byte[] SerializeRequest(WsRequest request, JsonSerializerOptions options)
            => JsonSerializer.SerializeToUtf8Bytes(request, options);

        /// <summary>
        /// Serialize a response to UTF-8 bytes.
        /// </summary>
        public static byte[] SerializeResponse(WsResponse response, JsonSerializerOptions options)
            => JsonSerializer.SerializeToUtf8Bytes(response, options);

        /// <summary>
        /// Serialize a notification to UTF-8 bytes.
        /// </summary>
        public static byte[] SerializeNotification(WsNotification notification, JsonSerializerOptions options)
            => JsonSerializer.SerializeToUtf8Bytes(notification, options);

        /// <summary>
        /// Parse a complete WebSocket text message into a discriminated message.
        /// Discrimination rules (child 1 D2):
        /// <list type="bullet">
        /// <item>Has "id" + "method" → Request</item>
        /// <item>Has "id" + ("result" or "error") → Response</item>
        /// <item>Has "method" without "id" → Notification</item>
        /// </list>
        /// </summary>
        public static bool TryParseMessage(
            ReadOnlyMemory<byte> utf8Json,
            JsonSerializerOptions options,
            out WsParsedMessage? message)
        {
            message = null;
            try
            {
                using var doc = JsonDocument.Parse(utf8Json);
                var root = doc.RootElement;

                bool hasId = root.TryGetProperty("id", out _);
                bool hasMethod = root.TryGetProperty("method", out _);
                bool hasResult = root.TryGetProperty("result", out _);
                bool hasError = root.TryGetProperty("error", out _);

                if (hasId && hasMethod)
                {
                    var req = JsonSerializer.Deserialize<WsRequest>(root.GetRawText(), options);
                    if (req == null) return false;
                    message = new WsParsedMessage
                    {
                        Type = WsMessageType.Request,
                        Id = req.Id,
                        Method = req.Method,
                        Params = req.Params
                    };
                    return true;
                }

                if (hasId && (hasResult || hasError))
                {
                    var resp = JsonSerializer.Deserialize<WsResponse>(root.GetRawText(), options);
                    if (resp == null) return false;
                    message = new WsParsedMessage
                    {
                        Type = WsMessageType.Response,
                        Id = resp.Id,
                        Result = resp.Result,
                        Error = resp.Error
                    };
                    return true;
                }

                if (hasMethod && !hasId)
                {
                    var notif = JsonSerializer.Deserialize<WsNotification>(root.GetRawText(), options);
                    if (notif == null) return false;
                    message = new WsParsedMessage
                    {
                        Type = WsMessageType.Notification,
                        Method = notif.Method,
                        Params = notif.Params
                    };
                    return true;
                }

                // Has "id" but neither method nor result/error — treat as empty response.
                if (hasId)
                {
                    var idElem = root.GetProperty("id");
                    message = new WsParsedMessage
                    {
                        Type = WsMessageType.Response,
                        Id = idElem.ValueKind == JsonValueKind.String
                            ? idElem.GetString()
                            : idElem.GetRawText(),
                        Result = null,
                        Error = null
                    };
                    return true;
                }

                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
