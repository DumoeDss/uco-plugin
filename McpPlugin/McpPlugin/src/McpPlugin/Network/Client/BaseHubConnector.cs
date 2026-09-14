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
using com.AtelierAI.Uco.Framework.Common.Hub.Server;
using com.AtelierAI.Uco.Framework.Common.Model;
using Microsoft.Extensions.Logging;
using R3;
using WsState = com.AtelierAI.Uco.Framework.ConnectionState;
using Version = com.AtelierAI.Uco.Framework.Common.Version;

namespace com.AtelierAI.Uco.Framework
{
    public abstract class BaseHubConnector : IConnectServerHub, IDisposable
    {
        protected readonly ILogger _logger;
        protected readonly Version _apiVersion;
        protected readonly IConnectionManager _connectionManager;
        protected readonly CancellationTokenSource _cancellationTokenSource = new();

        private readonly ThreadSafeBool _isDisposed = new(false);
        private volatile VersionHandshakeResponse? lastHandshakeResponse = null;
        private Func<CancellationToken, Task>? _capabilityRegistrationHandler;
        private int _initializationStartedForGeneration = -1;

        /// <summary>
        /// Tracks which connection generation handlers were last registered for.
        /// Prevents re-registration on retry attempts within the same connection cycle.
        /// </summary>
        private int _handlersRegisteredForGeneration = -1;

        /// <summary>
        /// Disposable for startup subscriptions.
        /// </summary>
        protected readonly IDisposable _connectionSubscriptions;

        /// <summary>
        /// Disposables for subscription on the server events RPC calls.
        /// </summary>
        protected readonly CompositeDisposable _serverEventsDisposables = new();

        public ReadOnlyReactiveProperty<ConnectionState> ConnectionState => _connectionManager.ConnectionState;
        public ReadOnlyReactiveProperty<bool> KeepConnected => _connectionManager.KeepConnected;
        public Observable<Unit> OnAuthorizationRejected => _connectionManager.OnAuthorizationRejected;
        public VersionHandshakeResponse? VersionHandshakeStatus => lastHandshakeResponse;

        /// <summary>
        /// Optional host identity advertised under the <c>bridge-identity-v1</c>
        /// handshake capability. <c>null</c> keeps the legacy handshake shape.
        /// </summary>
        protected readonly IHandshakeIdentity? _handshakeIdentity;

        /// <summary>
        /// Primary constructor. Accepts an already-constructed <see cref="IConnectionManager"/>,
        /// enabling injection of a mock or custom implementation in tests.
        /// </summary>
        public BaseHubConnector(ILogger logger, Version apiVersion, IConnectionManager connectionManager,
            IHandshakeIdentity? handshakeIdentity = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogTrace("{class} Ctor.", GetType().Name);

            _apiVersion = apiVersion ?? throw new ArgumentNullException(nameof(apiVersion));
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));
            _handshakeIdentity = handshakeIdentity;

            var subscriptions = new CompositeDisposable();

            // The transport event starts the generation-owned initialization sequence.
            // ConnectionManager does not complete its attempt until this sequence reports
            // success or failure.
            _connectionManager.OnTransportConnected
                .Subscribe(_ => OnConnectionEstablished())
                .AddTo(subscriptions);

            _connectionSubscriptions = subscriptions;
        }

        /// <summary>
        /// Convenience constructor that creates a <see cref="ConnectionManager"/> internally.
        /// </summary>
        public BaseHubConnector(ILogger logger, Version apiVersion, string endpoint, IWebSocketConnectionProvider wsProvider,
            IHandshakeIdentity? handshakeIdentity = null)
            : this(logger, apiVersion, new ConnectionManager(
                logger ?? throw new ArgumentNullException(nameof(logger)),
                apiVersion ?? throw new ArgumentNullException(nameof(apiVersion)),
                endpoint ?? throw new ArgumentNullException(nameof(endpoint)),
                wsProvider ?? throw new ArgumentNullException(nameof(wsProvider))),
                handshakeIdentity)
        {
        }

        public Task<bool> Connect(CancellationToken cancellationToken = default)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called on disposed object. Ignoring.", nameof(Connect));
                return Task.FromResult(false);
            }
            _logger.LogDebug("{method} Connecting... to {endpoint}.",
                nameof(Connect), _connectionManager.Endpoint);
            return _connectionManager.Connect(cancellationToken);
        }

        public Task Disconnect(CancellationToken cancellationToken = default)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called on disposed object. Ignoring.",
                    nameof(Disconnect));
                return Task.CompletedTask;
            }
            _logger.LogDebug("{method} Disconnecting... from {endpoint}.",
                nameof(Disconnect), _connectionManager.Endpoint);
            return _connectionManager.Disconnect(cancellationToken);
        }

        public void DisconnectImmediate()
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called on disposed object. Ignoring.",
                    nameof(DisconnectImmediate));
                return;
            }

            _logger.LogDebug("{method}... from {endpoint}.",
                nameof(DisconnectImmediate), _connectionManager.Endpoint);

            _connectionManager.DisconnectImmediate();
        }

        public Task<VersionHandshakeResponse> PerformVersionHandshake(RequestVersionHandshake request) => PerformVersionHandshake(request, _cancellationTokenSource.Token);
        public async Task<VersionHandshakeResponse> PerformVersionHandshake(RequestVersionHandshake request, CancellationToken cancellationToken = default)
        {
            if (_isDisposed.Value)
                throw new ObjectDisposedException(GetType().Name, "Can't perform version handshake on disposed object.");

            _logger.LogTrace("{class} Performing version handshake.", GetType().Name);

            try
            {
                var response = await _connectionManager.InvokeAsync<RequestVersionHandshake, VersionHandshakeResponse>(
                    nameof(IServerMcpManager.PerformVersionHandshake), request, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{class} Version handshake cancelled.", GetType().Name);
                    return new VersionHandshakeResponse
                    {
                        ApiVersion = "Unknown",
                        ServerVersion = "Unknown",
                        Compatible = false,
                        Message = "Version handshake was cancelled.",
                        IsConnectionError = true
                    };
                }

                if (response == null)
                {
                    _logger.LogError("{class} Version handshake failed: No response from server.", GetType().Name);
                    return new VersionHandshakeResponse
                    {
                        ApiVersion = "Unknown",
                        ServerVersion = "Unknown",
                        Compatible = false,
                        Message = "Version handshake failed with null response.",
                        IsConnectionError = true
                    };
                }

                _logger.LogInformation("{class} Version handshake completed. Compatible: {Compatible}, Message: {Message}",
                    GetType().Name, response.Compatible, response.Message);

                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{class} Version handshake failed: {Error}", GetType().Name, ex.Message);
                return new VersionHandshakeResponse
                {
                    ApiVersion = "Unknown",
                    ServerVersion = "Unknown",
                    Compatible = false,
                    Message = "Version handshake failed with exception: " + ex.Message,
                    IsConnectionError = true
                };
            }
        }

        public void SetCapabilityRegistrationHandler(Func<CancellationToken, Task> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            if (_isDisposed.Value)
                throw new ObjectDisposedException(GetType().Name);

            _capabilityRegistrationHandler = handler;
        }

        /// <summary>
        /// Called when a transport generation starts its owned initialization.
        /// Clears old handlers and registers the new generation before the version handshake.
        /// </summary>
        private void OnConnectionCycleStarted(int generation)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called on disposed object. Ignoring.", nameof(OnConnectionCycleStarted));
                return;
            }

            if (generation == _handlersRegisteredForGeneration)
                return; // Already registered for this connection cycle

            _handlersRegisteredForGeneration = generation;

            _logger.LogTrace("{method} Clearing server events disposables and registering handlers for generation {gen}.",
                nameof(OnConnectionCycleStarted), generation);

            _serverEventsDisposables.Clear();

            OnBeforeSubscribeToServerEvents();
            RegisterServerHandlers(_connectionManager, _serverEventsDisposables);
        }

        private async void OnConnectionEstablished()
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{method} called on disposed object. Ignoring.", nameof(OnConnectionEstablished));
                return;
            }

            var generation = _connectionManager.ConnectionGeneration;
            var previousGeneration = Interlocked.Exchange(
                ref _initializationStartedForGeneration,
                generation);
            if (previousGeneration == generation)
                return;

            try
            {
                await OnConnectionEstablishedCore(generation);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{method} Unhandled exception during connection establishment.", nameof(OnConnectionEstablished));
                _connectionManager.ReportConnectionInitializationFailure(
                    generation,
                    "registration-failed",
                    ex.GetBaseException().Message);
            }
        }

        private async Task OnConnectionEstablishedCore(int generation)
        {
            if (generation != _connectionManager.ConnectionGeneration) return;
            try
            {
                OnConnectionCycleStarted(generation);
            }
            catch (Exception ex)
            {
                _connectionManager.ReportConnectionInitializationFailure(
                    generation,
                    "handler-registration-failed",
                    ex.GetBaseException().Message);
                return;
            }

            var serverEventsCts = _serverEventsDisposables.ToCancellationTokenSource();
            using var lifecycleCts = CancellationTokenSource.CreateLinkedTokenSource(
                serverEventsCts.Token,
                _connectionManager.ConnectionCancellationToken,
                _cancellationTokenSource.Token);
            var cancellationToken = lifecycleCts.Token;
            _connectionManager.ReportAttemptStage("handshake");

            // Perform version handshake after handlers are registered
            var handshakeResponse = await PerformVersionHandshake(
                request: CreateVersionHandshake(generation),
                cancellationToken: cancellationToken);

            if (cancellationToken.IsCancellationRequested ||
                generation != _connectionManager.ConnectionGeneration)
                return;

            lastHandshakeResponse = handshakeResponse;

            if (handshakeResponse == null || handshakeResponse.IsConnectionError)
            {
                var reason = handshakeResponse?.Message ?? "No response from server";
                _logger.LogWarning("{class} Version handshake failed. Reason: {reason}",
                    GetType().Name, reason);
                _connectionManager.ReportConnectionInitializationFailure(
                    generation,
                    "handshake-failed",
                    reason);
                return;
            }

            if (!handshakeResponse.Compatible)
            {
                LogVersionMismatchError(handshakeResponse);
                _logger.LogError("{class} Version mismatch — disconnecting. Server: {serverVersion}, API: {apiVersion}, Message: {message}",
                    GetType().Name, handshakeResponse.ServerVersion, handshakeResponse.ApiVersion, handshakeResponse.Message);
                _connectionManager.ReportConnectionInitializationFailure(
                    generation,
                    "rejected",
                    handshakeResponse.Message);
                return;
            }

            try
            {
                _connectionManager.ReportAttemptStage("registering-capabilities");
                await OnConnectedAsync(cancellationToken);
                if (_capabilityRegistrationHandler != null)
                    await _capabilityRegistrationHandler(cancellationToken);
            }
            catch (Exception ex)
            {
                _connectionManager.ReportConnectionInitializationFailure(
                    generation,
                    "registration-failed",
                    ex.GetBaseException().Message);
                return;
            }

            if (cancellationToken.IsCancellationRequested ||
                generation != _connectionManager.ConnectionGeneration)
                return;

            _connectionManager.TrySetConnected(generation);
        }

        /// <summary>
        /// Builds the per-generation handshake request. The base shape advertises the
        /// wire capabilities this connector implements; when a host supplied an
        /// <see cref="IHandshakeIdentity"/> the request additionally carries the
        /// <c>bridge-identity-v1</c> capability and the Editor identity members so the
        /// server can pin and verify routing against this specific Editor.
        /// </summary>
        protected virtual RequestVersionHandshake CreateVersionHandshake(int generation)
        {
            var identity = _handshakeIdentity;
            if (identity == null)
            {
                return new RequestVersionHandshake
                {
                    RequestID = Guid.NewGuid().ToString(),
                    ApiVersion = _apiVersion.Api,
                    PluginVersion = _apiVersion.Plugin,
                    Environment = _apiVersion.Environment,
                    Capabilities = new[] { "cancel-tool-call-v1", "operation-identity-v1" },
                    Generation = generation
                };
            }

            return new RequestVersionHandshake
            {
                RequestID = Guid.NewGuid().ToString(),
                ApiVersion = _apiVersion.Api,
                PluginVersion = _apiVersion.Plugin,
                Environment = _apiVersion.Environment,
                Capabilities = new[] { "cancel-tool-call-v1", "operation-identity-v1", "bridge-identity-v1" },
                Generation = generation,
                ProjectPath = identity.ProjectPath,
                EditorPid = identity.EditorPid,
                UnityVersion = identity.UnityVersion,
                InstanceId = identity.InstanceId
            };
        }

        private void LogVersionMismatchError(VersionHandshakeResponse handshakeResponse)
        {
            var errorMessage = $"API VERSION MISMATCH: {handshakeResponse.Message}";
            _logger.LogError(errorMessage);
        }

        /// <summary>
        /// Called once per connection cycle, right before <see cref="RegisterServerHandlers"/>.
        /// Override to reset per-connection state that must be clean before any server
        /// notifications can arrive (e.g. epoch counters, flags).
        /// </summary>
        protected virtual void OnBeforeSubscribeToServerEvents() { }

        /// <summary>
        /// Registers all incoming-method handlers (requests and notifications) via
        /// <see cref="IConnectionManager.RegisterHandler{TParam, TResult}"/> and
        /// <see cref="IConnectionManager.RegisterNotification{TParam}"/>.
        /// Replaces the old <c>SubscribeOnServerEvents(HubConnection, disposables)</c>.
        /// </summary>
        protected abstract void RegisterServerHandlers(IConnectionManager connectionManager, CompositeDisposable disposables);

        /// <summary>
        /// Called once after a successful connection and version handshake.
        /// Override to perform post-connect initialization (e.g. fetching initial state).
        /// </summary>
        protected virtual Task OnConnectedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public virtual void Dispose()
        {
            if (!_isDisposed.TrySetTrue())
                return;

            GC.SuppressFinalize(this);
            _logger.LogDebug("{method} called.", nameof(Dispose));

            if (!_cancellationTokenSource.IsCancellationRequested)
                _cancellationTokenSource.Cancel();

            _cancellationTokenSource.Dispose();
            _serverEventsDisposables.Dispose();
            _connectionSubscriptions.Dispose();

            _connectionManager.Dispose();

            _logger.LogDebug("{method} completed.", nameof(Dispose));
        }
    }
}
