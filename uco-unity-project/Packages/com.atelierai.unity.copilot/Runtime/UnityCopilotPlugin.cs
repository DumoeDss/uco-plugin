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

using System;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework;
using WsState = com.AtelierAI.Uco.Framework.ConnectionState;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging;
using R3;

namespace com.AtelierAI.Unity.Copilot
{
    using LogLevel = Runtime.Utils.LogLevel;

    public partial class UnityCopilotPlugin : IDisposable
    {
        public const string Version = "1.0.7";

        private static int _singletonCount = 0;
        public static bool HasAnyInstance => _singletonCount > 0;
        protected static void IncrementSingletonCount() => Interlocked.Increment(ref _singletonCount);
        protected static void DecrementSingletonCount() => Interlocked.Decrement(ref _singletonCount);

        private static LogLevel _configuredLogLevel = LogLevel.Warning;
        // Uses direct enum comparison: configured threshold <= requested level means enabled
        public static bool IsLogEnabled(LogLevel level) => _configuredLogLevel <= level;
        public static void ApplyLogLevel(LogLevel level) => _configuredLogLevel = level;

        protected readonly CompositeDisposable _disposables = new();
        protected readonly CopilotPluginSlot _plugin = new();

        // Tracks only the latest plugin's ConnectionState subscription.
        // Replaced (old one disposed) each time BuildUcoPlugin creates a new IUcoPlugin instance.
        private IDisposable? _pluginConnectionSubscription;

        public IUcoPlugin? UcoPluginInstance => _plugin.Instance;
        public bool HasUcoPluginInstance => _plugin.HasInstance;

        public ILogger Logger => UcoPluginInstance?.Logger ?? _logger;
        public Reflector? Reflector => UcoPluginInstance?.UcoManager.Reflector;
        public IToolManager? Tools => UcoPluginInstance?.UcoManager.ToolManager;
        public IPromptManager? Prompts => UcoPluginInstance?.UcoManager.PromptManager;
        public IResourceManager? Resources => UcoPluginInstance?.UcoManager.ResourceManager;

        public UnityLogCollector? LogCollector { get; protected set; } = null;

        // --- Connection state ---

        protected readonly ReactiveProperty<WsState> _connectionState
            = new(WsState.Disconnected);

        public ReadOnlyReactiveProperty<WsState> ConnectionState => _connectionState;

        public ReadOnlyReactiveProperty<bool> IsConnected => _connectionState
            .Select(x => x == WsState.Connected)
            .ToReadOnlyReactiveProperty(false);

        // --- Constructor / Dispose ---

        protected UnityCopilotPlugin()
        {
            _disposables.Add(_connectionState);
        }

        public virtual void Dispose()
        {
            _pluginConnectionSubscription?.Dispose();
            _pluginConnectionSubscription = null;
            _disposables.Dispose();
            // LogCollector and _connectionState are disposed by _disposables
            LogCollector = null;
        }

        // --- Log collector ---

        public void AddUnityLogCollector(ILogStorage logStorage)
        {
            if (logStorage == null)
                throw new ArgumentNullException(nameof(logStorage));

            if (LogCollector != null)
                throw new InvalidOperationException($"{nameof(UnityLogCollector)} is already added.");

            LogCollector = new UnityLogCollector(logStorage);
            _disposables.Add(LogCollector);
        }

        public void AddUnityLogCollectorIfNeeded(Func<ILogStorage> logStorageProvider)
        {
            if (LogCollector != null)
                return;

            AddUnityLogCollector(logStorageProvider());
        }

        public void DisposeLogCollector()
        {
            LogCollector?.Save();
            LogCollector?.Dispose();
            LogCollector = null;
        }

        // --- Connection methods ---

        public Task<bool> ConnectIfNeeded(CancellationToken cancellationToken = default)
        {
            if (!unityConnectionConfig.KeepConnected)
                return Task.FromResult(false);
            return ConnectCore(restartPendingAttempt: false, cancellationToken);
        }

        /// <summary>
        /// Starts an explicit connection attempt. If an earlier attempt is still pending while
        /// the plugin is disconnected, it is cancelled first so the new attempt does not merely
        /// wait behind stale connection work.
        /// </summary>
        public Task<bool> Connect(CancellationToken cancellationToken = default)
            => ConnectCore(restartPendingAttempt: true, cancellationToken);

        private async Task<bool> ConnectCore(
            bool restartPendingAttempt,
            CancellationToken cancellationToken)
        {
            _logger.LogTrace("{method} called.", nameof(Connect));
            try
            {
                var ucoPlugin = UcoPluginInstance;
                if (ucoPlugin == null)
                {
                    _logger.LogError("{method}: uco plugin instance is null.", nameof(Connect));
                    return false;
                }

                if (restartPendingAttempt
                    && ucoPlugin.ConnectionState.CurrentValue != WsState.Connected)
                {
                    // ConnectionManager cancellation happens before its bounded synchronous
                    // cleanup, so the old attempt releases its gate while the new call waits
                    // with the caller-provided cancellation token.
                    ucoPlugin.DisconnectImmediate();
                }

                return await ucoPlugin.Connect(cancellationToken);
            }
            finally
            {
                _logger.LogTrace("{method} completed.", nameof(Connect));
            }
        }

        public async Task Disconnect()
        {
            _logger.LogTrace("{method} called.", nameof(Disconnect));
            try
            {
                var ucoPlugin = UcoPluginInstance;
                if (ucoPlugin == null)
                {
                    _logger.LogWarning("{method}: uco plugin instance is null, nothing to disconnect, ignoring.",
                        nameof(Disconnect));
                    return;
                }
                try
                {
                    _logger.LogDebug("{method}: Disconnecting uco plugin instance.", nameof(Disconnect));
                    await ucoPlugin.Disconnect();
                }
                catch (Exception e)
                {
                    _logger.LogError("{method}: Exception during disconnecting: {exception}",
                        nameof(Disconnect), e);
                }
            }
            finally
            {
                _logger.LogTrace("{method} completed.", nameof(Disconnect));
            }
        }

        public void DisconnectImmediate()
        {
            _logger.LogTrace("{method} called.", nameof(DisconnectImmediate));
            try
            {
                var ucoPlugin = UcoPluginInstance;
                if (ucoPlugin == null)
                {
                    _logger.LogWarning("{method}: uco plugin instance is null, nothing to disconnect, ignoring.",
                        nameof(DisconnectImmediate));
                    return;
                }
                try
                {
                    _logger.LogDebug("{method}: Disconnecting uco plugin instance.", nameof(DisconnectImmediate));
                    ucoPlugin.DisconnectImmediate();
                }
                catch (Exception e)
                {
                    _logger.LogError("{method}: Exception during disconnecting: {exception}",
                        nameof(DisconnectImmediate), e);
                }
            }
            finally
            {
                _logger.LogTrace("{method} completed.", nameof(DisconnectImmediate));
            }
        }

        public async Task NotifyToolRequestCompleted(RequestToolCompletedData request, CancellationToken cancellationToken = default)
        {
            var ucoPlugin = UcoPluginInstance
                ?? throw new InvalidOperationException($"{nameof(UcoPluginInstance)} is null");

            while (ucoPlugin.ConnectionState.CurrentValue != WsState.Connected)
            {
                await Task.Delay(100, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("{method}: operation cancelled while waiting for connection.",
                        nameof(NotifyToolRequestCompleted));
                    return;
                }
            }

            if (ucoPlugin.UcoManager == null)
            {
                _logger.LogCritical("{method}: {instance} is null",
                    nameof(NotifyToolRequestCompleted), nameof(ucoPlugin.UcoManager));
                return;
            }

            if (ucoPlugin.UcoManagerHub == null)
            {
                _logger.LogCritical("{method}: {instance} is null",
                    nameof(NotifyToolRequestCompleted), nameof(ucoPlugin.UcoManagerHub));
                return;
            }

            await ucoPlugin.UcoManagerHub.NotifyToolRequestCompleted(request);
        }

        // --- Token / Port utilities ---

        /// <summary>
        /// Generates a cryptographically random URL-safe token.
        /// Used by <see cref="UnityConnectionConfig.SetDefault"/> for initial token generation.
        /// </summary>
        public static string GenerateToken()
        {
            var bytes = new byte[32];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Generate a deterministic TCP port based on current directory.
        /// Uses SHA256 hash for better distribution and less collisions.
        /// Port range: 20000-29999 (avoids Windows ephemeral/reserved port ranges).
        /// </summary>
        public static int GeneratePortFromDirectory()
        {
            const int MinPort = 20000; // Range chosen to avoid Windows ephemeral/reserved ports (49152-65535)
            const int MaxPort = 29999;
            const int PortRange = MaxPort - MinPort + 1;

            var currentDir = Environment.CurrentDirectory.ToLowerInvariant();

            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(currentDir));

                // Use first 4 bytes as an unsigned integer to avoid Math.Abs(int.MinValue) overflow
                var hash = (uint)BitConverter.ToInt32(hashBytes, 0);

                // Map to port range
                var port = MinPort + (int)(hash % PortRange);

                return port;
            }
        }
    }
}
