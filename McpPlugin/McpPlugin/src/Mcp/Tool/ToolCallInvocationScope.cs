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
    internal static class ToolCallInvocationScope
    {
        private static readonly AsyncLocal<ToolCallContext?> CurrentValue = new();

        public static ToolCallContext? Current => CurrentValue.Value;

        public static IDisposable Push(ToolCallContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var previous = CurrentValue.Value;
            CurrentValue.Value = context;
            return new Scope(previous);
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
            private readonly ToolCallContext? _previous;
            private int _disposed;

            public Scope(ToolCallContext? previous) => _previous = previous;

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
