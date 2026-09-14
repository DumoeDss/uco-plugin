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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.AtelierAI.Unity.Copilot.Utils;
using Microsoft.Extensions.Logging;
using R3;

namespace com.AtelierAI.Unity.Copilot
{
    using static com.IvanMurzak.McpPlugin.Common.Consts.MCP.Server;
    using ILogger = Microsoft.Extensions.Logging.ILogger;
    using LogLevel = com.AtelierAI.Unity.Copilot.Runtime.Utils.LogLevel;

    public partial class UnityCopilotPluginEditor
    {
        static readonly Subject<UnityConnectionConfig> _onConfigChanged = new Subject<UnityConnectionConfig>();
        static readonly ILogger _logger = UnityLoggerFactory.LoggerFactory.CreateLogger<UnityCopilotPluginEditor>();
        static readonly object _instanceMutex = new();

        static UnityCopilotPluginEditor instance = null!;

        public static bool HasInstance
        {
            get
            {
                lock (_instanceMutex)
                {
                    return instance != null;
                }
            }
        }
        public static UnityCopilotPluginEditor Instance
        {
            get
            {
                InitSingletonIfNeeded();
                lock (_instanceMutex)
                {
                    return instance;
                }
            }
        }

        public static void InitSingletonIfNeeded()
        {
            lock (_instanceMutex)
            {
                if (instance == null)
                {
                    instance = new UnityCopilotPluginEditor();
                    if (instance == null)
                    {
                        _logger.LogWarning("{method}: UnityCopilotPluginEditor instance is null",
                            nameof(InitSingletonIfNeeded));
                        return;
                    }
                }
            }
        }

        // Replaces McpPlugin.McpPlugin static singleton behavior
        private static readonly ReactiveProperty<IMcpPlugin?> _pluginProperty = new(null);
        public static ReadOnlyReactiveProperty<IMcpPlugin?> PluginProperty => _pluginProperty;
        public static IMcpPlugin? CurrentPlugin => _pluginProperty.Value;
        private static void SetCurrentPlugin(IMcpPlugin? plugin) => _pluginProperty.Value = plugin;

        public static LogLevel LogLevel
        {
            get => Instance.unityConnectionConfig.LogLevel;
            set
            {
                Instance.unityConnectionConfig.LogLevel = value;
                ApplyLogLevel(value);
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static string Host
        {
            get => Instance.unityConnectionConfig.Host;
            set
            {
                Instance.unityConnectionConfig.Host = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static string LocalHost
        {
            get => Instance.unityConnectionConfig.LocalHost;
            set
            {
                Instance.unityConnectionConfig.LocalHost = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static bool KeepConnected
        {
            get => Instance.unityConnectionConfig.KeepConnected;
            set
            {
                Instance.unityConnectionConfig.KeepConnected = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static bool KeepServerRunning
        {
            get => Instance.unityConnectionConfig.KeepServerRunning;
            set
            {
                Instance.unityConnectionConfig.KeepServerRunning = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static TransportMethod TransportMethod
        {
            get => Instance.unityConnectionConfig.TransportMethod;
            set
            {
                Instance.unityConnectionConfig.TransportMethod = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static int TimeoutMs
        {
            get => Instance.unityConnectionConfig.TimeoutMs;
            set
            {
                Instance.unityConnectionConfig.TimeoutMs = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static int Port
        {
            get
            {
                if (Uri.TryCreate(Host, UriKind.Absolute, out var uri) && uri.Port > 0 && uri.Port <= Consts.Hub.MaxPort)
                    return uri.Port;

                return GeneratePortFromDirectory();
            }
        }

        public static string? Token
        {
            get => Instance.unityConnectionConfig.Token;
            set
            {
                Instance.unityConnectionConfig.Token = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static AuthOption AuthOption
        {
            get => Instance.unityConnectionConfig.AuthOption;
            set
            {
                Instance.unityConnectionConfig.AuthOption = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static ConnectionMode ConnectionMode
        {
            get => Instance.unityConnectionConfig.ConnectionMode;
            set
            {
                Instance.unityConnectionConfig.ConnectionMode = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }

        // ── Safe-defaults (fail-closed) network gates ─────────────────────────────────
        // Static facades over UnityConnectionConfig.{AllowLanBind, AllowInsecureRemoteHttp,
        // ForceTokenWhenLanBind}. They route through NotifyChanged so the UI and
        // configurators re-render after a toggle flip. See UnityConnectionConfig for the
        // semantics and the migration notes about backward compatibility.
        public static bool AllowLanBind
        {
            get => Instance.unityConnectionConfig.AllowLanBind;
            set
            {
                Instance.unityConnectionConfig.AllowLanBind = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static bool AllowInsecureRemoteHttp
        {
            get => Instance.unityConnectionConfig.AllowInsecureRemoteHttp;
            set
            {
                Instance.unityConnectionConfig.AllowInsecureRemoteHttp = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static bool ForceTokenWhenLanBind
        {
            get => Instance.unityConnectionConfig.ForceTokenWhenLanBind;
            set
            {
                Instance.unityConnectionConfig.ForceTokenWhenLanBind = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }
        public static string CloudServerUrl => UnityCopilotPlugin.UnityConnectionConfig.CloudServerUrl;
        public static string? CloudToken
        {
            get => Instance.unityConnectionConfig.CloudToken;
            set
            {
                Instance.unityConnectionConfig.CloudToken = value;
                NotifyChanged(Instance.unityConnectionConfig);
            }
        }



        // 'new' is intentional: static dispatch on the subtype, instance logic lives in the base.
        public static new ReadOnlyReactiveProperty<ConnectionState> ConnectionState
            => ((UnityCopilotPlugin)Instance).ConnectionState;
        public static new ReadOnlyReactiveProperty<bool> IsConnected
            => ((UnityCopilotPlugin)Instance).IsConnected;

        public static new Task NotifyToolRequestCompleted(RequestToolCompletedData request, CancellationToken cancellationToken = default)
            => ((UnityCopilotPlugin)Instance).NotifyToolRequestCompleted(request, cancellationToken);

        public static IDisposable SubscribeOnChanged(Action<UnityConnectionConfig> action, bool invokeImmediately = true)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            var subscription = _onConfigChanged.Subscribe(action);
            if (invokeImmediately)
            {
                try { action(Instance.unityConnectionConfig); }
                catch (Exception e) { _logger.LogError(e, "{method}: exception invoking action immediately", nameof(SubscribeOnChanged)); }
            }
            return subscription;
        }

        public static new Task<bool> ConnectIfNeeded() => ((UnityCopilotPlugin)Instance).ConnectIfNeeded();

        public static new Task<bool> Connect() => ((UnityCopilotPlugin)Instance).Connect();

        // Disconnect() and DisconnectImmediate() are inherited from UnityCopilotPlugin base.

        public static void StaticDispose()
        {
            _logger.LogTrace("{method} called.", nameof(StaticDispose));

            lock (_instanceMutex)
            {
                instance?.Dispose();
                instance = null!;
            }
        }

        static void NotifyChanged(UnityConnectionConfig data)
        {
            try { _onConfigChanged.OnNext(data); }
            catch (Exception e) { _logger.LogError(e, "{method}: exception", nameof(NotifyChanged)); }
        }
    }
}
