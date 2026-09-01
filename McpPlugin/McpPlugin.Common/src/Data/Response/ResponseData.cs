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

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    public class ResponseData<T> : ResponseData, IRequestID
    {
        public T? Value { get; set; }

        public ResponseData() : base() { }
        public ResponseData(string requestId, ResponseStatus status)
        {
            RequestID = requestId ?? throw new ArgumentNullException(nameof(requestId));
            Status = status;
        }

        public new ResponseData<T> SetRequestID(string requestId)
        {
            base.SetRequestID(requestId);
            return this;
        }

        public static new ResponseData<T> Success(string requestId, string? message = null) => new ResponseData<T>(requestId, ResponseStatus.Success)
        {
            Message = message
        };
        public static new ResponseData<T> Error(string requestId, string? message = null) => new ResponseData<T>(requestId, ResponseStatus.Error)
        {
            Message = message
        };
        public static new ResponseData<T> Processing(string requestId, string? message = null) => new ResponseData<T>(requestId, ResponseStatus.Processing)
        {
            Message = message
        };
    }

    public class ResponseData : IRequestID
    {
        public string RequestID { get; set; } = string.Empty;
        public ResponseStatus Status { get; set; }
        public string? Message { get; set; }

        /// <summary>
        /// Optional structured error for controlled calls.  The member is
        /// additive: legacy callers can continue to deserialize responses
        /// without looking at it.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public ToolCallError? StructuredError { get; set; }

        /// <summary>Non-wire alias for callers that prefer an error-data name.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public ToolCallError? ErrorData
        {
            get => StructuredError;
            set => StructuredError = value;
        }

        public ResponseData() { }
        public ResponseData(string requestId, ResponseStatus status)
        {
            RequestID = requestId ?? throw new ArgumentNullException(nameof(requestId));
            Status = status;
        }

        public virtual ResponseData SetRequestID(string requestId)
        {
            RequestID = requestId;
            return this;
        }

        public static ResponseData Success(string requestId, string? message = null) => new ResponseData(requestId, ResponseStatus.Success)
        {
            Message = message
        };
        public static ResponseData Error(string requestId, string? message = null) => new ResponseData(requestId, ResponseStatus.Error)
        {
            Message = message
        };
        public static ResponseData Processing(string requestId, string? message = null) => new ResponseData(requestId, ResponseStatus.Processing)
        {
            Message = message
        };
    }
}
