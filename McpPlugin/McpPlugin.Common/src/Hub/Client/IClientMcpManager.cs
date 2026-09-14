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
    public interface IClientMcpManager : IClientDisconnectable
    {
        IClientToolHub? ToolHub { get; }
        IClientPromptHub? PromptHub { get; }
        IClientResourceHub? ResourceHub { get; }
        IClientSystemToolHub? SystemToolHub { get; }

        Task OnMcpClientConnected(UcoClientData connectedClient, UcoClientData[] allActiveClients);
        Task OnMcpClientDisconnected(UcoClientData disconnectedClient, UcoClientData[] remainingClients);

        /// <summary>
        /// Called once on initial connection to populate ActiveClients with the server's current
        /// snapshot, covering the edge case where clients were already connected before the plugin joined.
        /// </summary>
        Task OnInitialClientData(UcoClientData[] allActiveClients);
    }
}
