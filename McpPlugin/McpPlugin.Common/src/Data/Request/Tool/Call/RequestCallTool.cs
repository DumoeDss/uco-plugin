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
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    public class RequestCallTool : IRequestID, IDisposable
    {
        public string RequestID { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public IReadOnlyDictionary<string, JsonElement> Arguments { get; set; } = new Dictionary<string, JsonElement>();
        /// <summary>
        /// Optional version-one call-control metadata.  Keeping this member
        /// nullable preserves the legacy wire shape when no control object is
        /// supplied.
        /// </summary>
        [JsonPropertyName("control")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ToolCallControl? Control { get; set; }

        public RequestCallTool() { }
        public RequestCallTool(string name, IReadOnlyDictionary<string, JsonElement> arguments)
            : this(Guid.NewGuid().ToString(), name, arguments) { }
        public RequestCallTool(string requestId, string name, IReadOnlyDictionary<string, JsonElement> arguments)
        {
            RequestID = requestId ?? throw new ArgumentNullException(nameof(requestId));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Arguments = arguments ?? throw new ArgumentNullException(nameof(arguments));
        }

        public RequestCallTool(
            string requestId,
            string name,
            IReadOnlyDictionary<string, JsonElement> arguments,
            ToolCallControl? control)
            : this(requestId, name, arguments)
        {
            Control = control;
        }

        public RequestCallTool(
            string name,
            IReadOnlyDictionary<string, JsonElement> arguments,
            ToolCallControl? control)
            : this(Guid.NewGuid().ToString(), name, arguments, control) { }

        public virtual void Dispose()
        {
            // Arguments.Clear();
        }
        ~RequestCallTool() => Dispose();
    }
}
