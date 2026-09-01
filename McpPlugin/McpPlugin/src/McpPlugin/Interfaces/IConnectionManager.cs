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
using R3;

namespace com.IvanMurzak.McpPlugin
{
    public interface IConnectionManager : IConnection, IDisposable
    {
        string Endpoint { get; }

        /// <summary>
        /// Monotonically increasing generation counter, incremented each time a new
        /// WebSocket connection is created. Used by <see cref="BaseHubConnector"/> to
        /// detect stale event messages from a previous connection cycle.
        /// </summary>
        int ConnectionGeneration { get; }

        CancellationToken ConnectionCancellationToken { get; }

        /// <summary>
        /// Sets the public connection state to Connected.
        /// Called by the application layer after a successful handshake.
        /// </summary>
        void SetConnected();

        /// <summary>
        /// Fires the <see cref="IConnection.OnAuthorizationRejected"/> event.
        /// Called when the server explicitly rejects the connection due to authorization failure.
        /// </summary>
        void NotifyAuthorizationRejected();

        /// <summary>
        /// Fires when the WebSocket transport connection is established
        /// (<c>ConnectAsync</c> succeeded + receive loop started).
        /// This is distinct from application-level Connected state (which requires a successful handshake).
        /// </summary>
        Observable<Unit> OnTransportConnected { get; }

        // ── Envelope-based RPC (replaces old HubConnection.InvokeAsync) ────

        Task InvokeAsync<TInput>(string methodName, TInput input, CancellationToken cancellationToken = default, int timeoutMs = 10_000);
        Task<TResult> InvokeAsync<TInput, TResult>(string methodName, TInput input, CancellationToken cancellationToken = default, int timeoutMs = 10_000);
        Task<TResult> InvokeAsync<TResult>(string methodName, CancellationToken cancellationToken = default, int timeoutMs = 10_000);

        // ── Handler registration (replaces hubConnection.On&lt;T&gt;) ───────

        /// <summary>
        /// Registers a handler for an incoming RPC request method.
        /// </summary>
        IDisposable RegisterHandler<TParam, TResult>(string method, Func<TParam?, Task<TResult?>> handler);

        /// <summary>
        /// Registers a request handler that receives the active connection
        /// cancellation token. The original overload remains compatible.
        /// </summary>
        IDisposable RegisterHandler<TParam, TResult>(
            string method,
            Func<TParam?, CancellationToken, Task<TResult?>> handler);

        /// <summary>
        /// Registers a handler for an incoming notification (no response expected).
        /// </summary>
        IDisposable RegisterNotification<TParam>(string method, Func<TParam?, Task> handler);

        /// <summary>Registers a notification handler with the connection token.</summary>
        IDisposable RegisterNotification<TParam>(
            string method,
            Func<TParam?, CancellationToken, Task> handler);
    }
}
