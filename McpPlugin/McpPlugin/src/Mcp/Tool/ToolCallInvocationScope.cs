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
using com.IvanMurzak.McpPlugin.Common.Model;

namespace com.IvanMurzak.McpPlugin
{
    /// <summary>
    /// Async-flowing invocation state used by reflected parameter injection.
    /// No per-call values are stored on a shared <see cref="RunTool"/>
    /// instance, so concurrent calls cannot overwrite one another.
    /// </summary>
    public static class ToolCallInvocationScope
    {
        private sealed class ScopeValue
        {
            public ToolCallContext Context { get; }
            public AuthoringInvocation? Invocation { get; }
            public bool RunnerExecutionAuthorized { get; }

            public ScopeValue(
                ToolCallContext context,
                AuthoringInvocation? invocation,
                bool runnerExecutionAuthorized = false)
            {
                Context = context;
                Invocation = invocation;
                RunnerExecutionAuthorized = runnerExecutionAuthorized;
            }
        }

        private static readonly AsyncLocal<ScopeValue?> CurrentValue = new();

        public static ToolCallContext? Current => CurrentValue.Value?.Context;
        public static AuthoringInvocation? CurrentInvocation => CurrentValue.Value?.Invocation;
        internal static bool IsRunnerExecutionAuthorized
            => CurrentValue.Value?.RunnerExecutionAuthorized == true;

        public static IDisposable Push(ToolCallContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var previous = CurrentValue.Value;
            CurrentValue.Value = new ScopeValue(context, null);
            return new Scope(previous);
        }

        /// <summary>
        /// Pushes the immutable runner metadata required by the authoring
        /// middleware. The metadata comes from the existing registration and
        /// cannot be supplied as a generated tool argument.
        /// </summary>
        public static IDisposable Push(ToolCallContext context, AuthoringInvocation invocation)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            var previous = CurrentValue.Value;
            CurrentValue.Value = new ScopeValue(context, invocation);
            return new Scope(previous);
        }

        internal static IDisposable AuthorizeRunnerExecution(IRunTool runner)
        {
            if (runner == null)
                throw new ArgumentNullException(nameof(runner));

            var current = CurrentValue.Value;
            var authorized = current?.Invocation != null
                && current.Invocation.PolicyApproved
                && ReferenceEquals(current.Invocation.Runner, runner);
            if (!authorized)
                return NoopDisposable.Instance;

            CurrentValue.Value = new ScopeValue(
                current!.Context,
                current.Invocation,
                runnerExecutionAuthorized: true);
            return new Scope(current);
        }

        /// <summary>
        /// Direct calls to a reflected runner predate the pipeline.  Supply a
        /// small legacy context for those callers, but preserve an active
        /// pipeline context when one is already present.
        /// </summary>
        public static IDisposable PushIfMissing(string requestId, CancellationToken cancellationToken)
        {
            if (CurrentValue.Value != null)
                return NoopDisposable.Instance;

            var context = new ToolCallContext
            {
                RequestID = requestId,
                CallId = requestId,
                CorrelationId = requestId,
                CancellationToken = cancellationToken,
                Legacy = true
            };
            return Push(context);
        }

        private sealed class Scope : IDisposable
        {
            private readonly ScopeValue? _previous;
            private int _disposed;

            public Scope(ScopeValue? previous) => _previous = previous;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    CurrentValue.Value = _previous;
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
