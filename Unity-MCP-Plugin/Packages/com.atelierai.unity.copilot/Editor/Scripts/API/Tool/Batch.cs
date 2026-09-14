/*
 * Design inspired by MCP for Unity (CoplayDev/unity-mcp)'s batch_execute tool,
 * Copyright (c) Coplay Inc., licensed under the MIT License.
 * Original: https://github.com/CoplayDev/unity-mcp/blob/main/Server/src/services/tools/batch_execute.py
 * This is a from-scratch C# implementation using the plugin's tool dispatch API.
 */

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
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using com.AtelierAI.Uco.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    /// <summary>
    /// Batch dispatcher that lets an LLM execute multiple tools in a single
    /// server round-trip. Reduces LLM-to-Unity latency by amortizing transport
    /// overhead across many tool calls.
    /// </summary>
    [UcoToolType]
    public partial class Tool_Batch
    {
        /// <summary>
        /// Default per-call ceiling on the number of commands. Beyond this the
        /// tool throws and asks the caller to split the batch.
        /// </summary>
        public const int DefaultMaxCommands = 25;

        /// <summary>
        /// Absolute upper bound on the number of commands per batch, regardless
        /// of caller intent. Guards against pathologically large batches that
        /// would tie up the main thread.
        /// </summary>
        public const int HardMaxCommands = 100;

        public static class Error
        {
            public static string CommandsNullOrEmpty()
                => "commands is null or empty. Provide at least one BatchCommand.";

            public static string TooManyCommands(int count)
                => $"commands.Length={count} exceeds the hard maximum of {HardMaxCommands}. " +
                   $"Split the work into multiple batch-execute calls (recommended batch size: <= {DefaultMaxCommands}).";

            public static string CommandToolEmpty(int index)
                => $"commands[{index}].tool is null or empty. Each BatchCommand must specify a non-empty tool id.";

            public static string CommandNull(int index)
                => $"commands[{index}] is null. Each entry must be a populated BatchCommand object.";

            public static string NestedBatchNotAllowed()
                => "batch-execute cannot be nested. A BatchCommand cannot itself invoke batch-execute.";

            public static string UnknownTool(string toolName)
                => $"Unknown tool: '{toolName}'. Use 'unity-tool-list' to enumerate available tools.";

            public static string ToolManagerNotAvailable()
                => "Tool manager is not available. UnityCopilotPluginEditor may not be initialized.";
        }

        /// <summary>
        /// One entry in a batch-execute submission: a tool id plus the parameters
        /// to pass to that tool.
        /// </summary>
        [Description("A single tool invocation inside a batch-execute call. " +
            "Specifies which tool to call and the parameters object to pass to it.")]
        public class BatchCommand
        {
            [Description("tool id, e.g. 'gameobject-create'. Must match an existing tool's id.")]
            public string Tool { get; set; } = string.Empty;

            [Description("Parameters object passed to the tool. Keys match the tool's input schema.")]
            public Dictionary<string, JsonElement> Params { get; set; } = new();
        }

        /// <summary>
        /// Aggregated result of one batch-execute invocation. Mirrors the input
        /// array via <see cref="Results"/> while exposing summary counters for
        /// quick inspection by the caller.
        /// </summary>
        [Description("Aggregated result of a batch-execute call. " +
            "Contains per-command outcomes plus summary counters.")]
        public class BatchResult
        {
            [Description("Echo of total command count submitted.")]
            public int TotalCommands { get; set; }

            [Description("Number of commands that completed without error.")]
            public int Succeeded { get; set; }

            [Description("Number of commands that returned an error.")]
            public int Failed { get; set; }

            [Description("True when execution was aborted early due to failFast=true on first failure.")]
            public bool Aborted { get; set; }

            [Description("True when commands ran in parallel; false for sequential.")]
            public bool RanInParallel { get; set; }

            [Description("Per-command results in input order. When Aborted=true, later commands have Ok=false+Skipped=true.")]
            public BatchCommandResult[] Results { get; set; } = Array.Empty<BatchCommandResult>();

            [Description("Optional human-readable note about the batch run as a whole (e.g. parallel-downgrade reason).")]
            public string? Note { get; set; }
        }

        /// <summary>
        /// Outcome of a single command inside a batch-execute call. Data normally
        /// contains a successful result payload. When a shared parent abort cannot
        /// prove complete rollback, a failed prior child may retain bounded data
        /// alongside its error for diagnosis and recovery.
        /// </summary>
        [Description("Result of a single command inside a batch-execute call.")]
        public class BatchCommandResult
        {
            [Description("Tool id that was called.")]
            public string Tool { get; set; } = string.Empty;

            [Description("True if the command completed successfully.")]
            public bool Ok { get; set; }

            [Description("True if the command was skipped due to failFast abort.")]
            public bool Skipped { get; set; }

            [Description("Error message when Ok=false.")]
            public string? Error { get; set; }

            [Description("Stable safety/error code when the command was rejected before execution.")]
            public string? ErrorCode { get; set; }

            [Description("Result payload, typically the JSON-stringified output of the underlying tool. " +
                "A failed prior child may retain a bounded payload when shared rollback leaves final mutation state uncertain.")]
            public object? Data { get; set; }

            [Description("Optional bounded authoring transaction report returned by this child.")]
            public JsonObject? Transaction { get; set; }
        }
    }
}
