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
using System.Net.Sockets;
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

        public Task<bool> Connect(CancellationToken cancellationToken = default)
            => ConnectExplicit(cancellationToken);

        private async Task<bool> ConnectExplicit(CancellationToken cancellationToken)
        {
            if (_isDisposed.Value)
            {
                _logger.LogWarning("{class}[{guid}] {method} called but already disposed, ignored.",
                    nameof(ConnectionManager), _guid, nameof(Connect));
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection canceled before starting for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(Connect), Endpoint);
                return false;
            }

            Task<bool>? pendingReplacement;
            Task<bool>? staleTask;
            Task? staleSettlement = null;
            CancellationTokenSource? staleCancellation = null;
            TaskCompletionSource<bool>? replacementOwnership = null;
            Task? pendingPublication = null;
            TaskCompletionSource<bool>? publicationOwnership = null;
            var observedDisconnectSequence = 0L;

            try
            {
                await _ongoingConnectionGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                pendingReplacement = _pendingExplicitConnectionTask;
                staleTask = _ongoingConnectionTask;

                if (pendingReplacement == null &&
                    staleTask != null &&
                    !staleTask.IsCompleted)
                {
                    replacementOwnership = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingReplacement = replacementOwnership.Task;
                    _pendingExplicitConnectionTask = pendingReplacement;
                    staleSettlement = _ongoingConnectionSettlement;
                    staleCancellation = internalCts;
                    observedDisconnectSequence = Interlocked.Read(ref _disconnectSequence);
                }
                else if (pendingReplacement == null)
                {
                    if (_explicitConnectionPublication == null)
                    {
                        publicationOwnership = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                        _explicitConnectionPublication = publicationOwnership;
                    }
                    else
                    {
                        pendingPublication = _explicitConnectionPublication.Task;
                    }
                }
            }
            finally
            {
                _ongoingConnectionGate.Release();
            }

            if (replacementOwnership == null)
            {
                if (pendingReplacement != null)
                    return await WaitForConnectionCompletion(pendingReplacement, cancellationToken);

                if (pendingPublication != null)
                {
                    if (!await WaitForConnectionPublication(pendingPublication, cancellationToken))
                        return false;
                    return await ConnectExplicit(cancellationToken);
                }

                try
                {
                    return await ConnectOwned(cancellationToken, publicationOwnership);
                }
                finally
                {
                    await CompleteConnectionPublication(publicationOwnership);
                }
            }

            return await ReplaceOwnedConnection(
                staleTask!,
                staleSettlement,
                staleCancellation,
                observedDisconnectSequence,
                replacementOwnership,
                cancellationToken);
        }

        private async Task<bool> ConnectShared(CancellationToken cancellationToken)
        {
            Task<bool>? taskToJoin;
            try
            {
                await _ongoingConnectionGate.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                taskToJoin = _pendingExplicitConnectionTask ?? _ongoingConnectionTask;
            }
            finally
            {
                _ongoingConnectionGate.Release();
            }

            return taskToJoin != null
                ? await WaitForConnectionCompletion(taskToJoin, cancellationToken)
                : await ConnectOwned(cancellationToken);
        }

        private async Task<bool> ReplaceOwnedConnection(
            Task<bool> staleTask,
            Task? staleSettlement,
            CancellationTokenSource? staleCancellation,
            long observedDisconnectSequence,
            TaskCompletionSource<bool> replacementOwnership,
            CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogDebug(
                    "{class}[{guid}] {method} claimed explicit replacement; cancelling and settling the current owner.",
                    nameof(ConnectionManager), _guid, nameof(Connect));
                _connectionState.Value = WsState.Reconnecting;

                try
                {
                    staleCancellation?.Cancel(throwOnFirstException: false);
                }
                catch (ObjectDisposedException)
                {
                }
                catch (AggregateException ex)
                {
                    _logger.LogWarning(ex,
                        "{class}[{guid}] {method} A cancellation callback failed while replacing the current connection owner.",
                        nameof(ConnectionManager), _guid, nameof(Connect));
                }

                try
                {
                    await staleTask;
                }
                catch (OperationCanceledException)
                {
                }

                if (staleSettlement != null)
                    await staleSettlement;

                if (cancellationToken.IsCancellationRequested ||
                    observedDisconnectSequence != Interlocked.Read(ref _disconnectSequence))
                {
                    replacementOwnership.TrySetResult(false);
                    return false;
                }

                var result = await ConnectOwned(cancellationToken);
                replacementOwnership.TrySetResult(result);
                return result;
            }
            catch (Exception ex)
            {
                replacementOwnership.TrySetException(ex);
                throw;
            }
            finally
            {
                await _ongoingConnectionGate.WaitAsync(CancellationToken.None);
                if (ReferenceEquals(_pendingExplicitConnectionTask, replacementOwnership.Task))
                    _pendingExplicitConnectionTask = null;
                _ongoingConnectionGate.Release();
            }
        }

        private async Task<bool> ConnectOwned(
            CancellationToken cancellationToken,
            TaskCompletionSource<bool>? publicationOwnership = null)
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
            var observedAttemptSequence = Interlocked.Read(ref _attemptSequence);
            var observedDisconnectSequence = Interlocked.Read(ref _disconnectSequence);
            if (ongoingTask != null)
                CompleteConnectionPublicationLocked(publicationOwnership);
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
                if (ongoingTask != null)
                    CompleteConnectionPublicationLocked(publicationOwnership);
                _ongoingConnectionGate.Release();

                if (ongoingTask != null)
                {
                    _logger.LogDebug("{class}[{guid}] {method} Connection already in progress after acquiring gate; waiting for its completion.",
                        nameof(ConnectionManager), _guid, nameof(Connect));
                    return await WaitForConnectionCompletion(ongoingTask, cancellationToken);
                }

                var ownsExplicitReplacement = TryClaimExplicitReplacement(observedDisconnectSequence);
                if (Interlocked.Read(ref _attemptSequence) != observedAttemptSequence &&
                    !ownsExplicitReplacement)
                {
                    _logger.LogDebug("{class}[{guid}] {method} A connection attempt completed while this caller waited for the gate.",
                        nameof(ConnectionManager), _guid, nameof(Connect));
                    return _webSocket.CurrentValue?.State is WebSocketState.Open;
                }

                if (_webSocket.CurrentValue?.State is WebSocketState.Open)
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
                var connectionSettlement = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                await _ongoingConnectionGate.WaitAsync(cancellationToken);
                connectionTask = InternalConnect(cancellationToken);
                _ongoingConnectionTask = connectionTask;
                _ongoingConnectionSettlement = connectionSettlement.Task;
                CompleteConnectionPublicationLocked(publicationOwnership);
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
                    if (ReferenceEquals(_ongoingConnectionTask, connectionTask))
                        _ongoingConnectionTask = null;
                    if (ReferenceEquals(_ongoingConnectionSettlement, connectionSettlement.Task))
                        _ongoingConnectionSettlement = null;
                    _ongoingConnectionGate.Release();
                    connectionSettlement.TrySetResult(true);
                }
            }
            finally
            {
                _logger.LogDebug("{class}[{guid}] {method} releasing gate.",
                    nameof(ConnectionManager), _guid, nameof(Connect));
                _gate.Release();
            }
        }

        private static async Task<bool> WaitForConnectionPublication(
            Task publication,
            CancellationToken cancellationToken)
        {
            try
            {
                var completed = await Task.WhenAny(
                    publication,
                    Task.Delay(Timeout.Infinite, cancellationToken));
                if (completed != publication)
                    return false;
                await publication;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private async Task CompleteConnectionPublication(
            TaskCompletionSource<bool>? publicationOwnership)
        {
            if (publicationOwnership == null || publicationOwnership.Task.IsCompleted)
                return;

            await _ongoingConnectionGate.WaitAsync(CancellationToken.None);
            try
            {
                CompleteConnectionPublicationLocked(publicationOwnership);
            }
            finally
            {
                _ongoingConnectionGate.Release();
            }
        }

        private void CompleteConnectionPublicationLocked(
            TaskCompletionSource<bool>? publicationOwnership)
        {
            if (publicationOwnership == null)
                return;
            if (ReferenceEquals(_explicitConnectionPublication, publicationOwnership))
                _explicitConnectionPublication = null;
            publicationOwnership.TrySetResult(true);
        }

        private bool TryClaimExplicitReplacement(long observedDisconnectSequence)
        {
            if (observedDisconnectSequence <= 0 ||
                observedDisconnectSequence != Interlocked.Read(ref _disconnectSequence))
                return false;

            while (true)
            {
                var claimed = Interlocked.Read(ref _claimedReplacementSequence);
                if (claimed >= observedDisconnectSequence)
                    return false;
                if (Interlocked.CompareExchange(
                        ref _claimedReplacementSequence,
                        observedDisconnectSequence,
                        claimed) == claimed)
                    return true;
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
            Interlocked.Exchange(ref _connectionCycleAttemptCount, 0);
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

            // Handshake and capability registration are part of the owned Connect attempt,
            // but their RPCs travel over this same manager. Once the transport is open they
            // must bypass Connect(), otherwise the attempt waits on itself forever.
            if (_webSocket.CurrentValue?.State is WebSocketState.Open &&
                _connectionState.Value is WsState.Connecting or WsState.Reconnecting)
                return true;

            if (!_continueToReconnect.CurrentValue)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection not available and auto-reconnect disabled for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(EnsureConnection), Endpoint);
                return false;
            }

            _logger.LogDebug("{class}[{guid}] {method} Connection is not established. Attempting to connect to: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(EnsureConnection), Endpoint);

            await ConnectShared(cancellationToken);

            if (_connectionState.Value is not WsState.Connected
                && _webSocket.CurrentValue?.State is not WebSocketState.Open)
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
            // ClientWebSocket is strictly one-shot: ConnectAsync may be called ONLY on an
            // instance whose State is None (never started). A socket in a DEAD state
            // (Closed/Aborted/CloseSent/CloseReceived) has already been started and CANNOT be
            // reused — it must be discarded and replaced with a fresh instance.
            // A socket that is None (fresh) or Open is usable and MUST be preserved.
            // Concurrent callers share _ongoingConnectionTask before entering this method;
            // therefore a Connecting socket observed here has no live owning task and is a
            // stale cancelled/timed-out attempt that must be replaced.
            if (existing != null && existing.State
                    is WebSocketState.None
                    or WebSocketState.Open)
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

            Task<(ClientWebSocket WebSocket, Uri Uri)> connectionCreationTask;
            try
            {
                connectionCreationTask = _wsProvider.CreateConnectionAsync(Endpoint);
            }
            catch (Exception ex)
            {
                BeginAttempt();
                ReportAttemptStage("transport-failed", ex.GetBaseException().Message, terminal: true);
                throw;
            }

            var cancellationSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                cancellationSignal);
            var completedCreation = await Task.WhenAny(
                connectionCreationTask,
                cancellationSignal.Task);
            if (completedCreation != connectionCreationTask)
            {
                ObserveAndDisposeAbandonedConnectionCreation(connectionCreationTask);
                BeginAttempt();
                ReportAttemptStage(
                    "cancelled",
                    "Connection cancellation was requested during transport creation.",
                    terminal: true);
                return false;
            }

            (ClientWebSocket WebSocket, Uri Uri) connection;
            try
            {
                connection = await connectionCreationTask;
            }
            catch (Exception ex)
            {
                BeginAttempt();
                ReportAttemptStage("transport-failed", ex.GetBaseException().Message, terminal: true);
                throw;
            }
            var (ws, uri) = connection;
            if (ws == null)
            {
                _logger.LogError("{class}[{guid}] {method} Failed to create WebSocket instance. Check connection configuration for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);
                BeginAttempt();
                ReportAttemptStage("transport-failed",
                    "The WebSocket provider did not create a transport.", terminal: true);
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("{class}[{guid}] {method} Connection canceled before setting up observables for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);

                BeginAttempt();
                ReportAttemptStage("cancelled", "Connection cancellation was requested before transport setup.", terminal: true);
                try { ws.Dispose(); }
                catch { }
                return false;
            }

            _logger.LogDebug("{class}[{guid}] {method} Successfully created WebSocket instance for endpoint: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(CreateWebSocketIfNeeded), Endpoint);

            // Invalidate every prior-generation call before publishing a fresh identity.
            _dispatcher.CancelAllInFlight();
            var generation = Interlocked.Increment(ref _connectionGeneration);
            _dispatcher.CurrentGeneration = generation;
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

        private static void ObserveAndDisposeAbandonedConnectionCreation(
            Task<(ClientWebSocket WebSocket, Uri Uri)> connectionCreationTask)
        {
            _ = connectionCreationTask.ContinueWith(
                static task =>
                {
                    if (task.Status == TaskStatus.RanToCompletion)
                    {
                        try { task.Result.WebSocket?.Dispose(); }
                        catch { }
                        return;
                    }

                    _ = task.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
            var closedSub = _wsObservable.Closed
                .Where(_ => _continueToReconnect.CurrentValue && !_cancellationTokenSource.IsCancellationRequested)
                .Subscribe(ex =>
                {
                    _logger.LogWarning(ex, "{class}[{guid}] {method} Connection closed. Attempting fresh reconnection to: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(SetupWsObservables), Endpoint);
                    try { _webSocket.CurrentValue?.Abort(); }
                    catch { }
                    ReportConnectionInitializationFailure(
                        ConnectionGeneration,
                        "transport-failed",
                        ex?.GetBaseException().Message ?? "The transport closed during connection initialization.");
                    _connectionState.Value = WsState.Reconnecting;

                    // Wait for any current owner to settle before deciding whether another
                    // Connect is needed. This closes the narrow race where the transport can
                    // close after InternalConnect completes but before Connect clears the
                    // _ongoingConnectionTask field.
                    var reconnectTask = ReconnectAfterCurrentOwnerSettles(
                        _cancellationTokenSource.Token);
                    _ = reconnectTask.ContinueWith(static t => _ = t.Exception,
                        TaskContinuationOptions.ExecuteSynchronously);
                });

            _wsReconnectSubscription.Disposable = closedSub;
        }

        private async Task ReconnectAfterCurrentOwnerSettles(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested &&
                _continueToReconnect.CurrentValue)
            {
                await _ongoingConnectionGate.WaitAsync(cancellationToken);
                var ongoing = _ongoingConnectionTask;
                _ongoingConnectionGate.Release();

                if (ongoing == null)
                    break;

                try { await ongoing; }
                catch { }

                // The outer Connect continuation clears _ongoingConnectionTask immediately
                // after this task completes. Yield briefly rather than racing that cleanup.
                await Task.Delay(1, cancellationToken);
            }

            if (cancellationToken.IsCancellationRequested ||
                !_continueToReconnect.CurrentValue ||
                _connectionState.Value is not WsState.Reconnecting)
                return;

            await ConnectShared(cancellationToken);
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

        /// <summary>
        /// Bounds handshake plus handler/capability registration for one transport generation.
        /// Individual RPCs are bounded as well; this outer bound guarantees attempt ownership
        /// even if an application callback never completes.
        /// </summary>
        protected virtual TimeSpan ConnectionInitializationTimeout { get; } = TimeSpan.FromSeconds(45);

        private const int MaxConsecutiveInitializationFailures = 3;

        private async Task<bool> StartConnectionLoop(CancellationToken cancellationToken)
        {
            _logger.LogDebug("{class}[{guid}] {method} Starting connection loop for endpoint: {endpoint}",
                nameof(ConnectionManager), _guid, nameof(StartConnectionLoop), Endpoint);

            var consecutiveRejections = 0;
            var consecutiveInitializationFailures = 0;

            while (!cancellationToken.IsCancellationRequested && _continueToReconnect.CurrentValue)
            {
                // ClientWebSocket is one-shot: once ConnectAsync has been invoked on an instance
                // (even if it failed), that instance can never be reused. Each retry iteration
                // must therefore start from a FRESH socket — CreateWebSocketIfNeeded disposes
                // the previous (now started) instance and creates a new one. Without this, the
                // 2nd+ attempt reuses the faulted socket and throws "The WebSocket has already
                // been started" in a tight retry loop.
                if (!await CreateWebSocketIfNeeded(cancellationToken))
                    return false;

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
                        consecutiveInitializationFailures = 0;
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
                        ReportAttemptStage("rejected", "Server closed the connection during the stability window.", terminal: true);
                        return false;
                    }
                }
                else
                {
                    consecutiveRejections = 0;

                    var stage = LastAttempt.Stage;
                    if (stage == "rejected")
                    {
                        _continueToReconnect.Value = false;
                        _connectionState.Value = WsState.Disconnected;
                        _authorizationRejected.OnNext(Unit.Default);
                        return false;
                    }

                    if (stage == "handshake-failed" ||
                        stage == "handler-registration-failed" ||
                        stage == "registration-failed")
                    {
                        consecutiveInitializationFailures++;
                        if (consecutiveInitializationFailures >= MaxConsecutiveInitializationFailures)
                        {
                            _logger.LogError(
                                "{class}[{guid}] {method} Connection initialization failed {count} times consecutively. Stopping reconnection. Last stage: {stage}.",
                                nameof(ConnectionManager), _guid, nameof(StartConnectionLoop),
                                consecutiveInitializationFailures, stage);
                            _continueToReconnect.Value = false;
                            _connectionState.Value = WsState.Disconnected;
                            return false;
                        }
                    }
                    else
                    {
                        consecutiveInitializationFailures = 0;
                    }
                }

                if (cancellationToken.IsCancellationRequested || !_continueToReconnect.CurrentValue)
                    break;

                _connectionState.Value = WsState.Reconnecting;
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

            var attemptId = BeginAttempt();

            try
            {
                if (_connectionState.Value is not WsState.Reconnecting)
                    _connectionState.Value = WsState.Connecting;

                var connectionTask = ws.ConnectAsync(uri, cancellationToken);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                var completedTask = await Task.WhenAny(connectionTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    try { ws.Abort(); }
                    catch { }
                    _ = connectionTask.ContinueWith(static t => _ = t.Exception,
                        TaskContinuationOptions.ExecuteSynchronously);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        ReportAttemptStage("cancelled", "Connection cancellation was requested.", terminal: true);
                        return false;
                    }
                    _logger.LogWarning("{class}[{guid}] {method} Connection attempt timed out after 30 seconds for endpoint: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);
                    ReportAttemptStage("timed-out", "Transport connection exceeded 30 seconds.", terminal: true);
                    return false;
                }

                if (connectionTask.IsCompletedSuccessfully)
                {
                    _logger.LogInformation("{class}[{guid}] {method} Connection established successfully to: {endpoint}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);

                    // Start the receive loop — this feeds the WsConnectionObservable
                    // and handles all incoming messages.
                    StartReceiveLoop(ws, cancellationToken);

                    ReportAttemptStage("transport-connected");
                    var generation = ConnectionGeneration;
                    var initializationTask = BeginConnectionInitialization(generation, attemptId);
                    _transportConnected.OnNext(Unit.Default);

                    ConnectionInitializationResult initialization;
                    try
                    {
                        var initializationTimeoutTask = Task.Delay(ConnectionInitializationTimeout, cancellationToken);
                        var completedInitialization = await Task.WhenAny(initializationTask, initializationTimeoutTask);
                        if (completedInitialization != initializationTask)
                        {
                            var cancelled = cancellationToken.IsCancellationRequested;
                            var stage = cancelled
                                ? "cancelled"
                                : LastAttempt.Stage == "registering-capabilities"
                                    ? "registration-failed"
                                    : "handshake-failed";
                            var cause = cancelled
                                ? "Connection initialization was cancelled."
                                : $"Connection initialization exceeded {ConnectionInitializationTimeout.TotalSeconds:0.###} seconds.";
                            ReportConnectionInitializationFailure(generation, stage, cause);
                        }

                        initialization = await initializationTask;
                    }
                    finally
                    {
                        ClearConnectionInitialization(generation, attemptId, initializationTask);
                    }

                    if (initialization.Success)
                        return true;

                    AbortAttemptTransport(ws, initialization.RootCause);
                    return false;
                }
                else if (connectionTask.IsCanceled)
                {
                    _logger.LogDebug("{class}[{guid}] {method} Connection task was cancelled for endpoint: {endpoint}.",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);
                    ReportAttemptStage("cancelled", "The transport connection task was cancelled.", terminal: true);
                }
                else
                {
                    var rootCause = connectionTask.Exception?.GetBaseException().Message
                        ?? "The WebSocket transport connection failed without an exception detail.";
                    _logger.LogWarning("{class}[{guid}] {method} Connection attempt failed for endpoint: {endpoint}. Exception: {exception}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint, rootCause);
                    ReportAttemptStage("transport-failed", rootCause, terminal: true);
                }
            }
            catch (OperationCanceledException)
            {
                try { ws.Abort(); }
                catch { }
                _logger.LogWarning("{class}[{guid}] {method} Connection attempt canceled for endpoint: {endpoint}",
                    nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint);
                ReportAttemptStage("cancelled", "Connection cancellation was requested.", terminal: true);
            }
            catch (WebSocketException ex)
            {
                if (IsExpectedConnectionRefusal(ex))
                {
                    _logger.LogDebug("{class}[{guid}] {method} Endpoint is not listening yet; retrying quietly. Attempt: {attemptId}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), attemptId);
                }
                else
                {
                    _logger.LogError("{class}[{guid}] {method} WebSocketException during connection attempt to endpoint: {endpoint}. Error: {error}",
                        nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint, ex.Message);
                }
                ReportAttemptStage("transport-failed", ex.Message, terminal: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{class}[{guid}] {method} Connection attempt failed for endpoint: {endpoint}. Error: {error}",
                    nameof(ConnectionManager), _guid, nameof(AttemptConnection), Endpoint, ex.Message);
                ReportAttemptStage("transport-failed", ex.GetBaseException().Message, terminal: true);
            }

            return false;
        }

        private void AbortAttemptTransport(ClientWebSocket ws, string? rootCause)
        {
            _dispatcher.RejectAllPending(new InvalidOperationException(
                rootCause ?? "Connection initialization failed."));
            _dispatcher.ClearDeferred();
            _dispatcher.CancelAllInFlight();

            try { ws.Abort(); }
            catch { }
            try { ws.Dispose(); }
            catch { }

            if (ReferenceEquals(_webSocket.CurrentValue, ws))
            {
                _webSocket.Value = null;
                _pendingConnectUri = null;
            }
        }

        private static bool IsExpectedConnectionRefusal(WebSocketException exception)
        {
            if (exception.InnerException is SocketException socketException &&
                socketException.SocketErrorCode == SocketError.ConnectionRefused)
                return true;
            return exception.Message.IndexOf("refused", StringComparison.OrdinalIgnoreCase) >= 0 ||
                exception.Message.IndexOf("actively refused", StringComparison.OrdinalIgnoreCase) >= 0;
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
