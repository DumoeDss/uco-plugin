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
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace com.IvanMurzak.McpPlugin
{
    /// <summary>
    /// Creates configured <see cref="ClientWebSocket"/> instances for the WebSocket transport.
    /// Replaces the old <c>IHubConnectionProvider</c> which returned SignalR <c>HubConnection</c>.
    /// </summary>
    public interface IWebSocketConnectionProvider
    {
        /// <summary>
        /// JSON serialization options configured from the <c>Reflector</c> instance.
        /// Used by <c>WsRpcDispatcher</c> and <c>WsReceiveLoop</c> for envelope serialization.
        /// </summary>
        JsonSerializerOptions JsonSerializerOptions { get; }

        /// <summary>
        /// Creates an unconnected <see cref="ClientWebSocket"/> with headers configured,
        /// and returns it alongside the <see cref="Uri"/> the caller should pass to
        /// <c>ConnectAsync</c>.
        /// </summary>
        Task<(ClientWebSocket WebSocket, Uri Uri)> CreateConnectionAsync(string endpoint);
    }
}
