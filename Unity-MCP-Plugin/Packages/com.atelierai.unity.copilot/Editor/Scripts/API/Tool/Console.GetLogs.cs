/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak/Unity-MCP)    │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)     │
│  Copyright (c) 2025 Ivan Murzak                                   │
│  Licensed under the Apache License, Version 2.0.                  │
│  See the LICENSE file in the project root for more information.   │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.ComponentModel;
using com.AtelierAI.Uco.Framework;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Console
    {
        public const string ConsoleGetLogsToolId = "console-get-logs";
        [UcoTool
        (
            ConsoleGetLogsToolId,
            Title = "Console / Get Logs",
            ReadOnlyHint = true,
            IdempotentHint = true
        )]
        [UcoSkillDescription("Retrieve Unity Editor logs from the MCP plugin's `LogCollector`, " +
            "optionally filtered by log type, time window, operation/correlation identity, or source " +
            "(product / bridge / tool / unity). Useful for debugging and monitoring Editor activity.")]
        [UcoSkillBody("Retrieves Unity Editor logs with attribution and loss accounting.\n\n" +
            "## Inputs\n\n" +
            "- `maxEntries` (default 100, minimum 1) — caps the size of the returned array.\n" +
            "- `logTypeFilter` — Unity `LogType` filter; `null` returns all severities.\n" +
            "- `includeStackTrace` (default `false`) — include stack-trace strings in each entry.\n" +
            "- `lastMinutes` (default 0) — when non-zero, only logs from the last N minutes are returned.\n" +
            "- `correlationId` — only entries emitted inside that tool call's execution window.\n" +
            "- `operationId` — only entries attributed to that durable operation.\n" +
            "- `source` — one of `product`, `bridge`, `tool`, `unity`.\n" +
            "- `sinceUnixMs` — only entries at or after this Unix millisecond boundary.\n\n" +
            "## Attribution and loss accounting\n\n" +
            "Each entry carries `Source` and, when produced on the main thread inside an owned execution " +
            "window, `CorrelationId`/`OperationId`. The response reports `DroppedEntries` (lost to " +
            "capacity or storage failures) and `TruncatedEntries` (messages cut at the size limit) so an " +
            "empty result is distinguishable from a clean run.\n\n" +
            "## Example: new errors for one operation\n\n" +
            "`{ \"operationId\": \"<from tests-run>\", \"logTypeFilter\": \"Error\", \"sinceUnixMs\": 1760000000000, \"maxEntries\": 50 }`")]
        [Description("Retrieves Unity Editor logs with correlation/source attribution and loss accounting. " +
            "Useful for debugging and monitoring Unity Editor activity.")]
        public ConsoleLogsQueryResult GetLogs
        (
            [Description("Maximum number of log entries to return. Minimum: 1. Default: 100")]
            int maxEntries = 100,
            [Description("Filter by log type. 'null' means All.")]
            LogType? logTypeFilter = null,
            [Description("Include stack traces in the output. Default: false")]
            bool includeStackTrace = false,
            [Description("Return logs from the last N minutes. If 0, returns all available logs. Default: 0")]
            int lastMinutes = 0,
            [Description("Only entries emitted inside this tool call's execution window (correlation identity). Example: 'call-...'.")]
            string? correlationId = null,
            [Description("Only entries attributed to this durable operation id. Example: the operationId returned by tests-run.")]
            string? operationId = null,
            [Description("Source classification filter: 'product', 'bridge', 'tool', or 'unity'. 'null' returns all sources.")]
            string? source = null,
            [Description("Only entries at or after this Unix-epoch millisecond boundary. Record the boundary before an operation, then filter new entries here. Example: 1760000000000.")]
            long? sinceUnixMs = null
        )
        {
            // Validate parameters
            if (maxEntries < 1)
                throw new ArgumentException(Error.InvalidMaxEntries(maxEntries));

            if (!UnityCopilotPluginEditor.HasInstance)
                throw new InvalidOperationException("UnityCopilotPluginEditor is not initialized.");

            var logCollector = UnityCopilotPluginEditor.Instance.LogCollector;
            if (logCollector == null)
                throw new InvalidOperationException("LogCollector is not initialized.");

            var logs = logCollector.QueryDetailed(
                maxEntries: maxEntries,
                logTypeFilter: logTypeFilter,
                includeStackTrace: includeStackTrace,
                lastMinutes: lastMinutes,
                correlationId: correlationId,
                operationId: operationId,
                source: source,
                sinceUnixMs: sinceUnixMs
            );

            return logs;
        }
    }
}