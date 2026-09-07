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
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.McpPlugin
{
    /// <summary>
    /// Background task that loops on <see cref="ClientWebSocket.ReceiveAsync"/>.
    /// Accumulates multi-frame messages, parses them via <see cref="WsEnvelope.TryParseMessage"/>,
    /// and dispatches to <see cref="WsRpcDispatcher"/>.
    ///
    /// Heartbeat: uses a receive timeout (60s default, 2x the 30s SignalR KeepAliveInterval).
    /// If no data (text/binary frame) arrives within the timeout, the connection is treated as
    /// dead and the loop exits. netstandard2.1 does not expose
    /// <c>ClientWebSocketOptions.KeepAliveInterval</c> and <c>SendAsync</c> cannot emit WS Ping
    /// control frames, so we rely on the server's WS-level pings (auto-ponged by the runtime)
    /// for TCP keep-alive and on the receive-timeout for dead-connection detection.
    /// </summary>
    public sealed class WsReceiveLoop : IDisposable
    {
        private readonly ClientWebSocket _webSocket;
        private readonly WsRpcDispatcher _dispatcher;
        private readonly JsonSerializerOptions _options;
        private readonly WsConnectionObservable _observable;
        private readonly int _heartbeatTimeoutSeconds;
        private readonly ILogger _logger;
        private CancellationTokenSource? _heartbeatCts;
        private volatile bool _isAlive;
        private int _disposed;

        /// <summary>
        /// Whether the receive loop is still running.
        /// Checked by <see cref="ConnectionManager.Connect"/> for the rejection-detection
        /// window (3s survival check).
        /// </summary>
        public bool IsAlive => _isAlive;

        public WsReceiveLoop(
            ClientWebSocket webSocket,
            WsRpcDispatcher dispatcher,
            JsonSerializerOptions options,
            WsConnectionObservable observable,
            Microsoft.Extensions.Logging.ILogger logger,
            int heartbeatTimeoutSeconds = 60)
        {
            _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _observable = observable ?? throw new ArgumentNullException(nameof(observable));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _heartbeatTimeoutSeconds = heartbeatTimeoutSeconds;
        }

        /// <summary>
        /// Runs the receive loop until cancellation, close frame, or error.
        /// This method completes when the loop exits.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _isAlive = true;

            // Buffer for individual ReceiveAsync calls (4 KB, matching SignalR's MaximumReceiveMessageSize)
            var buffer = new byte[4096];
            using var messageBuffer = new MemoryStream();

            // Heartbeat: linked CTS that fires after _heartbeatTimeoutSeconds of inactivity.
            // CancelAfter is called after each successful receive to reset the timer.
            _heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _heartbeatCts.CancelAfter(TimeSpan.FromSeconds(_heartbeatTimeoutSeconds));

            _logger.LogDebug("WsReceiveLoop started, heartbeat timeout: {seconds}s", _heartbeatTimeoutSeconds);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    WebSocketReceiveResult result;

                    try
                    {
                        result = await _webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            _heartbeatCts.Token).ConfigureAwait(false);

                        // Reset heartbeat timer — we received data
                        _heartbeatCts.CancelAfter(TimeSpan.FromSeconds(_heartbeatTimeoutSeconds));
                    }
                    catch (OperationCanceledException) when (
                        _heartbeatCts.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested)
                    {
                        // Heartbeat timeout — no data within timeout. Dead connection.
                        _logger.LogWarning("WsReceiveLoop: heartbeat timeout ({seconds}s without data), treating connection as dead.",
                            _heartbeatTimeoutSeconds);
                        _observable.OnClosed(new TimeoutException(
                            $"No data received within {_heartbeatTimeoutSeconds}s."));
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogDebug("WsReceiveLoop: received close frame from server.");
                        _observable.OnClosed(null);
                        break;
                    }

                    // Accumulate message bytes
                    messageBuffer.Write(buffer, 0, result.Count);

                    if (!result.EndOfMessage)
                        continue;

                    // Complete message — parse and dispatch
                    var messageBytes = messageBuffer.ToArray();
                    messageBuffer.SetLength(0);

                    if (WsEnvelope.TryParseMessage(messageBytes, _options, out var parsed))
                    {
                        try
                        {
                            var dispatch = _dispatcher.HandleIncomingAsync(parsed, cancellationToken);
                            if (parsed?.Type == WsMessageType.Request
                                && (parsed.Method == "RunCallTool" || parsed.Method == "RunSystemTool"))
                            {
                                ObserveConcurrentToolDispatch(dispatch);
                            }
                            else
                            {
                                await dispatch.ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "WsReceiveLoop: error dispatching message.");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("WsReceiveLoop: failed to parse message ({bytes} bytes).", messageBytes.Length);
                    }
                }
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WsReceiveLoop: WebSocket error. Connection dropped.");
                _observable.OnClosed(ex);
            }
            catch (OperationCanceledException)
            {
                // External cancellation — graceful exit, no signal needed
                _logger.LogDebug("WsReceiveLoop: cancelled, exiting gracefully.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WsReceiveLoop: unexpected error. Connection dropped.");
                _observable.OnClosed(ex);
            }
            finally
            {
                _isAlive = false;
            }
        }

        private void ObserveConcurrentToolDispatch(Task dispatch)
        {
            _ = dispatch.ContinueWith(
                task => _logger.LogError(task.Exception,
                    "WsReceiveLoop: concurrent tool dispatch failed."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _heartbeatCts?.Dispose(); } catch (ObjectDisposedException) { }
        }
    }
}
