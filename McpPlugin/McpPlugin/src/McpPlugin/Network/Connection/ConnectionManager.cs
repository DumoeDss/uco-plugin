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
using com.IvanMurzak.McpPlugin.Common;
using Microsoft.Extensions.Logging;
using R3;
using WsState = com.IvanMurzak.McpPlugin.ConnectionState;
using Version = com.IvanMurzak.McpPlugin.Common.Version;

namespace com.IvanMurzak.McpPlugin
{
    /// <summary>
    /// Manages the WebSocket connection lifecycle, reconnect state machine,
    /// and RPC dispatch. Replaces SignalR <c>HubConnection</c> with raw
    /// <see cref="ClientWebSocket"/> + JSON envelope.
    ///
    /// All 4 reconnect layers are preserved from the original SignalR implementation:
    /// 1. Two semaphores (<see cref="_gate"/> + <see cref="_ongoingConnectionGate"/>) + linked CTS
    /// 2. Rejection-detection window (3s survival check, 3 consecutive → OnAuthorizationRejected)
    /// 3. Three-way event pipeline (OnTransportConnected / OnAuthorizationRejected / ConnectionState)
    /// 4. Unity domain-reload fire-and-forget teardown (DisconnectImmediate → Task.Run(ws.Dispose))
    /// </summary>
    public partial class ConnectionManager : IConnectionManager, IAsyncDisposable
    {
        protected readonly string _guid = Guid.NewGuid().ToString();
        protected readonly ILogger _logger;
        protected readonly Version _apiVersion;
        protected readonly string _endpoint;
        protected readonly IWebSocketConnectionProvider _wsProvider;

        // ── Layer 1: semaphores + CTS ─────────────────────────────────────
        protected readonly SemaphoreSlim _gate = new(1, 1);
        protected readonly SemaphoreSlim _ongoingConnectionGate = new(1, 1);

        // ── Reactive state ────────────────────────────────────────────────
        protected readonly ReactiveProperty<bool> _continueToReconnect = new(false);
        protected readonly ReactiveProperty<ClientWebSocket?> _webSocket = new();
        protected readonly ReactiveProperty<ConnectionState> _connectionState = new(WsState.Disconnected);
        private readonly Subject<Unit> _authorizationRejected = new();
        private readonly Subject<Unit> _transportConnected = new();
        protected readonly CompositeDisposable _disposables = new();
        protected readonly CancellationTokenSource _cancellationTokenSource;

        private readonly ThreadSafeBool _isDisposed = new(false);
        private readonly SerialDisposable _wsReconnectSubscription = new();
        private readonly ReadOnlyReactiveProperty<ConnectionState> _connectionStateReadOnly;
        private readonly ReadOnlyReactiveProperty<bool> _keepConnectedReadOnly;

        // ── WebSocket transport (new) ─────────────────────────────────────
        private WsConnectionLogger? _wsLogger;
        private WsConnectionObservable? _wsObservable;
        private readonly WsRpcDispatcher _dispatcher;
        private WsReceiveLoop? _receiveLoop;
        private volatile Task? _receiveLoopTask;

        // ── Stale-connection detection (replaces HubConnection identity) ──
        private int _connectionGeneration;

        private CancellationTokenSource? internalCts;
        private volatile Task<bool>? _ongoingConnectionTask;

        // ── Public properties ─────────────────────────────────────────────

        public ReadOnlyReactiveProperty<ConnectionState> ConnectionState => _connectionStateReadOnly;
        public ReadOnlyReactiveProperty<bool> KeepConnected => _keepConnectedReadOnly;
        public Observable<Unit> OnAuthorizationRejected => _authorizationRejected;
        public Observable<Unit> OnTransportConnected => _transportConnected;
        public string Endpoint => _endpoint;
        public int ConnectionGeneration => _connectionGeneration;
        public CancellationToken ConnectionCancellationToken => internalCts?.Token ?? CancellationToken.None;

        public void SetConnected()
        {
            if (_isDisposed.Value)
                return;
            _connectionState.Value = WsState.Connected;
        }

        public void NotifyAuthorizationRejected()
        {
            if (_isDisposed.Value)
                return;
            _authorizationRejected.OnNext(Unit.Default);
        }

        // ── Constructor ───────────────────────────────────────────────────

        public ConnectionManager(ILogger logger, Version apiVersion, string endpoint, IWebSocketConnectionProvider wsProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogTrace("{class}[{guid}] Ctor.", nameof(ConnectionManager), _guid);

            _apiVersion = apiVersion ?? throw new ArgumentNullException(nameof(apiVersion));
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _wsProvider = wsProvider ?? throw new ArgumentNullException(nameof(wsProvider));
            _cancellationTokenSource = _disposables.ToCancellationTokenSource();

            _connectionStateReadOnly = _connectionState.ToReadOnlyReactiveProperty();
            _keepConnectedReadOnly = _continueToReconnect.ToReadOnlyReactiveProperty();

            // Create the RPC dispatcher with the provider's JsonSerializerOptions
            _dispatcher = new WsRpcDispatcher(_wsProvider.JsonSerializerOptions);

            // Wire the dispatcher's send function to the current WebSocket.
            // This closure captures `this`, so _webSocket.CurrentValue always gets
            // the live connection — the SendFunc never needs manual updating.
            _dispatcher.SendFunc = async (bytes, ct) =>
            {
                var ws = _webSocket.CurrentValue;
                if (ws == null)
                    throw new InvalidOperationException("WebSocket is not connected.");
                await ws.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    ct).ConfigureAwait(false);
            };

            // Reconnection is handled by SetupWsObservables (Closed event).
            // State changes are driven by the connection flow + WsConnectionObservable callbacks.
        }

        // ── Envelope-based RPC (delegates to WsRpcDispatcher) ─────────────

        public async Task InvokeAsync<TInput>(string methodName, TInput input, CancellationToken cancellationToken = default, int timeoutMs = 10_000)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(InvokeAsync));
                return;
            }

            if (!await EnsureConnection(cancellationToken))
                return;

            await _dispatcher.InvokeAsync<TInput>(methodName, input, cancellationToken, timeoutMs);
        }

        public async Task<TResult> InvokeAsync<TInput, TResult>(string methodName, TInput input, CancellationToken cancellationToken = default, int timeoutMs = 10_000)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(InvokeAsync));
                return default!;
            }

            if (!await EnsureConnection(cancellationToken))
                return default!;

            return await _dispatcher.InvokeAsync<TInput, TResult>(methodName, input, cancellationToken, timeoutMs);
        }

        public Task<TResult> InvokeAsync<TResult>(string methodName, CancellationToken cancellationToken = default, int timeoutMs = 10_000)
            => InvokeAsync<object?, TResult>(methodName, null, cancellationToken, timeoutMs);

        // ── Handler registration (replaces hubConnection.On<T>) ───────────

        public IDisposable RegisterHandler<TParam, TResult>(string method, Func<TParam?, Task<TResult?>> handler)
            => _dispatcher.RegisterHandler<TParam, TResult>(method, handler);

        public IDisposable RegisterNotification<TParam>(string method, Func<TParam?, Task> handler)
            => _dispatcher.RegisterNotification<TParam>(method, handler);

        // ── Dispose ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (!_isDisposed.TrySetTrue())
                return;

            GC.SuppressFinalize(this);
            _logger.LogDebug("{class}[{guid}] {method}.",
                nameof(ConnectionManager), _guid, nameof(Dispose));

            DisposeCommonSync();

            var acquiredGate = _gate.Wait(TimeSpan.FromSeconds(5));
            try
            {
                if (!acquiredGate)
                {
                    _logger.LogWarning("{class}[{guid}] {method} Could not acquire gate within timeout during Dispose. Proceeding with cleanup anyway.",
                        nameof(ConnectionManager), _guid, nameof(Dispose));
                }
                if (!_webSocket.IsDisposed)
                {
                    try
                    {
                        // Fire-and-forget dispose of the WebSocket (non-blocking)
                        var ws = _webSocket.CurrentValue;
                        if (ws != null)
                        {
                            _ = Task.Run(() => { try { ws.Dispose(); } catch { } });
                        }
                        _webSocket.Value = null;
                        _webSocket.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("{class}[{guid}] {method} Error during disposal: {message}",
                            nameof(ConnectionManager), _guid, nameof(Dispose), ex.Message);
                    }
                }
            }
            finally
            {
                if (acquiredGate)
                    _gate.Release();

                try { _gate.Dispose(); } catch (ObjectDisposedException) { }
                try { _ongoingConnectionGate.Dispose(); } catch (ObjectDisposedException) { }

                _logger.LogDebug("{class}[{guid}] {method} completed.",
                    nameof(ConnectionManager), _guid, nameof(Dispose));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_isDisposed.TrySetTrue())
                return;

            GC.SuppressFinalize(this);
            _logger.LogDebug("{class}[{guid}] {method}.",
                nameof(ConnectionManager), _guid, nameof(DisposeAsync));

            DisposeCommonSync();

            var isGateAcquired = await _gate.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var ws = _webSocket.CurrentValue;
                if (ws != null)
                {
                    try
                    {
                        // Gracefully close the WebSocket
                        if (ws.State == WebSocketState.Open)
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                                .ConfigureAwait(false);
                        ws.Dispose();

                        if (!_webSocket.IsDisposed)
                            _webSocket.Value = null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("{class}[{guid}] {method} Error during async disposal: {message}\n{stackTrace}",
                            nameof(ConnectionManager), _guid, nameof(DisposeAsync), ex.Message, ex.StackTrace);
                    }
                }

                if (!_webSocket.IsDisposed)
                    _webSocket.Dispose();
            }
            finally
            {
                if (isGateAcquired)
                    _gate.Release();

                try { _gate.Dispose(); } catch (ObjectDisposedException) { }
                try { _ongoingConnectionGate.Dispose(); } catch (ObjectDisposedException) { }

                _logger.LogDebug("{class}[{guid}] {method} completed.",
                    nameof(ConnectionManager), _guid, nameof(DisposeAsync));
            }
        }

        private void DisposeCommonSync()
        {
            CancelInternalToken(dispose: true);
            _disposables.Dispose();

            if (!_continueToReconnect.IsDisposed)
                _continueToReconnect.Value = false;

            _wsLogger?.Dispose();
            _wsObservable?.Dispose();
            _receiveLoop?.Dispose();
            _dispatcher.Dispose();

            _wsLogger = null;
            _wsObservable = null;

            _wsReconnectSubscription.Dispose();
            _connectionState.Dispose();
            _continueToReconnect.Dispose();
            _connectionStateReadOnly.Dispose();
            _keepConnectedReadOnly.Dispose();
        }

        void CancelInternalToken(bool dispose = false)
        {
            if (internalCts == null)
                return;

            if (!internalCts.IsCancellationRequested)
                internalCts.Cancel();

            if (dispose)
            {
                internalCts.Dispose();
                internalCts = null;
            }
        }
    }
}
