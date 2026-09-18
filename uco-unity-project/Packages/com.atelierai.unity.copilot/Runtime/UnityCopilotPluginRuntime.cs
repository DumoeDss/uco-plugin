/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System.Diagnostics;
using com.AtelierAI.Uco.Framework;
using Microsoft.Extensions.Logging;
using R3;

namespace com.AtelierAI.Unity.Copilot
{
    /// <summary>
    /// Runtime singleton for game builds. Provides the developer-facing
    /// <see cref="Initialize"/> API and maintains a separate Uco connection
    /// alongside the Editor's connection.
    /// </summary>
    public partial class UnityCopilotPluginRuntime : UnityCopilotPlugin
    {
        protected UnityCopilotPluginRuntime(UnityConnectionConfig? config = null) : base()
        {
            unityConnectionConfig = config ?? new UnityConnectionConfig();
            IncrementSingletonCount();
        }

        internal IUcoPlugin BuildFromBuilder(IUcoPluginBuilder builder)
        {
            _logger.LogTrace("{method} called.", nameof(BuildFromBuilder));

            var stopwatch = Stopwatch.StartNew();
            var built = _plugin.Set(builder.Build(CreateDefaultReflector()));
            stopwatch.Stop();
            _logger.LogDebug("Runtime Uco Plugin built in {elapsedMilliseconds} ms.",
                stopwatch.ElapsedMilliseconds);

            ApplyConfigToUcoPlugin(built);

            built.ConnectionState
                .Subscribe(state => _connectionState.Value = state)
                .AddTo(_disposables);

            _logger.LogTrace("{method} completed.", nameof(BuildFromBuilder));
            return built;
        }

        public override void Dispose()
        {
            DecrementSingletonCount();
            base.Dispose();
            _plugin.Dispose();
        }
    }
}
