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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.ReflectorNet;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot
{
    /// <summary>
    /// Result of a detailed console query: the filtered entries plus the
    /// collector's loss accounting so "no errors" is distinguishable from
    /// "errors were dropped" (COCli-07).
    /// </summary>
    public class ConsoleLogsQueryResult
    {
        public LogEntry[] Entries { get; set; } = Array.Empty<LogEntry>();
        public int DroppedEntries { get; set; }
        public int TruncatedEntries { get; set; }
    }

    /// <summary>
    /// Collects Unity log messages and manages saving/loading them to/from a cache file.
    /// </summary>
    public class UnityLogCollector : IDisposable
    {
        private static readonly Regex RichTextRegex = new Regex(@"</?(b|i|size|color|material|quad|a)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        const int MaxMessageCharacters = 16 * 1024;

        readonly ILogStorage _logStorage;
        readonly ThreadSafeBool _isDisposed = new(false);
        // This collector's channel sink and the sink it displaced, so Dispose
        // can restore the previous channel owner instead of clearing it.
        readonly Action<PluginDiagnosticEntry> _channelSink;
        Action<PluginDiagnosticEntry>? _previousChannelSink;
        int _droppedEntries;
        int _truncatedEntries;

        public UnityLogCollector(ILogStorage logStorage)
        {
            if (!MainThread.Instance.IsMainThread)
                throw new Exception($"{GetType().GetTypeShortName()} constructor must be initialized on the main thread.");

            _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));

            _channelSink = AppendPluginDiagnostic;
            _previousChannelSink = PluginDiagnostics.CurrentSink;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;
            PluginDiagnostics.Install(_channelSink);
        }

        public LogClearResult Clear()
        {
            if (_isDisposed.Value)
                return new LogClearResult
                {
                    Ok = false,
                    Strategy = "collector-disposed",
                    Error = "The Unity log collector is disposed."
                };

            return _logStorage.Clear();
        }

        /// <summary>
        /// Synchronously saves all current log entries to the cache file.
        /// </summary>
        public void Save()
        {
            if (_isDisposed.Value)
                return;

            _logStorage.Flush();
        }

        /// <summary>
        /// Asynchronously saves all current log entries to the cache file.
        /// </summary>
        /// <returns>A task that completes when the save operation is finished.</returns>
        public Task SaveAsync()
        {
            if (_isDisposed.Value)
                return Task.CompletedTask;

            return _logStorage.FlushAsync();
        }

        public Task<LogEntry[]> QueryAsync(
            int maxEntries = 100,
            LogType? logTypeFilter = null,
            bool includeStackTrace = false,
            int lastMinutes = 0)
        {
            return _logStorage.QueryAsync(maxEntries, logTypeFilter, includeStackTrace, lastMinutes);
        }

        public LogEntry[] Query(
            int maxEntries = 100,
            LogType? logTypeFilter = null,
            bool includeStackTrace = false,
            int lastMinutes = 0)
        {
            return _logStorage.Query(maxEntries, logTypeFilter, includeStackTrace, lastMinutes);
        }

        /// <summary>
        /// Detailed query with operation/correlation/source/time-boundary
        /// filters and loss accounting (COCli-07). Existing filter names and
        /// semantics are unchanged; the new filters compose with them.
        /// </summary>
        public ConsoleLogsQueryResult QueryDetailed(
            int maxEntries = 100,
            LogType? logTypeFilter = null,
            bool includeStackTrace = false,
            int lastMinutes = 0,
            string? correlationId = null,
            string? operationId = null,
            string? source = null,
            long? sinceUnixMs = null)
        {
            if (maxEntries < 1)
                throw new ArgumentOutOfRangeException(nameof(maxEntries));
            if (!string.IsNullOrEmpty(source)
                && source != LogEntry.SourceProduct
                && source != LogEntry.SourceBridge
                && source != LogEntry.SourceTool
                && source != LogEntry.SourceUnity)
            {
                throw new ArgumentException(
                    $"source must be one of: {LogEntry.SourceProduct}, {LogEntry.SourceBridge}, {LogEntry.SourceTool}, {LogEntry.SourceUnity}.");
            }

            // Oversubscribe the storage query so the id/source filters below
            // can still fill maxEntries from a wider recent window.
            var fetchLimit = string.IsNullOrEmpty(correlationId) && string.IsNullOrEmpty(operationId)
                ? maxEntries
                : Math.Min(2_000, maxEntries * 10);
            var candidates = _logStorage.Query(
                fetchLimit, logTypeFilter, includeStackTrace, lastMinutes);

            var results = new System.Collections.Generic.List<LogEntry>(candidates.Length);
            var boundary = sinceUnixMs.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(sinceUnixMs.Value).LocalDateTime
                : (DateTime?)null;
            // Storage returns newest-first; keep the newest maxEntries after
            // filtering, then present them oldest-first for readability.
            for (var i = 0; i < candidates.Length && results.Count < maxEntries; i++)
            {
                var entry = candidates[i];
                if (!string.IsNullOrEmpty(correlationId)
                    && !string.Equals(entry.CorrelationId, correlationId, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(operationId)
                    && !string.Equals(entry.OperationId, operationId, StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(source)
                    && !string.Equals(entry.Source, source, StringComparison.Ordinal))
                    continue;
                if (boundary.HasValue && entry.Timestamp < boundary.Value)
                    continue;
                results.Add(entry);
            }
            results.Reverse();

            return new ConsoleLogsQueryResult
            {
                Entries = results.ToArray(),
                DroppedEntries = Volatile.Read(ref _droppedEntries) + PluginDiagnostics.DroppedEntries,
                TruncatedEntries = Volatile.Read(ref _truncatedEntries),
            };
        }

        /// <summary>Sink for the plugin/bridge/tool diagnostics channel.</summary>
        void AppendPluginDiagnostic(PluginDiagnosticEntry entry)
        {
            if (_isDisposed.Value || entry == null) return;
            try
            {
                _logStorage.Append(new LogEntry(
                    logType: entry.LogType,
                    message: BoundMessage(entry.Message, out var truncated),
                    source: entry.Source,
                    correlationId: entry.CorrelationId,
                    operationId: entry.OperationId,
                    timestamp: DateTime.Now,
                    stackTrace: entry.StackTrace));
                if (truncated)
                    Interlocked.Increment(ref _truncatedEntries);
            }
            catch
            {
                Interlocked.Increment(ref _droppedEntries);
            }
        }

        void OnLogMessageReceived(string message, string stackTrace, LogType type)
        {
            try
            {
                // Strip rich text tags
                var cleanMessage = BoundMessage(RichTextRegex.Replace(message, string.Empty), out var truncated);

                // COCli-07 attribution: entries produced on the main thread
                // inside an owned execution window carry that call's ids and
                // classify as product; everything Unity forwards otherwise
                // stays source=unity with empty correlation fields.
                string? correlationId = null;
                string? operationId = null;
                var source = LogEntry.SourceUnity;
                if (MainThread.Instance.IsMainThread && LogCallScope.Current is { } frame)
                {
                    correlationId = frame.CorrelationId;
                    operationId = frame.OperationId;
                    source = LogEntry.SourceProduct;
                }

                _logStorage.Append(new LogEntry(
                    logType: type,
                    message: cleanMessage,
                    source: source,
                    correlationId: correlationId,
                    operationId: operationId,
                    timestamp: DateTime.Now,
                    stackTrace: string.IsNullOrEmpty(stackTrace) ? null : stackTrace));
                if (truncated)
                    Interlocked.Increment(ref _truncatedEntries);
            }
            catch
            {
                Interlocked.Increment(ref _droppedEntries);
            }
        }

        string BoundMessage(string message, out bool truncated)
        {
            if (message.Length <= MaxMessageCharacters)
            {
                truncated = false;
                return message;
            }
            truncated = true;
            return message.Substring(0, MaxMessageCharacters);
        }

        public void Dispose()
        {
            if (!_isDisposed.TrySetTrue())
                return; // already disposed

            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            // Hand the channel back to whatever sink was installed before this
            // collector (e.g. the plugin's own collector) instead of clearing
            // it: a temporary collector must not disable console separation for
            // the rest of the session. A newer install is left untouched.
            PluginDiagnostics.RestoreIfCurrent(_channelSink, _previousChannelSink);
            _previousChannelSink = null;
            _logStorage.Dispose();

            GC.SuppressFinalize(this);
        }

        ~UnityLogCollector() => Dispose();
    }
}
