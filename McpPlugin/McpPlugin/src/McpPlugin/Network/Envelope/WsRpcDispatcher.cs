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
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;

namespace com.IvanMurzak.McpPlugin
{
    // ── Internal handler entries ──────────────────────────────────────────

    internal sealed class HandlerEntry
    {
        public Func<JsonElement?, CancellationToken, Task<(JsonElement? result, WsError? error)>> Invoker { get; set; } = null!;
    }

    internal sealed class NotificationEntry
    {
        public Func<JsonElement?, CancellationToken, Task> Invoker { get; set; } = null!;
    }

    internal sealed class Registration : IDisposable
    {
        private Action? _onDispose;
        private int _disposed; // 0 = not disposed, 1 = disposed

        public Registration(Action onDispose) => _onDispose = onDispose;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _onDispose?.Invoke();
        }
    }

    /// <summary>
    /// Replaces <c>HubConnection.On&lt;T&gt;</c> + <c>HubConnection.InvokeAsync&lt;TResult&gt;</c>.
    /// Handler registry for incoming requests/notifications + pending-request tracker
    /// for outgoing RPC + dual-path completion support.
    /// </summary>
    public sealed class WsRpcDispatcher : IDisposable
    {
        private readonly JsonSerializerOptions _options;
        private readonly ConcurrentDictionary<string, HandlerEntry> _handlers = new();
        private readonly ConcurrentDictionary<string, NotificationEntry> _notificationHandlers = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _deferred = new();
        private int _nextId;
        private int _disposed;

        /// <summary>
        /// Send callback set by <see cref="ConnectionManager"/> when a WebSocket connection
        /// is established. When null (disconnected), <see cref="InvokeAsync{TInput, TResult}"/>
        /// throws <see cref="InvalidOperationException"/>.
        /// </summary>
        internal Func<byte[], CancellationToken, Task>? SendFunc { get; set; }

        public WsRpcDispatcher(JsonSerializerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        // ── Handler registration (incoming: server → plugin) ──────────────

        /// <summary>
        /// Registers a handler for an incoming request method.
        /// The dispatcher will invoke the handler when a <c>{id, method, params}</c> message
        /// arrives, and send back <c>{id, result}</c> or <c>{id, error}</c>.
        /// Replaces <c>hubConnection.On&lt;TParam, TResult&gt;(method, handler)</c>.
        /// </summary>
        public IDisposable RegisterHandler<TParam, TResult>(string method, Func<TParam?, Task<TResult?>> handler)
        {
            if (string.IsNullOrEmpty(method))
                throw new ArgumentNullException(nameof(method));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RegisterHandler<TParam, TResult>(method, (param, _) => handler(param));
        }

        /// <summary>
        /// Registers a request handler that receives the cancellation token of
        /// the active WebSocket connection/request. The one-argument overload
        /// remains available for existing handlers.
        /// </summary>
        public IDisposable RegisterHandler<TParam, TResult>(
            string method,
            Func<TParam?, CancellationToken, Task<TResult?>> handler)
        {
            if (string.IsNullOrEmpty(method))
                throw new ArgumentNullException(nameof(method));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var entry = new HandlerEntry
            {
                Invoker = async (paramsElement, requestCancellationToken) =>
                {
                    try
                    {
                        // System.Text.Json maps an explicit null to the
                        // default value for non-nullable value properties.
                        // Check the raw tool-call payload first so a malformed
                        // known control member cannot become version 0 (and be
                        // reported as an unsupported version) on this typed
                        // dispatch path.
                        if (typeof(TParam) == typeof(RequestCallTool))
                            ValidateRawToolCallControl(paramsElement);

                        TParam? param = default;
                        if (paramsElement.HasValue && paramsElement.Value.ValueKind != JsonValueKind.Null)
                            param = paramsElement.Value.Deserialize<TParam>(_options);

                        var result = await handler(param, requestCancellationToken).ConfigureAwait(false);
                        if (result == null)
                            return ((JsonElement?)null, (WsError?)null);
                        return (JsonSerializer.SerializeToElement(result, _options), (WsError?)null);
                    }
                    catch (JsonException) when (typeof(TParam) == typeof(RequestCallTool))
                    {
                        return ((JsonElement?)null, CreateStructuredError(
                            WsErrorCodes.InvalidParams,
                            CreateMalformedToolCallError(paramsElement)));
                    }
                    catch (ToolCallControlException ex)
                    {
                        var error = ex.ToError();
                        AttachToolCallIdentity(error, paramsElement);
                        return ((JsonElement?)null, CreateStructuredError(
                            WsErrorCodes.InvalidParams,
                            error));
                    }
                    catch (Exception ex)
                    {
                        return ((JsonElement?)null, new WsError
                        {
                            Code = WsErrorCodes.InternalError,
                            Message = ex.Message
                        });
                    }
                }
            };

            _handlers[method] = entry;
            return new Registration(() => _handlers.TryRemove(method, out _));
        }

        /// <summary>
        /// Registers a handler for an incoming notification (no response expected).
        /// Replaces <c>hubConnection.On&lt;TParam&gt;(method, handler)</c> for fire-and-forget callbacks.
        /// </summary>
        public IDisposable RegisterNotification<TParam>(string method, Func<TParam?, Task> handler)
        {
            if (string.IsNullOrEmpty(method))
                throw new ArgumentNullException(nameof(method));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            return RegisterNotification<TParam>(method, (param, _) => handler(param));
        }

        /// <summary>Registers a notification handler with its connection token.</summary>
        public IDisposable RegisterNotification<TParam>(
            string method,
            Func<TParam?, CancellationToken, Task> handler)
        {
            if (string.IsNullOrEmpty(method))
                throw new ArgumentNullException(nameof(method));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            var entry = new NotificationEntry
            {
                Invoker = async (paramsElement, requestCancellationToken) =>
                {
                    TParam? param = default;
                    if (paramsElement.HasValue && paramsElement.Value.ValueKind != JsonValueKind.Null)
                        param = paramsElement.Value.Deserialize<TParam>(_options);

                    await handler(param, requestCancellationToken).ConfigureAwait(false);
                }
            };

            _notificationHandlers[method] = entry;
            return new Registration(() => _notificationHandlers.TryRemove(method, out _));
        }

        /// <summary>
        /// Removes all registered handlers and notification handlers.
        /// Called by <see cref="Dispose"/> during teardown.
        /// Handler lifecycle during a connection cycle is managed via disposables in
        /// <see cref="BaseHubConnector.OnConnectionCycleStarted"/> (each registration's
        /// Dispose removes the handler from the dictionary). Pending and deferred entries
        /// are NOT cleared here (they're cleared via <see cref="RejectAllPending"/> and
        /// <see cref="ClearDeferred"/>).
        /// </summary>
        public void ClearAll()
        {
            _handlers.Clear();
            _notificationHandlers.Clear();
        }

        // ── Outgoing RPC (plugin → server) ────────────────────────────────

        /// <summary>
        /// Sends an outgoing RPC request and awaits the response.
        /// Generates a unique id, registers a TCS, sends <c>{id, method, params}</c>,
        /// and resolves when <c>{id, result}</c> or <c>{id, error}</c> arrives.
        /// </summary>
        public async Task<TResult> InvokeAsync<TInput, TResult>(string method, TInput input, CancellationToken cancellationToken = default, int timeoutMs = 10_000)
        {
            var id = Interlocked.Increment(ref _nextId).ToString();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                // Build request
                JsonElement? paramsElement = null;
                if (input != null)
                    paramsElement = JsonSerializer.SerializeToElement(input, _options);

                var request = new WsRequest
                {
                    Id = id,
                    Method = method,
                    Params = paramsElement
                };

                var bytes = WsEnvelope.SerializeRequest(request, _options);

                var sendFunc = SendFunc;
                if (sendFunc == null)
                    throw new InvalidOperationException(
                        "WebSocket is not connected. Cannot invoke method '" + method + "'.");

                await sendFunc(bytes, cancellationToken).ConfigureAwait(false);

                // Await response with per-call timeout (default 10s; 5min for tool calls).
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                using var registration = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                var resultElement = await tcs.Task.ConfigureAwait(false);
                return JsonSerializer.Deserialize<TResult>(resultElement.GetRawText(), _options)!
                    ?? default!;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// Sends an outgoing RPC request without a typed result (fire-and-forget with ack).
        /// </summary>
        public async Task InvokeAsync<TInput>(string method, TInput input, CancellationToken cancellationToken = default, int timeoutMs = 10_000)
        {
            var id = Interlocked.Increment(ref _nextId).ToString();
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                JsonElement? paramsElement = null;
                if (input != null)
                    paramsElement = JsonSerializer.SerializeToElement(input, _options);

                var request = new WsRequest
                {
                    Id = id,
                    Method = method,
                    Params = paramsElement
                };

                var bytes = WsEnvelope.SerializeRequest(request, _options);

                var sendFunc = SendFunc;
                if (sendFunc == null)
                    throw new InvalidOperationException(
                        "WebSocket is not connected. Cannot invoke method '" + method + "'.");

                await sendFunc(bytes, cancellationToken).ConfigureAwait(false);

                // Await response with per-call timeout (default 10s; 5min for tool calls).
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

                using var registration = timeoutCts.Token.Register(() =>
                    tcs.TrySetCanceled(timeoutCts.Token));

                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        /// <summary>
        /// Sends an outgoing RPC request with no params, awaits a typed result.
        /// </summary>
        public Task<TResult> InvokeAsync<TResult>(string method, CancellationToken cancellationToken = default, int timeoutMs = 10_000)
            => InvokeAsync<object?, TResult>(method, null, cancellationToken, timeoutMs);

        // ── Receive-loop callbacks (incoming message dispatch) ────────────

        /// <summary>
        /// Called by <see cref="WsReceiveLoop"/> for each incoming message.
        /// Dispatches to the appropriate handler or resolves pending requests.
        /// </summary>
        public Task HandleIncomingAsync(WsParsedMessage? message)
            => HandleIncomingAsync(message, CancellationToken.None);

        /// <summary>
        /// Handles one incoming message with the cancellation token owned by
        /// its WebSocket connection/request.
        /// </summary>
        public async Task HandleIncomingAsync(
            WsParsedMessage? message,
            CancellationToken requestCancellationToken)
        {
            if (message == null) return;

            switch (message.Type)
            {
                case WsMessageType.Request:
                    await HandleRequestAsync(message, requestCancellationToken).ConfigureAwait(false);
                    break;
                case WsMessageType.Response:
                    ResolveResponse(message.Id!, message.Result, message.Error);
                    break;
                case WsMessageType.Notification:
                    await HandleNotificationAsync(message, requestCancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        private async Task HandleRequestAsync(
            WsParsedMessage message,
            CancellationToken requestCancellationToken)
        {
            var method = message.Method;
            if (string.IsNullOrEmpty(method) || message.Id == null) return;

            if (!_handlers.TryGetValue(method, out var entry))
            {
                // Method not found — send error response
                await SendResponseAsync(message.Id,
                    result: null,
                    error: new WsError
                    {
                        Code = WsErrorCodes.MethodNotFound,
                        Message = "Method not found: " + method
                    }).ConfigureAwait(false);
                return;
            }

            try
            {
                var (result, error) = await entry.Invoker(
                    message.Params,
                    requestCancellationToken).ConfigureAwait(false);
                await SendResponseAsync(message.Id, result, error).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await SendResponseAsync(message.Id,
                    result: null,
                    error: new WsError
                    {
                        Code = WsErrorCodes.InternalError,
                        Message = ex.Message
                    }).ConfigureAwait(false);
            }
        }

        private async Task HandleNotificationAsync(
            WsParsedMessage message,
            CancellationToken requestCancellationToken)
        {
            var method = message.Method;
            if (string.IsNullOrEmpty(method)) return;

            if (_notificationHandlers.TryGetValue(method, out var entry))
            {
                try
                {
                    await entry.Invoker(
                        message.Params,
                        requestCancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Notifications are fire-and-forget; swallow handler exceptions
                }
            }
        }

        private async Task SendResponseAsync(string id, JsonElement? result, WsError? error)
        {
            var sendFunc = SendFunc;
            if (sendFunc == null) return;

            var response = new WsResponse
            {
                Id = id,
                Result = result,
                Error = error
            };

            var bytes = WsEnvelope.SerializeResponse(response, _options);
            try
            {
                await sendFunc(bytes, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // If we can't send the response (connection dropped), swallow —
                // the receive loop will detect the drop and trigger reconnect
            }
        }

        private WsError CreateStructuredError(int rpcCode, ToolCallError error)
            => new WsError
            {
                Code = rpcCode,
                Message = error.Message,
                Data = JsonSerializer.SerializeToElement(error, _options)
            };

        private static ToolCallError CreateMalformedToolCallError(JsonElement? paramsElement)
        {
            var identity = ExtractToolCallIdentity(paramsElement);
            return new ToolCallError(
                ToolCallErrorCodes.InvalidControl,
                "Tool call control metadata is invalid.",
                callId: identity.CallId ?? identity.RequestId,
                correlationId: identity.CorrelationId ?? identity.CallId ?? identity.RequestId);
        }

        private static void ValidateRawToolCallControl(JsonElement? paramsElement)
        {
            if (!paramsElement.HasValue || paramsElement.Value.ValueKind != JsonValueKind.Object)
                return;

            var control = TryGetProperty(paramsElement.Value, "control");
            if (!control.HasValue || control.Value.ValueKind != JsonValueKind.Object)
                return;

            var version = TryGetProperty(control.Value, "version");
            if (version.HasValue && version.Value.ValueKind == JsonValueKind.Null)
            {
                var identity = ExtractToolCallIdentity(paramsElement);
                throw new ToolCallControlException(
                    ToolCallErrorCodes.InvalidControl,
                    "Tool call control version must be an integer.",
                    callId: identity.CallId ?? identity.RequestId,
                    correlationId: identity.CorrelationId ?? identity.CallId ?? identity.RequestId);
            }
        }

        private static void AttachToolCallIdentity(ToolCallError error, JsonElement? paramsElement)
        {
            var identity = ExtractToolCallIdentity(paramsElement);
            if (string.IsNullOrWhiteSpace(error.CallId))
                error.CallId = identity.CallId ?? identity.RequestId;
            if (string.IsNullOrWhiteSpace(error.CorrelationId))
                error.CorrelationId = identity.CorrelationId ?? identity.CallId ?? identity.RequestId ?? error.CallId;
        }

        private static (string? RequestId, string? CallId, string? CorrelationId)
            ExtractToolCallIdentity(JsonElement? paramsElement)
        {
            if (!paramsElement.HasValue || paramsElement.Value.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            var requestId = TryGetStringProperty(paramsElement.Value, "requestID");
            string? callId = null;
            string? correlationId = null;
            var control = TryGetProperty(paramsElement.Value, "control");
            if (control.HasValue && control.Value.ValueKind == JsonValueKind.Object)
            {
                callId = TryGetStringProperty(control.Value, "callId");
                correlationId = TryGetStringProperty(control.Value, "correlationId");
            }

            return (requestId, callId, correlationId);
        }

        private static JsonElement? TryGetProperty(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }

            return null;
        }

        private static string? TryGetStringProperty(JsonElement element, string name)
        {
            var property = TryGetProperty(element, name);
            return property.HasValue && property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
        }

        // ── Pending-request resolution (called by receive loop) ───────────

        /// <summary>
        /// Resolves a pending outgoing request by envelope id.
        /// Called by the receive loop when <c>{id, result}</c> or <c>{id, error}</c> arrives.
        /// </summary>
        public void ResolveResponse(string id, JsonElement? result, WsError? error)
        {
            if (!_pending.TryRemove(id, out var tcs)) return;

            if (error != null)
            {
                tcs.TrySetException(new WsRemoteException(error.Code, error.Message, error.Data));
            }
            else if (result.HasValue)
            {
                tcs.TrySetResult(result.Value);
            }
            else
            {
                // Empty result — set a null JsonElement
                tcs.TrySetResult(default);
            }
        }

        /// <summary>
        /// Rejects all pending requests (called when the connection drops).
        /// </summary>
        public void RejectAllPending(Exception ex)
        {
            foreach (var kvp in _pending)
            {
                if (_pending.TryRemove(kvp.Key, out var tcs))
                    tcs.TrySetException(ex);
            }
        }

        // ── Dual-path completion ──────────────────────────────────────────

        /// <summary>
        /// Registers a deferred completion TCS keyed by <paramref name="requestId"/>
        /// (distinct from the envelope id). Used for dual-path tool completion where
        /// the result may arrive EITHER as the direct RPC response OR via a
        /// <c>NotifyToolRequestCompleted</c> notification carrying a matching requestId.
        /// </summary>
        public void RegisterDeferredCompletion(string requestId, TaskCompletionSource<JsonElement> tcs)
        {
            _deferred[requestId] = tcs;
        }

        /// <summary>
        /// Resolves a deferred completion by requestId. Called when a
        /// <c>NotifyToolRequestCompleted</c> notification arrives with a matching requestId.
        /// Both the direct response and deferred paths can resolve the same TCS
        /// (whichever arrives first wins — <c>TrySetResult</c> is idempotent).
        /// </summary>
        public void ResolveDeferred(string requestId, JsonElement? result)
        {
            if (!_deferred.TryRemove(requestId, out var tcs)) return;

            if (result.HasValue)
                tcs.TrySetResult(result.Value);
            else
                tcs.TrySetResult(default);
        }

        /// <summary>
        /// Clears all deferred completion entries (called on disconnect).
        /// </summary>
        public void ClearDeferred()
        {
            _deferred.Clear();
        }

        // ── Dispose ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

            ClearAll();
            RejectAllPending(new ObjectDisposedException(nameof(WsRpcDispatcher)));
            ClearDeferred();
            SendFunc = null;
        }
    }

    /// <summary>
    /// Exception raised when the server sends an <c>{id, error}</c> response.
    /// </summary>
    public sealed class WsRemoteException : Exception
    {
        public int Code { get; }
        public JsonElement? ErrorData { get; }

        public WsRemoteException(int code, string message, JsonElement? errorData = null)
            : base(message)
        {
            Code = code;
            ErrorData = errorData;
        }
    }
}
