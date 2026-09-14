/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

namespace com.IvanMurzak.McpPlugin.Common.Model
{
    /// <summary>
    /// Serialized as lowercase strings on the wire: "error", "success", "processing".
    /// This MUST match the Node server's expected wire format (child 1 design.md D4 amendment 6).
    /// Note: the lowercase serialization is configured via JsonStringEnumConverter in the
    /// WebSocketConnectionProvider's JsonSerializerOptions (netstandard2.1 does not support
    /// JsonStringEnumConverter as an attribute).
    /// </summary>
    public enum ResponseStatus
    {
        Error, // request failed
        Success, // request completed successfully
        Processing // the request needs longer processing. It will be callback later, please wait
    }
}
