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
using R3;

namespace com.IvanMurzak.McpPlugin
{
    /// <summary>
    /// Replaces <c>HubConnectionObservable</c>. Exposes an R3 observable for
    /// the WebSocket connection-closed event, fed by callbacks from
    /// <see cref="WsReceiveLoop"/> and <see cref="ConnectionManager"/>.
    /// </summary>
    /// <remarks>
    /// Reconnect-phase notifications (reconnecting vs reconnected) do NOT flow
    /// through this observable. They are signaled via the
    /// <see cref="ConnectionManager"/>'s <c>ConnectionState</c> reactive property
    /// (Disconnected → Connecting → Connected) and the <c>OnTransportConnected</c> /
    /// <c>OnAuthorizationRejected</c> subjects. The closed event drives the fresh-
    /// <c>Connect()</c> reconnection path in <c>SetupWsObservables</c>.
    /// </remarks>
    public sealed class WsConnectionObservable : IDisposable
    {
        readonly Subject<Exception?> _closedSubject = new();
        int _disposed;

        public Observable<Exception?> Closed => _closedSubject;

        // ── Callbacks (called by WsReceiveLoop / ConnectionManager) ───────

        /// <summary>
        /// Signals that the connection was closed (receive loop exited).
        /// Triggers the ConnectionManager's fresh-Connect reconnection path.
        /// </summary>
        public void OnClosed(Exception? ex)
        {
            if (_disposed == 0) _closedSubject.OnNext(ex);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _closedSubject.Dispose();
        }
    }
}
