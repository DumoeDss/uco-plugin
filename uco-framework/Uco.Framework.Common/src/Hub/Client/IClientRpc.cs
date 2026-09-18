/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework.Common.Hub.Client
{
    public interface IClientRpc : IClientDisconnectable
    {
        // Task ForceDisconnect(); // Inherited from IClientDisconnectable

        /// <summary>
        /// Called once when the plugin first connects. Provides the complete snapshot of all
        /// Uco clients that are already active at the time of connection, covering the race
        /// condition where clients connected before the plugin joined.
        /// </summary>
        Task OnInitialClientData(UcoClientData[] allActiveClients);

        /// <summary>
        /// Fired when an Uco client connects. Carries the newly connected client's data and
        /// the complete list of all currently active clients (including the new one).
        /// </summary>
        Task OnPluginClientConnected(UcoClientData connectedClient, UcoClientData[] allActiveClients);

        /// <summary>
        /// Fired when an Uco client disconnects. Carries the disconnected client's data and
        /// the complete list of clients still active after the disconnection.
        /// </summary>
        Task OnPluginClientDisconnected(UcoClientData disconnectedClient, UcoClientData[] remainingClients);
    }
}
