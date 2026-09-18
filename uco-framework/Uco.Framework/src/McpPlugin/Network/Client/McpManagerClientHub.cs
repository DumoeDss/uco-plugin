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
using com.AtelierAI.Uco.Framework.Common;
using com.AtelierAI.Uco.Framework.Common.Hub.Client;
using com.AtelierAI.Uco.Framework.Common.Hub.Server;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.AtelierAI.Uco.Framework.Common.Utils;
using Microsoft.Extensions.Logging;
using R3;
using Version = com.AtelierAI.Uco.Framework.Common.Version;

namespace com.AtelierAI.Uco.Framework
{
    // ── Notification params wrapper types ────────────────────────────────
    // The Node server sends each notification's params as a single wrapper object
    // (not positional arguments like SignalR). These types provide the correct
    // deserialization shape with the camelCase naming policy.

    public class OnInitialClientDataParams
    {
        public UcoClientData[] Clients { get; set; } = Array.Empty<UcoClientData>();
    }

    public class OnPluginClientConnectedParams
    {
        public UcoClientData Connected { get; set; } = null!;
        public UcoClientData[] All { get; set; } = Array.Empty<UcoClientData>();
    }

    public class OnPluginClientDisconnectedParams
    {
        public UcoClientData Disconnected { get; set; } = null!;
        public UcoClientData[] Remaining { get; set; } = Array.Empty<UcoClientData>();
    }

    public class ForceDisconnectParams
    {
        public string? Reason { get; set; }
    }

    // ── UcoManagerClientHub ──────────────────────────────────────────────

    public class UcoManagerClientHub : BaseHubConnector, IMcpManagerHub
    {
        readonly IClientMcpManager _mcpManager;

        /// <summary>
        /// Incremented each time a live server notification (connect/disconnect) is received.
        /// Used to detect whether state changed during the async initial-data fetch, so we
        /// can skip applying a stale snapshot.
        /// </summary>
        private volatile int _liveNotificationEpoch = 0;

        public UcoManagerClientHub(
            ILogger<UcoManagerClientHub> logger,
            Version apiVersion,
            IWebSocketConnectionProvider wsProvider,
            IClientMcpManager mcpManager,
            IHandshakeIdentity? handshakeIdentity = null)
            : base(
                logger: logger,
                apiVersion: apiVersion,
                endpoint: Consts.Hub.RemoteApp,
                wsProvider: wsProvider,
                handshakeIdentity: handshakeIdentity)
        {
            _mcpManager = mcpManager ?? throw new ArgumentNullException(nameof(mcpManager));
        }

        #region Client Events

        protected override void OnBeforeSubscribeToServerEvents()
        {
            _liveNotificationEpoch = 0;
        }

        protected override void RegisterServerHandlers(IConnectionManager connectionManager, CompositeDisposable disposables)
        {
            var expectedGeneration = connectionManager.ConnectionGeneration;

            // ── Notifications (no response expected) ──────────────────────

            connectionManager.RegisterNotification<OnInitialClientDataParams>(
                nameof(IClientMcpRpc.OnInitialClientData),
                async param =>
                {
                    _logger.LogDebug("{class}.{method}", nameof(IClientMcpRpc), nameof(IClientMcpRpc.OnInitialClientData));
                    if (_liveNotificationEpoch != 0)
                    {
                        _logger.LogDebug("{class}.{method} Discarding stale initial snapshot: live notifications already received.",
                            nameof(UcoManagerClientHub), nameof(IClientMcpRpc.OnInitialClientData));
                        return;
                    }
                    if (param != null)
                        await _mcpManager.OnInitialClientData(param.Clients);
                })
                .AddTo(disposables);

            connectionManager.RegisterNotification<OnPluginClientConnectedParams>(
                nameof(IClientMcpRpc.OnPluginClientConnected),
                async param =>
                {
                    _logger.LogDebug("{class}.{method}", nameof(IClientMcpRpc), nameof(IClientMcpRpc.OnPluginClientConnected));
                    Interlocked.Increment(ref _liveNotificationEpoch);
                    if (param != null)
                        await _mcpManager.OnPluginClientConnected(param.Connected, param.All);
                })
                .AddTo(disposables);

            connectionManager.RegisterNotification<OnPluginClientDisconnectedParams>(
                nameof(IClientMcpRpc.OnPluginClientDisconnected),
                async param =>
                {
                    _logger.LogDebug("{class}.{method}", nameof(IClientMcpRpc), nameof(IClientMcpRpc.OnPluginClientDisconnected));
                    Interlocked.Increment(ref _liveNotificationEpoch);
                    if (param != null)
                        await _mcpManager.OnPluginClientDisconnected(param.Disconnected, param.Remaining);
                })
                .AddTo(disposables);

            connectionManager.RegisterNotification<ForceDisconnectParams>(
                nameof(IClientMcpRpc.ForceDisconnect),
                async param =>
                {
                    _logger.LogDebug("{class}.{method}", nameof(IClientMcpRpc), nameof(IClientMcpRpc.ForceDisconnect));

                    // Guard against stale ForceDisconnect messages using generation counter.
                    if (connectionManager.ConnectionGeneration != expectedGeneration)
                    {
                        _logger.LogWarning("{class}.{method} Received ForceDisconnect on a stale connection — ignoring to protect the active connection.",
                            nameof(UcoManagerClientHub), nameof(IClientMcpRpc.ForceDisconnect));
                        return;
                    }

                    var reason = param?.Reason;
                    if (!string.IsNullOrEmpty(reason))
                        _logger.LogError("Server forcefully disconnected this plugin. Reason: {Reason}", reason);
                    else
                        _logger.LogError("Server forcefully disconnected this plugin.");

                    var isAuthFailure = reason != null &&
                        (reason.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
                         reason.Contains("Token", StringComparison.OrdinalIgnoreCase));

                    await _mcpManager.ForceDisconnect();
                    await _connectionManager.Disconnect();

                    if (isAuthFailure)
                    {
                        _logger.LogWarning("Server rejected authorization. Firing OnAuthorizationRejected event.");
                        _connectionManager.NotifyAuthorizationRejected();
                    }
                })
                .AddTo(disposables);

            // ── Tool events (request-response) ────────────────────────────

            connectionManager.RegisterHandler<RequestCancelToolCall, ResponseCancelToolCall>(
                "CancelToolCall",
                (request, requestCancellationToken) => Task.FromResult<ResponseCancelToolCall?>(
                    _connectionManager.CancelToolCall(request)))
                .AddTo(disposables);

            if (_mcpManager.ToolHub != null)
            {
                connectionManager.RegisterHandler<RequestCallTool, ResponseData<ResponseCallTool>>(
                    nameof(IClientToolHub.RunCallTool),
                    (data, requestCancellationToken) =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientToolHub), nameof(IClientToolHub.RunCallTool));
                        return _mcpManager.ToolHub!.RunCallTool(data!, requestCancellationToken);
                    })
                    .AddTo(disposables);

                connectionManager.RegisterHandler<RequestListTool, ResponseData<ResponseListTool[]>>(
                    nameof(IClientToolHub.RunListTool),
                    data =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientToolHub), nameof(IClientToolHub.RunListTool));
                        return _mcpManager.ToolHub!.RunListTool(data!);
                    })
                    .AddTo(disposables);
            }

            // ── Prompt events (request-response) ──────────────────────────

            if (_mcpManager.PromptHub != null)
            {
                connectionManager.RegisterHandler<RequestGetPrompt, ResponseData<ResponseGetPrompt>>(
                    nameof(IClientPromptHub.RunGetPrompt),
                    data =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientPromptHub), nameof(IClientPromptHub.RunGetPrompt));
                        return _mcpManager.PromptHub!.RunGetPrompt(data!);
                    })
                    .AddTo(disposables);

                connectionManager.RegisterHandler<RequestListPrompts, ResponseData<ResponseListPrompts>>(
                    nameof(IClientPromptHub.RunListPrompts),
                    data =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientPromptHub), nameof(IClientPromptHub.RunListPrompts));
                        return _mcpManager.PromptHub!.RunListPrompts(data!);
                    })
                    .AddTo(disposables);
            }

            // ── Resource events (request-response) ────────────────────────

            if (_mcpManager.ResourceHub != null)
            {
                connectionManager.RegisterHandler<RequestResourceContent, ResponseData<ResponseResourceContent[]>>(
                    nameof(IClientResourceHub.RunResourceContent),
                    data =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientResourceHub), nameof(IClientResourceHub.RunResourceContent));
                        return _mcpManager.ResourceHub!.RunResourceContent(data!);
                    })
                    .AddTo(disposables);

                connectionManager.RegisterHandler<RequestListResources, ResponseData<ResponseListResource[]>>(
                    nameof(IClientResourceHub.RunListResources),
                    data =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientResourceHub), nameof(IClientResourceHub.RunListResources));
                        return _mcpManager.ResourceHub!.RunListResources(data!);
                    })
                    .AddTo(disposables);

                connectionManager.RegisterHandler<RequestListResourceTemplates, ResponseData<ResponseResourceTemplate[]>>(
                    nameof(IClientResourceHub.RunResourceTemplates),
                    data =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientResourceHub), nameof(IClientResourceHub.RunResourceTemplates));
                        return _mcpManager.ResourceHub!.RunResourceTemplates(data!);
                    })
                    .AddTo(disposables);
            }

            // ── System tool events (request-response) ─────────────────────

            if (_mcpManager.SystemToolHub != null)
            {
                connectionManager.RegisterHandler<RequestCallTool, ResponseData<ResponseCallTool>>(
                    nameof(IClientSystemToolHub.RunSystemTool),
                    (data, requestCancellationToken) =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientSystemToolHub), nameof(IClientSystemToolHub.RunSystemTool));
                        return _mcpManager.SystemToolHub!.RunSystemTool(data!, requestCancellationToken);
                    })
                    .AddTo(disposables);

                connectionManager.RegisterHandler<RequestListTool, ResponseData<ResponseListTool[]>>(
                    nameof(IClientSystemToolHub.RunListSystemTool),
                    data =>
                    {
                        _logger.LogDebug("{class}.{method}", nameof(IClientSystemToolHub), nameof(IClientSystemToolHub.RunListSystemTool));
                        return _mcpManager.SystemToolHub!.RunListSystemTool(data!);
                    })
                    .AddTo(disposables);
            }
        }

        #endregion

        #region Server Calls

        public Task<ResponseData> NotifyAboutUpdatedTools(RequestToolsUpdated request) => NotifyAboutUpdatedTools(request, _cancellationTokenSource.Token);
        public Task<ResponseData> NotifyAboutUpdatedTools(RequestToolsUpdated request, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("{class}.{method}", nameof(IServerMcpManager), nameof(IServerMcpManager.NotifyAboutUpdatedTools));
            return _connectionManager.InvokeAsync<RequestToolsUpdated, ResponseData>(nameof(IServerMcpManager.NotifyAboutUpdatedTools), request, cancellationToken);
        }

        public Task<ResponseData> NotifyAboutUpdatedPrompts(RequestPromptsUpdated request) => NotifyAboutUpdatedPrompts(request, _cancellationTokenSource.Token);
        public Task<ResponseData> NotifyAboutUpdatedPrompts(RequestPromptsUpdated request, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("{class}.{method}", nameof(IServerMcpManager), nameof(IServerMcpManager.NotifyAboutUpdatedPrompts));
            return _connectionManager.InvokeAsync<RequestPromptsUpdated, ResponseData>(nameof(IServerMcpManager.NotifyAboutUpdatedPrompts), request, cancellationToken);
        }

        public Task<ResponseData> NotifyAboutUpdatedResources(RequestResourcesUpdated request) => NotifyAboutUpdatedResources(request, _cancellationTokenSource.Token);
        public Task<ResponseData> NotifyAboutUpdatedResources(RequestResourcesUpdated request, CancellationToken cancellationToken = default)
        {
            _logger.LogTrace("{class}.{method}", nameof(IServerMcpManager), nameof(IServerMcpManager.NotifyAboutUpdatedResources));
            return _connectionManager.InvokeAsync<RequestResourcesUpdated, ResponseData>(nameof(IServerMcpManager.NotifyAboutUpdatedResources), request, cancellationToken);
        }

        public Task<ResponseData> NotifyToolRequestCompleted(RequestToolCompletedData request) => NotifyToolRequestCompleted(request, _cancellationTokenSource.Token);
        public Task<ResponseData> NotifyToolRequestCompleted(RequestToolCompletedData request, CancellationToken cancellationToken = default)
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("{class}.{method} request: {RequestId}\n{Json}",
                    nameof(IServerMcpManager),
                    nameof(IServerMcpManager.NotifyToolRequestCompleted),
                    request.RequestId,
                    request.ToPrettyJson()
                );
            }
            return _connectionManager.InvokeAsync<RequestToolCompletedData, ResponseData>(nameof(IServerMcpManager.NotifyToolRequestCompleted), request, cancellationToken);
        }

        public Task<UcoClientData[]> GetMcpClientData()
        {
            _logger.LogTrace("{class}.{method}", nameof(IServerMcpManager), nameof(IServerMcpManager.GetMcpClientData));
            return _connectionManager.InvokeAsync<UcoClientData[]>(nameof(IServerMcpManager.GetMcpClientData), _cancellationTokenSource.Token);
        }

        public Task<UcoServerData> GetServerData()
        {
            _logger.LogTrace("{class}.{method}", nameof(IServerMcpManager), nameof(IServerMcpManager.GetServerData));
            return _connectionManager.InvokeAsync<UcoServerData>(nameof(IServerMcpManager.GetServerData), _cancellationTokenSource.Token);
        }

        protected override Task OnConnectedAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("{class}.{method} Connected. Waiting for server-pushed initial client data snapshot.",
                nameof(UcoManagerClientHub), nameof(OnConnectedAsync));
            return Task.CompletedTask;
        }

        #endregion
    }
}
