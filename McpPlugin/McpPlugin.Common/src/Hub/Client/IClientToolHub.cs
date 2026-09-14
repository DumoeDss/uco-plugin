/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework.Common.Hub.Client
{
    public interface IClientToolHub
    {
        Task<ResponseData<ResponseCallTool>> RunCallTool(RequestCallTool request);

        /// <summary>
        /// Token-aware overload used by the WebSocket request handler. The
        /// default implementation keeps existing third-party hubs source and
        /// binary compatible while allowing managers to observe cancellation.
        /// </summary>
        Task<ResponseData<ResponseCallTool>> RunCallTool(
            RequestCallTool request,
            CancellationToken cancellationToken = default)
            => RunCallTool(request);

        Task<ResponseData<ResponseListTool[]>> RunListTool(RequestListTool request, CancellationToken cancellationToken = default);
    }
}
