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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework
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

    internal sealed class InFlightCall : IDisposable
    {
        private readonly CancellationTokenSource _linkedCancellation;
        private int _disposed;

        public InFlightCall(
            string identity,
            string requestId,
            string cancellationId,
            int generation,
            CancellationToken generationToken)
        {
            Identity = identity;
            RequestId = requestId;
            CancellationId = cancellationId;
            Generation = generation;
            _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(generationToken);
        }

        public string Identity { get; }
        public string RequestId { get; }
        public string CancellationId { get; }
        public int Generation { get; }
        public CancellationToken Token => _linkedCancellation.Token;

        public void Cancel()
        {
            try { _linkedCancellation.Cancel(throwOnFirstException: false); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _linkedCancellation.Dispose();
        }
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
        private readonly object _inFlightGate = new();
        private readonly Dictionary<string, InFlightCall> _inFlightByIdentity = new(StringComparer.Ordinal);
        private readonly Dictionary<string, InFlightCall> _inFlightByRequest = new(StringComparer.Ordinal);
        private readonly Dictionary<string, InFlightCall> _inFlightByCancellation = new(StringComparer.Ordinal);
        private const int MaxInFlightCalls = 1024;
        private const int MaxInFlightCallsPerGeneration = 256;
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
                await SendResponseAsync(message.Id,
                    result: null,
                    error: new WsError
                    {
                        Code = WsErrorCodes.MethodNotFound,
                        Message = "Method not found: " + method
                    }).ConfigureAwait(false);
                return;
            }

            InFlightCall? inFlight = null;
            try
            {
                inFlight = TryRegisterInFlightToolCall(
                    method,
                    message.Params,
                    requestCancellationToken);
                var effectiveToken = inFlight?.Token ?? requestCancellationToken;
                var (result, error) = await entry.Invoker(
                    message.Params,
                    effectiveToken).ConfigureAwait(false);
                await SendResponseAsync(message.Id, result, error).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var identity = ExtractToolCallIdentity(message.Params);
                await SendResponseAsync(message.Id,
                    result: null,
                    error: CreateStructuredError(
                        WsErrorCodes.CallCancelled,
                        new ToolCallError(
                            ToolCallErrorCodes.Cancelled,
                            "Tool call was cancelled.",
                            callId: identity.CallId ?? identity.RequestId,
                            correlationId: identity.CorrelationId
                                ?? identity.CallId ?? identity.RequestId)))
                    .ConfigureAwait(false);
            }
            catch (ToolCallControlException ex)
            {
                var error = ex.ToError();
                AttachToolCallIdentity(error, message.Params);
                await SendResponseAsync(message.Id,
                    result: null,
                    error: CreateStructuredError(
                        ex.Code == ToolCallErrorCodes.OperationCapacityExceeded
                            ? WsErrorCodes.PendingCapacityExceeded
                            : WsErrorCodes.InvalidParams,
                        error)).ConfigureAwait(false);
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
            finally
            {
                if (inFlight != null) RemoveInFlight(inFlight);
            }
        }

        private InFlightCall? TryRegisterInFlightToolCall(
            string method,
            JsonElement? paramsElement,
            CancellationToken generationToken)
        {
            if (method != "RunCallTool" && method != "RunSystemTool") return null;
            var identity = ExtractToolCallIdentity(paramsElement);
            var requestId = BoundIdentity(identity.RequestId);
            var callId = BoundIdentity(identity.CallId);
            var cancellationId = BoundIdentity(ExtractCancellationId(paramsElement));
            if (string.IsNullOrEmpty(requestId) && string.IsNullOrEmpty(callId)
                && string.IsNullOrEmpty(cancellationId))
                return null;

            var primary = !string.IsNullOrEmpty(callId) ? callId
                : !string.IsNullOrEmpty(requestId) ? requestId
                : cancellationId;
            lock (_inFlightGate)
            {
                if (_inFlightByIdentity.Count >= MaxInFlightCalls
                    || _inFlightByIdentity.Values.Count(call => call.Generation == CurrentGeneration)
                        >= MaxInFlightCallsPerGeneration)
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.OperationCapacityExceeded,
                        "In-flight tool call capacity is full.",
                        retryable: true);
                }
                if (_inFlightByIdentity.ContainsKey(primary!)
                    || (!string.IsNullOrEmpty(requestId) && _inFlightByRequest.ContainsKey(requestId))
                    || (!string.IsNullOrEmpty(cancellationId)
                        && _inFlightByCancellation.ContainsKey(cancellationId)))
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.InvalidControl,
                        "A tool call with this identity is already in flight.");
                }
                var call = new InFlightCall(
                    primary!, requestId ?? string.Empty, cancellationId ?? string.Empty,
                    CurrentGeneration, generationToken);
                _inFlightByIdentity.Add(primary!, call);
                if (!string.IsNullOrEmpty(requestId) && !_inFlightByRequest.ContainsKey(requestId))
                    _inFlightByRequest.Add(requestId, call);
                if (!string.IsNullOrEmpty(cancellationId)
                    && !_inFlightByCancellation.ContainsKey(cancellationId))
                    _inFlightByCancellation.Add(cancellationId, call);
                return call;
            }
        }

        public int CurrentGeneration { get; set; }

        public ResponseCancelToolCall CancelToolCall(RequestCancelToolCall? request)
        {
            if (request == null)
                return CancelResponse(false, "cancellation_unavailable", "Cancellation request is missing.");
            if (request.Generation != CurrentGeneration)
                return CancelResponse(false, "stale_generation", "Cancellation generation is stale.");

            InFlightCall? call;
            var callId = BoundIdentity(request.CallId);
            var requestId = BoundIdentity(request.RequestID);
            var cancellationId = BoundIdentity(request.CancellationId);
            lock (_inFlightGate)
            {
                call = !string.IsNullOrEmpty(callId)
                    && _inFlightByIdentity.TryGetValue(callId, out var byCall) ? byCall
                    : !string.IsNullOrEmpty(requestId)
                        && _inFlightByRequest.TryGetValue(requestId, out var byRequest) ? byRequest
                        : !string.IsNullOrEmpty(cancellationId)
                            && _inFlightByCancellation.TryGetValue(cancellationId, out var byCancellation)
                                ? byCancellation
                                : null;
            }
            if (call == null || call.Generation != request.Generation)
                return CancelResponse(false, "operation_not_found", "No matching in-flight tool call exists.");

            call.Cancel();
            return CancelResponse(true, "cancelled", "Cancellation was delivered to the in-flight call.");
        }

        public void CancelAllInFlight()
        {
            InFlightCall[] calls;
            lock (_inFlightGate)
            {
                calls = _inFlightByIdentity.Values.Distinct().ToArray();
                _inFlightByIdentity.Clear();
                _inFlightByRequest.Clear();
                _inFlightByCancellation.Clear();
            }
            foreach (var call in calls)
            {
                call.Cancel();
                call.Dispose();
            }
        }

        private void RemoveInFlight(InFlightCall call)
        {
            lock (_inFlightGate)
            {
                if (_inFlightByIdentity.TryGetValue(call.Identity, out var current)
                    && ReferenceEquals(current, call))
                    _inFlightByIdentity.Remove(call.Identity);
                if (!string.IsNullOrEmpty(call.RequestId)
                    && _inFlightByRequest.TryGetValue(call.RequestId, out current)
                    && ReferenceEquals(current, call))
                    _inFlightByRequest.Remove(call.RequestId);
                if (!string.IsNullOrEmpty(call.CancellationId)
                    && _inFlightByCancellation.TryGetValue(call.CancellationId, out current)
                    && ReferenceEquals(current, call))
                    _inFlightByCancellation.Remove(call.CancellationId);
            }
            call.Dispose();
        }

        private static string? ExtractCancellationId(JsonElement? paramsElement)
        {
            if (!paramsElement.HasValue || paramsElement.Value.ValueKind != JsonValueKind.Object)
                return null;
            var control = TryGetProperty(paramsElement.Value, "control");
            return control.HasValue && control.Value.ValueKind == JsonValueKind.Object
                ? TryGetStringProperty(control.Value, "cancellationId")
                : null;
        }

        private static string? BoundIdentity(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var trimmed = value.Trim();
            return trimmed.Length <= 160 ? trimmed : trimmed.Substring(0, 160);
        }

        private static ResponseCancelToolCall CancelResponse(
            bool accepted,
            string code,
            string message)
            => new ResponseCancelToolCall
            {
                Accepted = accepted,
                Code = code,
                Message = message
            };

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

            // System.Text.Json maps explicit JSON null to null for the
            // nullable authoring fields. Without this raw check, `null` would
            // become indistinguishable from an omitted directive and could
            // reach a manager as a legacy/default value. Validate the fields
            // whose wire presence is semantically significant before typed
            // deserialization or runner lookup.
            var identityForAuthoring = ExtractToolCallIdentity(paramsElement);
            var confirm = TryGetProperty(control.Value, "confirm");
            if (confirm.HasValue && (confirm.Value.ValueKind == JsonValueKind.Null
                || (confirm.Value.ValueKind != JsonValueKind.True
                    && confirm.Value.ValueKind != JsonValueKind.False)))
            {
                throw InvalidRawAuthoringControl(
                    "confirm must be a boolean.",
                    identityForAuthoring);
            }

            var dryRun = TryGetProperty(control.Value, "dryRun");
            if (dryRun.HasValue && (dryRun.Value.ValueKind != JsonValueKind.String
                || (dryRun.Value.GetString() != "none"
                    && dryRun.Value.GetString() != "validate"
                    && dryRun.Value.GetString() != "plan")))
            {
                throw InvalidRawAuthoringControl(
                    "dryRun must be one of: none, validate, plan.",
                    identityForAuthoring);
            }

            var confirmation = TryGetProperty(control.Value, "confirmation");
            if (confirmation.HasValue && confirmation.Value.ValueKind != JsonValueKind.Object)
            {
                throw InvalidRawAuthoringControl(
                    "confirmation must be an object.",
                    identityForAuthoring);
            }
        }

        private static ToolCallControlException InvalidRawAuthoringControl(
            string message,
            (string? RequestId, string? CallId, string? CorrelationId) identity)
            => new ToolCallControlException(
                ToolCallErrorCodes.InvalidControl,
                message,
                callId: identity.CallId ?? identity.RequestId,
                correlationId: identity.CorrelationId ?? identity.CallId ?? identity.RequestId);

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
            CancelAllInFlight();
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
