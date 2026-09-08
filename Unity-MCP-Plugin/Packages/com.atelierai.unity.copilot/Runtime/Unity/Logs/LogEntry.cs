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
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot
{
    public class LogEntry
    {
        /// <summary>Source classification of the emitter (COCli-07).</summary>
        public const string SourceProduct = "product";
        public const string SourceBridge = "bridge";
        public const string SourceTool = "tool";
        public const string SourceUnity = "unity";

        public LogType LogType { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
        public string? StackTrace { get; set; }

        /// <summary>Correlation id of the tool call active at emission, when known.</summary>
        public string? CorrelationId { get; set; }

        /// <summary>Operation id of the durable operation active at emission, when known.</summary>
        public string? OperationId { get; set; }

        /// <summary>One of product / bridge / tool / unity; defaults to unity.</summary>
        public string Source { get; set; } = SourceUnity;

        public LogEntry()
        {
            LogType = LogType.Log;
            Message = string.Empty;
            Timestamp = DateTime.Now;
            StackTrace = null;
        }
        public LogEntry(LogType logType, string message)
        {
            LogType = logType;
            Message = message;
            Timestamp = DateTime.Now;
            StackTrace = null;
        }
        public LogEntry(LogType logType, string message, string? stackTrace = null)
        {
            LogType = logType;
            Message = message;
            Timestamp = DateTime.Now;
            StackTrace = string.IsNullOrEmpty(stackTrace) ? null : stackTrace;
        }
        public LogEntry(LogType logType, string message, DateTime timestamp, string? stackTrace = null)
        {
            LogType = logType;
            Message = message;
            Timestamp = timestamp;
            StackTrace = string.IsNullOrEmpty(stackTrace) ? null : stackTrace;
        }
        public LogEntry(
            LogType logType,
            string message,
            string source,
            string? correlationId,
            string? operationId,
            DateTime timestamp,
            string? stackTrace = null)
        {
            LogType = logType;
            Message = message;
            Source = string.IsNullOrEmpty(source) ? SourceUnity : source;
            CorrelationId = string.IsNullOrEmpty(correlationId) ? null : correlationId;
            OperationId = string.IsNullOrEmpty(operationId) ? null : operationId;
            Timestamp = timestamp;
            StackTrace = string.IsNullOrEmpty(stackTrace) ? null : stackTrace;
        }

        public override string ToString() => ToString(includeStackTrace: false);

        public string ToString(bool includeStackTrace)
        {
            var attribution = string.IsNullOrEmpty(CorrelationId) && string.IsNullOrEmpty(OperationId)
                ? string.Empty
                : $" ({Source}; {(string.IsNullOrEmpty(CorrelationId) ? "-" : CorrelationId)}" +
                  $"{(string.IsNullOrEmpty(OperationId) ? string.Empty : "/" + OperationId)})";
            return includeStackTrace && !string.IsNullOrEmpty(StackTrace)
                ? $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LogType}]{attribution} {Message}\nStack Trace:\n{StackTrace}"
                : $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LogType}]{attribution} {Message}";
        }
    }
}

