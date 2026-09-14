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
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Unity.Copilot.Utils
{
    using LogLevel = Runtime.Utils.LogLevel;
    using LogLevelMicrosoft = Microsoft.Extensions.Logging.LogLevel;

    public class UnityLogger : ILogger
    {
        readonly string _categoryName;
        readonly string _categoryColor;

        // Colors that look good on dark gray background
        private static readonly string[] CategoryColors = new[]
        {
            "#FF6B9D", // Pink
            "#4ECDC4", // Teal
            "#FFD93D", // Yellow
            "#95E1D3", // Mint
            "#F38181", // Coral
            "#AA96DA", // Purple
            "#6BCF7F", // Green
            "#5DADE2", // Blue
            "#F8B739", // Orange
            "#EC7063", // Red
            "#48C9B0", // Turquoise
            "#AF7AC5", // Violet
            "#58D68D", // Lime
            "#F5B041", // Amber
            "#85C1E2"  // Sky Blue
        };

        public UnityLogger(string categoryName)
        {
            _categoryName = categoryName.Contains('.')
                ? categoryName.Substring(categoryName.LastIndexOf('.') + 1)
                : categoryName;

            // Use hashcode to consistently select a color for this category
            var colorIndex = Math.Abs(categoryName.GetHashCode()) % CategoryColors.Length;
            _categoryColor = CategoryColors[colorIndex];
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;

        public bool IsEnabled(LogLevelMicrosoft logLevel)
        {
            // Prevent infinite loop during UnityCopilotPlugin initialization by checking if any instance exists
            if (!UnityCopilotPlugin.HasAnyInstance)
            {
                // During initialization, allow all log levels
                return true;
            }

            return UnityCopilotPlugin.IsLogEnabled(logLevel switch
            {
                LogLevelMicrosoft.Critical => LogLevel.Exception,
                LogLevelMicrosoft.Error => LogLevel.Error,
                LogLevelMicrosoft.Warning => LogLevel.Warning,
                LogLevelMicrosoft.Information => LogLevel.Info,
                LogLevelMicrosoft.Debug => LogLevel.Debug,
                LogLevelMicrosoft.Trace => LogLevel.Trace,
                _ => LogLevel.None
            });
        }

        public void Log<TState>(LogLevelMicrosoft logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            if (formatter == null) throw new ArgumentNullException(nameof(formatter));
            if (state == null) throw new ArgumentNullException(nameof(state));

            // Map LogLevel to short names
#if UNITY_EDITOR
            string logLevelShort = logLevel switch
            {
                LogLevelMicrosoft.Critical => "<color=#ff0000>crit:</color>",
                LogLevelMicrosoft.Error => "<color=#ff6b6b>fail:</color>",
                LogLevelMicrosoft.Warning => "<color=#ffaa00>warn:</color>",
                LogLevelMicrosoft.Information => "<color=#00ff00>info:</color>",
                LogLevelMicrosoft.Debug => "<color=#00ffff>dbug:</color>",
                LogLevelMicrosoft.Trace => "<color=#aaaaaa>trce:</color>",
                _ => "<color=#ffffff>none</color>"
            };
            var message = $"{logLevelShort} [{DateTime.Now:HH:mm:ss:ffff}] <color=#B4FF32>[AI]</color> <color={_categoryColor}><b>{_categoryName}</b></color> {formatter(state, exception)}";
#else
            string logLevelShort = logLevel switch
            {
                LogLevelMicrosoft.Critical => "crit:",
                LogLevelMicrosoft.Error => "fail:",
                LogLevelMicrosoft.Warning => "warn:",
                LogLevelMicrosoft.Information => "info:",
                LogLevelMicrosoft.Debug => "dbug:",
                LogLevelMicrosoft.Trace => "trce:",
                _ => "none"
            };
            var message = $"{logLevelShort} [{DateTime.Now:HH:mm:ss:ffff}] [AI] {_categoryName} {formatter(state, exception)}";
#endif

#if UNITY_EDITOR
            // COCli-07: plugin/bridge/tool diagnostics go to the collector-only
            // channel by default so the Editor Console stays reserved for
            // product and Unity output. Without a sink (pre-initialization)
            // the channel keeps the legacy console behavior.
            if (PluginDiagnostics.HasSink)
            {
                var exceptionText = exception == null
                    ? null
                    : exception.GetType().Name + ": " + exception.Message;
                PluginDiagnostics.Append(
                    source: ClassifySource(_categoryName),
                    logType: logLevel switch
                    {
                        LogLevelMicrosoft.Critical => UnityEngine.LogType.Exception,
                        LogLevelMicrosoft.Error => UnityEngine.LogType.Error,
                        LogLevelMicrosoft.Warning => UnityEngine.LogType.Warning,
                        _ => UnityEngine.LogType.Log,
                    },
                    message: message,
                    stackTrace: exceptionText);
                return;
            }
#endif

            switch (logLevel)
            {
                case LogLevelMicrosoft.Critical:
                case LogLevelMicrosoft.Error:
                    if (exception != null)
                        UnityEngine.Debug.LogException(exception);
                    UnityEngine.Debug.LogError(message);
                    break;

                case LogLevelMicrosoft.Warning:
                    if (exception != null)
                        UnityEngine.Debug.LogWarning(exception);
                    UnityEngine.Debug.LogWarning(message);
                    break;

                default:
                    if (exception != null)
                        UnityEngine.Debug.Log(exception);
                    UnityEngine.Debug.Log(message);
                    break;
            }
        }

        /// <summary>
        /// Emission-site source classification: categories under a tool's
        /// wrapper (e.g. <c>Tool_Script.Execute</c>) are tool diagnostics;
        /// everything else the plugin emits is bridge infrastructure.
        /// </summary>
        static string ClassifySource(string categoryName)
            => categoryName.StartsWith("Tool_", StringComparison.Ordinal)
                ? LogEntry.SourceTool
                : LogEntry.SourceBridge;
    }
}
