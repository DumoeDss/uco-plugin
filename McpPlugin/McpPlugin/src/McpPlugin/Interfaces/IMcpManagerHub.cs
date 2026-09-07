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
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Hub.Server;
using com.IvanMurzak.McpPlugin.Common.Model;

namespace com.IvanMurzak.McpPlugin
{
    public interface IMcpManagerHub : IConnectServerHub, IServerMcpManager
    {
        /// <summary>
        /// Gets the version handshake response status if a handshake has been performed; otherwise, null.
        /// </summary>
        VersionHandshakeResponse? VersionHandshakeStatus { get; }

        /// <summary>
        /// Registers the generation-owned capability advertisement that must complete before
        /// the connection can become ready.
        /// </summary>
        void SetCapabilityRegistrationHandler(Func<CancellationToken, Task> handler);

        Task<ResponseData> NotifyAboutUpdatedTools(RequestToolsUpdated request, CancellationToken cancellationToken);
        Task<ResponseData> NotifyAboutUpdatedPrompts(RequestPromptsUpdated request, CancellationToken cancellationToken);
        Task<ResponseData> NotifyAboutUpdatedResources(RequestResourcesUpdated request, CancellationToken cancellationToken);
    }
}
