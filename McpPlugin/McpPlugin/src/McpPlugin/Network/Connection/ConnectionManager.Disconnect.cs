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
using WsState = com.IvanMurzak.McpPlugin.ConnectionState;

namespace com.IvanMurzak.McpPlugin
{
    public partial class ConnectionManager : IConnectionManager, IAsyncDisposable
    {
        public async Task Disconnect(CancellationToken cancellationToken = default)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(Disconnect));
                return;
            }

            _logger.LogDebug("{class}[{guid}] {method} called.",
                nameof(ConnectionManager), _guid, nameof(Disconnect));

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{class}[{guid}] {method} canceled before it gets started.",
                    nameof(ConnectionManager), _guid, nameof(Disconnect));
                return;
            }

            Interlocked.Increment(ref _disconnectSequence);
            CancelInternalToken(dispose: false);
            _continueToReconnect.Value = false;

            try
            {
                _logger.LogDebug("{class}[{guid}] {method} acquiring gate.",
                    nameof(ConnectionManager), _guid, nameof(Disconnect));

                await _gate.WaitAsync(cancellationToken);

                _logger.LogDebug("{class}[{guid}] {method} acquired gate.",
                    nameof(ConnectionManager), _guid, nameof(Disconnect));
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("{class}[{guid}] {method} canceled while waiting for gate for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(Disconnect), Endpoint);
                return;
            }

            try
            {
                await DisconnectInternal(cancellationToken);
            }
            finally
            {
                _logger.LogDebug("{class}[{guid}] {method} releasing gate.",
                    nameof(ConnectionManager), _guid, nameof(Disconnect));
                _gate.Release();
            }
        }

        /// <summary>
        /// Immediately disconnects without waiting for async cleanup.
        /// Use this during assembly reload or other critical shutdown scenarios.
        /// Fully synchronous — no Tasks are spawned, safe for domain reload.
        /// </summary>
        public void DisconnectImmediate()
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(DisconnectImmediate));
                return;
            }

            Interlocked.Increment(ref _disconnectSequence);
            CancelInternalToken(dispose: false);
            _continueToReconnect.Value = false;

            var acquiredGate = _gate.Wait(TimeSpan.Zero);
            try
            {
                _logger.LogDebug("{class}[{guid}] {method} Gate acquired: {acquired}",
                    nameof(ConnectionManager), _guid, nameof(DisconnectImmediate), acquiredGate);

                DisconnectImmediateCore();
            }
            finally
            {
                if (acquiredGate)
                {
                    _logger.LogDebug("{class}[{guid}] {method} Releasing gate.",
                        nameof(ConnectionManager), _guid, nameof(DisconnectImmediate));
                    _gate.Release();
                }
                else
                {
                    _logger.LogWarning("{class}[{guid}] {method} Could not acquire gate within timeout. Proceeding without gate protection.",
                        nameof(ConnectionManager), _guid, nameof(DisconnectImmediate));
                }
            }
        }

        /// <summary>
        /// Synchronous core of the immediate disconnect path.
        /// Uses non-blocking gate attempts (TimeSpan.Zero) so it never deadlocks
        /// when called from a thread that has a SynchronizationContext (e.g. Unity main thread).
        /// </summary>
        private void DisconnectImmediateCore()
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(DisconnectImmediateCore));
                return;
            }

            _logger.LogDebug("{class}[{guid}] {method}.",
                nameof(ConnectionManager), _guid, nameof(DisconnectImmediateCore));

            var acquiredOngoingGate = _ongoingConnectionGate.Wait(TimeSpan.Zero);
            if (acquiredOngoingGate)
            {
                _ongoingConnectionTask = null;
                _ongoingConnectionGate.Release();
            }
            else
            {
                _logger.LogWarning("{class}[{guid}] {method} Could not acquire ongoingConnectionGate (held by another thread). " +
                    "Connect's finally block will clear _ongoingConnectionTask after cancellation propagates.",
                    nameof(ConnectionManager), _guid, nameof(DisconnectImmediateCore));
            }

            var tempWs = ClearConnectionState();
            if (tempWs == null)
                return;

            _logger.LogDebug("{class}[{guid}] {method} Performing immediate disconnect without waiting for cleanup.",
                nameof(ConnectionManager), _guid, nameof(DisconnectImmediateCore));

            // Fire-and-forget: let the OS/runtime clean up the socket. Exceptions are
            // intentionally unobserved — this is an emergency path (domain reload) where
            // spawning async work or blocking on results is unsafe.
            _ = Task.Run(() => { try { tempWs.Dispose(); } catch { } });
        }

        private async Task DisconnectInternal(CancellationToken cancellationToken)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(DisconnectInternal));
                return;
            }

            _logger.LogDebug("{class}[{guid}] {method}.",
                 nameof(ConnectionManager), _guid, nameof(DisconnectInternal));

            await _ongoingConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            _ongoingConnectionTask = null;
            _ongoingConnectionGate.Release();

            var tempWs = ClearConnectionState();
            if (tempWs == null)
                return;

            await DisconnectGracefulAsync(tempWs, cancellationToken);
        }

        /// <summary>
        /// Clears all WebSocket connection state fields and returns the active
        /// ClientWebSocket (or null). The caller is responsible for disposing the
        /// returned connection in the appropriate manner (sync fire-and-forget for
        /// immediate shutdown; async for graceful disconnect).
        /// </summary>
        private ClientWebSocket? ClearConnectionState()
        {
            // Reject all pending RPC requests — the connection is going away
            _dispatcher.RejectAllPending(new InvalidOperationException("Connection closed."));
            _dispatcher.ClearDeferred();
            _dispatcher.CancelAllInFlight();

            _wsLogger?.Dispose();
            _wsObservable?.Dispose();
            _receiveLoop?.Dispose();

            _wsLogger = null;
            _wsObservable = null;
            _receiveLoop = null;
            _wsReconnectSubscription.Disposable = null;

            // Update state immediately to prevent reconnection attempts
            _connectionState.Value = WsState.Disconnected;

            var tempWs = _webSocket.CurrentValue;
            _webSocket.Value = null;
            _pendingConnectUri = null;

            return tempWs;
        }

        private async Task DisconnectGracefulAsync(ClientWebSocket ws, CancellationToken cancellationToken)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cancellationToken)
                        .ConfigureAwait(false);
                ws.Dispose();
                _logger.LogDebug("{class}[{guid}] {method} WebSocket closed and disposed successfully.",
                    nameof(ConnectionManager), _guid, nameof(DisconnectGracefulAsync));
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning("{class}[{guid}] {method} WebSocket close was canceled: {message}",
                    nameof(ConnectionManager), _guid, nameof(DisconnectGracefulAsync), ex.Message);
            }
            catch (WebSocketException ex)
            {
                _logger.LogError("{class}[{guid}] {method} WebSocketException while closing WebSocket: {message}\n{stackTrace}",
                    nameof(ConnectionManager), _guid, nameof(DisconnectGracefulAsync), ex.Message, ex.StackTrace);
            }
            catch (Exception ex)
            {
                _logger.LogCritical("{class}[{guid}] {method} Unexpected error while closing WebSocket: {message}\n{stackTrace}",
                    nameof(ConnectionManager), _guid, nameof(DisconnectGracefulAsync), ex.Message, ex.StackTrace);
                throw;
            }
        }
    }
}
