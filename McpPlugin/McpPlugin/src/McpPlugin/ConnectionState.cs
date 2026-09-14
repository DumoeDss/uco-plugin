/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>
    /// Connection state values that map 1:1 to the old
    /// <c>Microsoft.AspNetCore.SignalR.Client.HubConnectionState</c>.
    /// Used by <see cref="IConnection"/>, <see cref="IConnectionManager"/>,
    /// and <see cref="IConnectServerHub"/>.
    /// </summary>
    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }
}
