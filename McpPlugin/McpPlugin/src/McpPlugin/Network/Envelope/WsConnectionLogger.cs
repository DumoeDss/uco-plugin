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
using Microsoft.Extensions.Logging;
using R3;

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>
    /// Replaces <c>HubConnectionLogger</c>. Subscribes to
    /// <see cref="WsConnectionObservable"/> events and logs them.
    /// Same log levels and format as the current implementation.
    /// </summary>
    public sealed class WsConnectionLogger : IDisposable
    {
        readonly string? _guid;
        readonly ILogger _logger;
        readonly CompositeDisposable _disposables = new();
        int _disposed;

        public WsConnectionLogger(ILogger logger, WsConnectionObservable observable, string? guid = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _guid = guid;

            _logger.LogTrace("{guid} WsConnectionLogger.Ctor.", _guid);

            observable.Closed
                .Subscribe(ex =>
                {
                    _logger.LogTrace("{guid} WsConnectionLogger OnClosed. Exception: {message}", _guid, ex?.Message);
                    if (ex != null)
                        _logger.LogError(ex, "{guid} WsConnectionLogger Error in Closed event: {message}", _guid, ex.Message);
                })
                .AddTo(_disposables);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;

            _logger.LogTrace("{guid} WsConnectionLogger.Dispose.", _guid);
            _disposables.Dispose();
        }
    }
}
