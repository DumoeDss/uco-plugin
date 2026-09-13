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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;

namespace com.IvanMurzak.McpPlugin
{
    /// <summary>
    /// The terminal delegate for one tool invocation.  Middleware receives the
    /// same context and can decide whether to call this delegate.
    /// </summary>
    public delegate Task<ResponseCallTool> ToolCallNext(ToolCallContext context);

    /// <summary>
    /// A transport-neutral interception point for tool execution.
    /// </summary>
    public interface IToolExecutionMiddleware
    {
        Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next);
    }

    /// <summary>
    /// Default middleware that preserves the pre-pipeline execution behavior.
    /// </summary>
    public sealed class PassThroughToolExecutionMiddleware : IToolExecutionMiddleware
    {
        public Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return next(context);
        }
    }

    /// <summary>
    /// Composes middleware in registration order.  The first registered
    /// middleware is the outermost invocation, and a middleware can
    /// short-circuit simply by not invoking <paramref name="next"/>.
    /// </summary>
    public sealed class ToolExecutionPipeline
    {
        private readonly IReadOnlyList<IToolExecutionMiddleware> _middleware;

        public IReadOnlyList<IToolExecutionMiddleware> Middleware => _middleware;

        public ToolExecutionPipeline()
            : this(new[] { new PassThroughToolExecutionMiddleware() })
        {
        }

        public ToolExecutionPipeline(IEnumerable<IToolExecutionMiddleware>? middleware)
        {
            var registered = middleware == null
                ? Array.Empty<IToolExecutionMiddleware>()
                : middleware.Where(item => item != null).ToArray();
            _middleware = registered.Length == 0
                ? new IToolExecutionMiddleware[] { new PassThroughToolExecutionMiddleware() }
                : registered;
        }

        /// <summary>
        /// Invokes the ordered middleware chain and then the supplied terminal.
        /// </summary>
        public async Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext terminal)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (terminal == null)
                throw new ArgumentNullException(nameof(terminal));

            // The transport supplies the caller token.  A deadline is local to
            // this invocation and is never pushed back by a retry or a nested
            // call.  Keep both sources separate so an OperationCanceledException
            // can be reported as either `cancelled` or `deadline_exceeded`.
            using var deadlineCancellation = DeadlineCancellation.Create(context.DeadlineUnixMs);
            using var linkedCancellation = CreateLinkedCancellationSource(
                context.CancellationToken,
                deadlineCancellation?.Token ?? CancellationToken.None);
            var invocationContext = context.Clone();
            invocationContext.CancellationToken = linkedCancellation?.Token
                ?? deadlineCancellation?.Token
                ?? context.CancellationToken;

            var callerCancellationObserved = context.CancellationToken.IsCancellationRequested ? 1 : 0;
            using var callerCancellationRegistration = context.CancellationToken.CanBeCanceled
                ? context.CancellationToken.Register(() => Interlocked.Exchange(ref callerCancellationObserved, 1))
                : default(CancellationTokenRegistration);

            // Do not enter middleware (and therefore never enter a runner) for
            // an already-expired or already-cancelled call.
            if (IsDeadlineExceeded(context, deadlineCancellation))
                return DeadlineOrThrow(invocationContext);
            if (Volatile.Read(ref callerCancellationObserved) != 0)
                return ControlledOrThrowCancellation(invocationContext);

            // A middleware is expected to invoke its continuation at most once,
            // but keeping that invariant at the pipeline boundary prevents a
            // faulty middleware from executing a side-effecting runner twice.
            var terminalGate = new object();
            Task<ResponseCallTool>? terminalTask = null;
            ToolCallNext guardedTerminal = invocationContext =>
            {
                lock (terminalGate)
                {
                    if (terminalTask != null)
                        return terminalTask;

                    try
                    {
                        // Middleware may suspend its continuation long enough for
                        // the absolute deadline or caller token to change. Keep
                        // this check inside the same gate as terminal invocation so
                        // a delayed continuation cannot start a non-cooperative
                        // runner after the call is no longer executable.
                        if (IsDeadlineExceeded(invocationContext, deadlineCancellation))
                        {
                            terminalTask = Task.FromResult(DeadlineOrThrow(invocationContext));
                        }
                        else if (Volatile.Read(ref callerCancellationObserved) != 0
                            || context.CancellationToken.IsCancellationRequested)
                        {
                            terminalTask = Task.FromResult(ControlledOrThrowCancellation(invocationContext));
                        }
                        else
                        {
                            terminalTask = terminal(invocationContext)
                                ?? Task.FromException<ResponseCallTool>(
                                    new InvalidOperationException("Tool execution terminal returned null."));
                        }
                    }
                    catch (Exception exception)
                    {
                        terminalTask = Task.FromException<ResponseCallTool>(exception);
                    }

                    return terminalTask;
                }
            };

            ToolCallNext next = guardedTerminal;
            for (var index = _middleware.Count - 1; index >= 0; index--)
            {
                var current = _middleware[index];
                var continuation = next;
                next = callContext => current.InvokeAsync(callContext, continuation);
            }

            try
            {
                var resultTask = next(invocationContext);
                if (resultTask == null)
                    throw new InvalidOperationException("Tool execution middleware returned a null task.");

                return await resultTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!invocationContext.Legacy)
            {
                if (IsDeadlineExceeded(invocationContext, deadlineCancellation))
                    return ControlledError(invocationContext, ToolCallErrorCodes.DeadlineExceeded, "Tool call deadline expired.");

                // The linked token is intentionally passed to the terminal so
                // cooperative runners can observe caller cancellation.  A
                // non-cooperative runner may still complete under the existing
                // non-durable operation behavior.
                return ControlledError(invocationContext, ToolCallErrorCodes.Cancelled, "Tool call was cancelled.");
            }
            catch (ToolCallControlException exception) when (!invocationContext.Legacy)
            {
                return ControlledError(invocationContext, exception);
            }
            catch (Exception exception) when (!invocationContext.Legacy)
            {
                // Middleware and custom runners are allowed to throw.  The
                // controlled wire contract stays bounded (COCli-09): a short,
                // single-line cause in the message plus type/message/stack in
                // `details` — never raw unbounded exception dumps.
                return ControlledError(
                    invocationContext,
                    ToolCallErrorCodes.ToolExecutionFailed,
                    ExceptionDiagnostics.BoundedFailureMessage(exception, "Tool execution failed"),
                    details: ExceptionDiagnostics.BoundedExceptionDetails(exception),
                    innerException: exception);
            }
        }

        /// <summary>Compatibility name for callers that call a pipeline.</summary>
        public Task<ResponseCallTool> ExecuteAsync(ToolCallContext context, ToolCallNext terminal)
            => InvokeAsync(context, terminal);

        private static CancellationTokenSource? CreateLinkedCancellationSource(
            CancellationToken caller,
            CancellationToken deadline)
        {
            if (!caller.CanBeCanceled && !deadline.CanBeCanceled)
                return null;

            return CancellationTokenSource.CreateLinkedTokenSource(caller, deadline);
        }

        private static bool IsDeadlineExceeded(
            ToolCallContext context,
            DeadlineCancellation? deadlineCancellation)
        {
            if (!context.DeadlineUnixMs.HasValue)
                return false;

            if (deadlineCancellation?.Token.IsCancellationRequested == true)
                return true;

            return context.DeadlineUnixMs.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static ResponseCallTool ControlledOrThrowCancellation(ToolCallContext context)
        {
            if (context.Legacy)
                throw new OperationCanceledException(context.CancellationToken);

            return ControlledError(context, ToolCallErrorCodes.Cancelled, "Tool call was cancelled.");
        }

        private static ResponseCallTool DeadlineOrThrow(ToolCallContext context)
        {
            if (context.Legacy)
                throw new OperationCanceledException(context.CancellationToken);

            return ControlledError(context, ToolCallErrorCodes.DeadlineExceeded, "Tool call deadline expired.");
        }

        private static ResponseCallTool ControlledError(
            ToolCallContext context,
            ToolCallControlException exception)
        {
            var error = exception.ToError();
            if (string.IsNullOrWhiteSpace(error.CallId))
                error.CallId = context.CallId;
            if (string.IsNullOrWhiteSpace(error.CorrelationId))
                error.CorrelationId = context.CorrelationId;
            return ResponseCallTool.Error(error).SetRequestID(context.RequestID);
        }

        private static ResponseCallTool ControlledError(
            ToolCallContext context,
            string code,
            string message,
            JsonNode? details = null,
            Exception? innerException = null)
        {
            // `innerException` exists solely to make it explicit that callers
            // may log the original failure; it is intentionally not serialized.
            _ = innerException;
            return ResponseCallTool.Error(new ToolCallError(
                code,
                message,
                retryable: false,
                callId: context.CallId,
                correlationId: context.CorrelationId,
                details: details)).SetRequestID(context.RequestID);
        }

        /// <summary>
        /// CancellationTokenSource.CancelAfter accepts only an Int32 delay.
        /// This helper reschedules a timer in bounded chunks so an absolute
        /// deadline far in the future is not accidentally treated as a
        /// 24-day deadline.
        /// </summary>
        private sealed class DeadlineCancellation : IDisposable
        {
            private const long MaxTimerDelayMs = int.MaxValue;
            private readonly long _deadlineUnixMs;
            private readonly CancellationTokenSource _source = new();
            private readonly Timer? _timer;
            private int _disposed;

            private DeadlineCancellation(long deadlineUnixMs)
            {
                _deadlineUnixMs = deadlineUnixMs;
                if (deadlineUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    _source.Cancel(throwOnFirstException: false);
                    return;
                }

                var remaining = deadlineUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _timer = new Timer(OnTimer, null, ComputeDelay(remaining), Timeout.Infinite);
            }

            public CancellationToken Token => _source.Token;

            public static DeadlineCancellation? Create(long? deadlineUnixMs)
                => deadlineUnixMs.HasValue ? new DeadlineCancellation(deadlineUnixMs.Value) : null;

            private void OnTimer(object? state)
            {
                if (Volatile.Read(ref _disposed) != 0 || _source.IsCancellationRequested)
                    return;

                var remaining = _deadlineUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (remaining <= 0)
                {
                    try
                    {
                        _source.Cancel(throwOnFirstException: false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Disposal raced the timer callback.
                    }
                    return;
                }

                try
                {
                    _timer?.Change(ComputeDelay(remaining), Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                    // Disposal raced the timer callback.
                }
            }

            private static int ComputeDelay(long deadlineOrRemainingMs)
            {
                var delay = deadlineOrRemainingMs > MaxTimerDelayMs
                    ? MaxTimerDelayMs
                    : deadlineOrRemainingMs;
                return (int)Math.Max(1L, delay);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                _timer?.Dispose();
                _source.Dispose();
            }
        }
    }
}
