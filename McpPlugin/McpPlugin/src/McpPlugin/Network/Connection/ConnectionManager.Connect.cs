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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using R3;
using WsState = com.IvanMurzak.McpPlugin.ConnectionState;

namespace com.IvanMurzak.McpPlugin
{
    public partial class ConnectionManager : IConnectionManager, IAsyncDisposable
    {
        // Pending URI from the provider (stored when the WebSocket is created,
        // consumed by AttemptConnection when calling ConnectAsync).
        private Uri? _pendingConnectUri;

        public async Task<bool> Connect(CancellationToken cancellationToken = default)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(Connect));
                return false;
            }

            _logger.LogDebug("{class}[{guid}] {method} called.",
                nameof(ConnectionManager), _guid, nameof(Connect));

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection canceled before starting for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(Connect), Endpoint);
                return false;
            }

            // Check if there's already an ongoing connection attempt
            await _ongoingConnectionGate.WaitAsync(cancellationToken);
            var ongoingTask = _ongoingConnectionTask;
            _ongoingConnectionGate.Release();

            if (ongoingTask != null)
                return await WaitForConnectionCompletion(ongoingTask, cancellationToken);

            try
            {
                _logger.LogDebug("{class}[{guid}] {method} acquiring gate.",
                    nameof(ConnectionManager), _guid, nameof(Connect));

                await _gate.WaitAsync(cancellationToken);

                _logger.LogDebug("{class}[{guid}] {method} acquired gate.",
                    nameof(ConnectionManager), _guid, nameof(Connect));
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection canceled while waiting for gate for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(Connect), Endpoint);
                return false;
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{class}[{guid}] {method} Connection canceled before starting for endpoint: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(Connect), Endpoint);
                    return false;
                }

                if (_isDisposed.Value)
                {
                    _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                        nameof(ConnectionManager), _guid, nameof(Connect));
                    return false;
                }

                // Double-check for ongoing task after acquiring gate
                await _ongoingConnectionGate.WaitAsync(cancellationToken);
                ongoingTask = _ongoingConnectionTask;
                _ongoingConnectionGate.Release();

                if (ongoingTask != null)
                {
                    _logger.LogDebug("{class}[{guid}] {method} Connection already in progress after acquiring gate, releasing gate and waiting.",
                        nameof(ConnectionManager), _guid, nameof(Connect));
                    _gate.Release();
                    return await WaitForConnectionCompletion(ongoingTask, cancellationToken);
                }

                if (_webSocket.CurrentValue?.State is WebSocketState.Open or WebSocketState.Connecting)
                {
                    _logger.LogDebug("{class}[{guid}] {method} Already connected. Ignoring.",
                        nameof(ConnectionManager), _guid, nameof(Connect));
                    return true;
                }

                // Dispose the previous internal CancellationTokenSource if it exists
                CancelInternalToken(dispose: true);

                internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancellationToken = internalCts.Token;

                _continueToReconnect.Value = true;

                Task<bool> connectionTask;
                await _ongoingConnectionGate.WaitAsync(cancellationToken);
                connectionTask = InternalConnect(cancellationToken);
                _ongoingConnectionTask = connectionTask;
                _ongoingConnectionGate.Release();
                try
                {
                    return await connectionTask;
                }
                finally
                {
                    // Use CancellationToken.None: cleanup must run even when cancellationToken
                    // (the internal CTS token) has already been cancelled by DisconnectImmediate.
                    await _ongoingConnectionGate.WaitAsync(CancellationToken.None);
                    _ongoingConnectionTask = null;
                    _ongoingConnectionGate.Release();
                }
            }
            finally
            {
                _logger.LogDebug("{class}[{guid}] {method} releasing gate.",
                    nameof(ConnectionManager), _guid, nameof(Connect));
                _gate.Release();
            }
        }

        private async Task<bool> WaitForConnectionCompletion(Task<bool> ongoingTask, CancellationToken cancellationToken)
        {
            _logger.LogDebug("{class}[{guid}] {method} Connection already in progress, waiting for existing attempt.",
                nameof(ConnectionManager), _guid, nameof(WaitForConnectionCompletion));
            try
            {
                var completedTask = await Task.WhenAny(ongoingTask, Task.Delay(Timeout.Infinite, cancellationToken));
                if (completedTask != ongoingTask)
                {
                    _logger.LogWarning("{class}[{guid}] {method} Waiting for ongoing connection was canceled for endpoint: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(WaitForConnectionCompletion), Endpoint);
                    return false;
                }
                return await ongoingTask;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("{class}[{guid}] {method} Ongoing connection was canceled for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(WaitForConnectionCompletion), Endpoint);
                return false;
            }
        }

        private async Task<bool> InternalConnect(CancellationToken cancellationToken)
        {
            try
            {
                if (_isDisposed.Value)
                {
                    _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                        nameof(ConnectionManager), _guid, nameof(InternalConnect));
                    return false;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{class}[{guid}] {method} Connection canceled before creating WebSocket for endpoint: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(InternalConnect), Endpoint);
                    return false;
                }

                _logger.LogDebug("{class}[{guid}] {method} called.",
                    nameof(ConnectionManager), _guid, nameof(InternalConnect));

                if (!await CreateWebSocketIfNeeded(cancellationToken))
                    return false;

                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{class}[{guid}] {method} Connection canceled before starting connection loop for endpoint: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(InternalConnect), Endpoint);
                    return false;
                }

                return await StartConnectionLoop(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection was canceled for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(InternalConnect), Endpoint);
                return false;
            }
            catch (WebSocketException ex)
            {
                _logger.LogError("{class}[{guid}] {method} WebSocketException during connection: {message}\n{stackTrace}",
                    nameof(ConnectionManager), _guid, nameof(InternalConnect), ex.Message, ex.StackTrace);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError("{class}[{guid}] {method} Unexpected error during connection: {message}\n{stackTrace}",
                    nameof(ConnectionManager), _guid, nameof(InternalConnect), ex.Message, ex.StackTrace);
                return false;
            }
        }

        private async Task<bool> EnsureConnection(CancellationToken cancellationToken)
        {
            if (_connectionState.Value is WsState.Connected)
                return true;

            if (!_continueToReconnect.CurrentValue)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection not available and auto-reconnect disabled for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(EnsureConnection), Endpoint);
                return false;
            }

            _logger.LogDebug("{class}[{guid}] {method} Connection is not established. Attempting to connect to: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(EnsureConnection), Endpoint);

            await Connect(cancellationToken);

            if (_connectionState.Value is not WsState.Connected)
            {
                _logger.LogWarning("{class}[{guid}] {method} Failed to establish connection to remote endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(EnsureConnection), Endpoint);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Creates a ClientWebSocket if needed. Must be called from within a _gate-protected section.
        /// </summary>
        private async Task<bool> CreateWebSocketIfNeeded(CancellationToken cancellationToken)
        {
            var existing = _webSocket.CurrentValue;
            if (existing != null && existing.State != WebSocketState.Closed && existing.State != WebSocketState.Aborted)
                return true;

            // Dispose previous observable/logger subscriptions
            _wsLogger?.Dispose();
            _wsObservable?.Dispose();
            _receiveLoop?.Dispose();
            _wsReconnectSubscription.Disposable = null;

            // After SignalR auto-reconnect exhausts retries, the connection is stuck in
            // Closed/Aborted state. Dispose it so a fresh transport can succeed.
            if (existing != null)
            {
                _logger.LogDebug("{class}[{guid}] {method} Disposing existing closed WebSocket for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);
                try { existing.Dispose(); }
                catch (Exception ex) { _logger.LogDebug(ex, "{class}[{guid}] {method} Dispose of stale WebSocket failed", nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded)); }
                _webSocket.Value = null;
            }

            _logger.LogDebug("{class}[{guid}] {method} Creating new ClientWebSocket instance for endpoint: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);

            var (ws, uri) = await _wsProvider.CreateConnectionAsync(Endpoint);
            if (ws == null)
            {
                _logger.LogError("{class}[{guid}] {method} Failed to create WebSocket instance. Check connection configuration for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection canceled before setting up observables for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);

                try { ws.Dispose(); }
                catch { }
                return false;
            }

            _logger.LogDebug("{class}[{guid}] {method} Successfully created WebSocket instance for endpoint: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);

            // Increment generation counter for stale-connection detection
            Interlocked.Increment(ref _connectionGeneration);
            _pendingConnectUri = uri;
            _webSocket.Value = ws;

            // Set up observables and logging BEFORE connecting (same pattern as the
            // original HubConnection — handlers must be ready before the connection
            // is established so the server's immediate post-connect messages are received).
            // Order matters: SetupWsObservables creates _wsObservable, which
            // SetupWsLogging subscribes to via WsConnectionLogger.
            SetupWsObservables();
            SetupWsLogging();

            return true;
        }

        private void SetupWsLogging()
        {
            _wsLogger = new WsConnectionLogger(_logger, _wsObservable!, guid: _guid);
        }

        private void SetupWsObservables()
        {
            _wsObservable = new WsConnectionObservable();

            // On connection closed (receive loop exited), create a fresh connection and restart.
            // Uses _cancellationTokenSource (object lifetime) to avoid self-cancellation
            // when Connect() replaces the connection-cycle internalCts.
            //
            // Note: reconnect-phase signaling (Reconnecting/Reconnected) was removed — the
            // synthesized reconnect flow uses Closed → fresh Connect(), and the transport-
            // connected signal fires directly in AttemptConnection via _transportConnected.OnNext.
            var closedSub = _wsObservable.Closed
                .Where(_ => _continueToReconnect.CurrentValue && !_cancellationTokenSource.IsCancellationRequested)
                .Subscribe(ex =>
                {
                    _logger.LogWarning(ex, "{class}[{guid}] {method} Connection closed. Attempting fresh reconnection to: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(SetupWsObservables), Endpoint);
                    // Fire-and-forget: Connect() handles sequential execution via its internal gate.
                    var reconnectTask = Connect(_cancellationTokenSource.Token);
                    _ = reconnectTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.ExecuteSynchronously);
                });

            _wsReconnectSubscription.Disposable = closedSub;
        }

        /// <summary>
        /// Maximum number of consecutive immediate disconnects (server closes connection right after
        /// handshake) before the connection loop gives up. This pattern typically indicates that the
        /// server is rejecting the client (e.g. invalid or revoked authorization token).
        /// </summary>
        private const int MaxConsecutiveRejections = 3;

        /// <summary>
        /// If the server closes the connection within this duration after a successful handshake,
        /// it is counted as an immediate rejection (e.g. authorization failure).
        /// Protected to allow test subclasses to reduce the delay.
        /// </summary>
        protected virtual TimeSpan RejectionThreshold { get; } = TimeSpan.FromSeconds(3);

        private async Task<bool> StartConnectionLoop(CancellationToken cancellationToken)
        {
            _logger.LogDebug("{class}[{guid}] {method} Starting connection loop for endpoint: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(StartConnectionLoop), Endpoint);

            var consecutiveRejections = 0;

            while (!cancellationToken.IsCancellationRequested && _continueToReconnect.CurrentValue)
            {
                if (await AttemptConnection(cancellationToken))
                {
                    // Connection established — verify the server doesn't immediately close it.
                    try
                    {
                        await Task.Delay(RejectionThreshold, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // Survival check: is the receive loop still alive after the rejection window?
                    if (_receiveLoop?.IsAlive == true)
                    {
                        // Connection survived the stability check — genuinely established.
                        // Note: _connectionState is NOT set to Connected here. That happens only
                        // after the application-level handshake succeeds (via SetConnected).
                        consecutiveRejections = 0;
                        return true;
                    }

                    // Server closed the connection immediately after handshake.
                    _connectionState.Value = WsState.Disconnected;
                    consecutiveRejections++;
                    _logger.LogWarning("{class}[{guid}] {method} Connection to {endpoint} was closed by the server immediately after handshake ({count}/{max}). " +
                        "This typically indicates authorization failure (invalid or revoked token).",
                        nameof(ConnectionManager), _guid, nameof(StartConnectionLoop), Endpoint, consecutiveRejections, MaxConsecutiveRejections);

                    if (consecutiveRejections >= MaxConsecutiveRejections)
                    {
                        _logger.LogError("{class}[{guid}] {method} Connection to {endpoint} rejected {count} times consecutively. " +
                            "Stopping reconnection attempts. The server is likely rejecting this client due to an authorization issue. " +
                            "Please check your authorization token and try reconnecting.",
                            nameof(ConnectionManager), _guid, nameof(StartConnectionLoop), Endpoint, consecutiveRejections);
                        _continueToReconnect.Value = false;
                        _connectionState.Value = WsState.Disconnected;
                        _authorizationRejected.OnNext(Unit.Default);
                        return false;
                    }
                }
                else
                {
                    // Connection attempt itself failed (server unreachable, timeout, etc.)
                    consecutiveRejections = 0;
                }

                if (cancellationToken.IsCancellationRequested || !_continueToReconnect.CurrentValue)
                    break;

                await WaitBeforeRetry(cancellationToken);
            }

            _logger.LogDebug("{class}[{guid}] {method} Connection loop terminated for endpoint: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(StartConnectionLoop), Endpoint);
            return false;
        }

        /// <summary>
        /// Attempts to start the WebSocket connection. Must be called from within a _gate-protected section.
        /// Protected virtual to allow test subclasses to simulate server behavior.
        /// </summary>
        protected virtual async Task<bool> AttemptConnection(CancellationToken cancellationToken)
        {
            var ws = _webSocket.CurrentValue;
            var uri = _pendingConnectUri;
            if (ws == null || uri == null)
                return false;

            if (ws.State is WebSocketState.Open or WebSocketState.Connecting)
                return true;

            _logger.LogInformation("{class}[{guid}] {method} Starting connection attempt to: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);

            try
            {
                _connectionState.Value = WsState.Connecting;

                var connectionTask = ws.ConnectAsync(uri, cancellationToken);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                var completedTask = await Task.WhenAny(connectionTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _logger.LogWarning("{class}[{guid}] {method} Connection attempt timed out after 30 seconds for endpoint: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);
                    _ = connectionTask.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.ExecuteSynchronously);
                    return false;
                }

                if (connectionTask.IsCompletedSuccessfully)
                {
                    _logger.LogInformation("{class}[{guid}] {method} Connection established successfully to: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);

                    // Start the receive loop — this feeds the WsConnectionObservable
                    // and handles all incoming messages.
                    StartReceiveLoop(ws, cancellationToken);

                    _transportConnected.OnNext(Unit.Default);
                    return true;
                }
                else
                {
                    _logger.LogWarning("{class}[{guid}] {method} Connection attempt failed for endpoint: {endpoint}. Exception: {exception}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint, connectionTask.Exception?.Message);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection attempt canceled for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);
            }
            catch (WebSocketException ex)
            {
                _logger.LogError("{class}[{guid}] {method} WebSocketException during connection attempt to endpoint: {endpoint}. Error: {error}",
                    nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{class}[{guid}] {method} Connection attempt failed for endpoint: {endpoint}. Error: {error}",
                    nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint, ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Starts the background receive loop for the connected WebSocket.
        /// </summary>
        private void StartReceiveLoop(ClientWebSocket ws, CancellationToken cancellationToken)
        {
            _receiveLoop?.Dispose();
            _receiveLoop = new WsReceiveLoop(
                ws,
                _dispatcher,
                _wsProvider.JsonSerializerOptions,
                _wsObservable!,
                _logger);

            _receiveLoopTask = _receiveLoop.RunAsync(cancellationToken);
        }

        protected virtual async Task WaitBeforeRetry(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            _logger.LogTrace("{class}[{guid}] {method} Waiting 5 seconds before retry for endpoint: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(WaitBeforeRetry), Endpoint);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
