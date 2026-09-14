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
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public partial class Tool_Batch
    {
        public const string BatchExecuteToolId = "batch-execute";

        [McpPluginTool
        (
            BatchExecuteToolId,
            Title = "Batch Execute",
            DestructiveHint = true
        )]
        [McpPluginSkillDescription("Execute multiple tool calls in a single SignalR round-trip. " +
            "Drastically reduces LLM-to-Unity latency by amortizing transport overhead across many calls. " +
            "Use this when issuing several independent tool calls in sequence (e.g. create N GameObjects, " +
            "modify several components, query a handful of assets).")]
        [McpPluginSkillBody("Dispatches a list of `BatchCommand`s through the plugin's internal tool runner, " +
            "saving one network hop per command compared to issuing each tool call individually.\n\n" +
            "## Inputs\n\n" +
            "- `commands` — array of `{ tool, params }` entries. `tool` is the tool id " +
            "(e.g. `gameobject-create`); `params` is the parameters object that tool expects. " +
            "Hard maximum 100 entries; default soft limit 25 — split larger workloads.\n" +
            "- `failFast` (default `true`) — abort the batch after the first failing command. " +
            "Remaining commands appear in the result with `ok=false` + `skipped=true`.\n" +
            "- `parallel` (default `false`) — opt into parallel dispatch when all commands are safe. " +
            "Because the plugin's `IRunTool` does not expose read-only hints to this code path, the current " +
            "implementation conservatively downgrades to sequential and reports it via `BatchResult.RanInParallel=false` " +
            "and a `Note` field. Once the underlying surface exposes the hint we can flip to true parallel.\n" +
            "- `maxParallelism` (default 4) — hint for the maximum worker count when parallel dispatch is used. " +
            "Ignored under the current sequential-downgrade behavior.\n\n" +
            "## Output\n\n" +
            "Returns a `BatchResult` with `TotalCommands`, `Succeeded`, `Failed`, `Aborted`, `RanInParallel`, " +
            "an optional `Note`, plus a `Results` array carrying per-command `{ Tool, Ok, Skipped, Error, Data }`.\n\n" +
            "## Notes\n\n" +
            "- `batch-execute` cannot be nested. A command whose `tool` is `batch-execute` fails immediately " +
            "with an error, without aborting the batch unless `failFast=true`.\n" +
            "- Unknown tool ids fail the individual command but do not throw — they are reflected in the per-command result.\n" +
            "- Each command is dispatched via `UnityCopilotPluginEditor.Instance.Tools.RunCallTool(...)`, which itself " +
            "marshals to the Unity main thread as needed. Batch-execute does not wrap commands in additional " +
            "main-thread dispatch.")]
        [Description("Execute multiple tool calls in a single SignalR round-trip. " +
            "Drastically reduces LLM-to-Unity latency by amortizing transport overhead across many calls.")]
        public async Task<BatchResult> Execute
        (
            [Description("Array of commands. Each must have 'tool' (string) and 'params' (object). " +
                "Max " + nameof(DefaultMaxCommands) + " (25) recommended; hard max " + nameof(HardMaxCommands) + " (100).")]
            BatchCommand[] commands,

            [Description("Stop after first failure. Default true.")]
            bool failFast = true,

            [Description("Run read-only commands in parallel. Falls back to sequential if any command is destructive " +
                "or the read-only hint is not exposed by the underlying tool surface. Default false.")]
            bool parallel = false,

            [Description("Hint for max parallel workers when parallel=true. Default 4. Ignored when parallel=false " +
                "or when the implementation falls back to sequential.")]
            int maxParallelism = 4
        )
        {
            // ------ Input validation ------
            if (commands == null || commands.Length == 0)
                throw new ArgumentException(Error.CommandsNullOrEmpty(), nameof(commands));

            if (commands.Length > HardMaxCommands)
                throw new ArgumentException(Error.TooManyCommands(commands.Length), nameof(commands));

            if (maxParallelism < 1)
                maxParallelism = 1;

            // Resolve tool manager once. We need it to (a) reject unknown tools per-command
            // and (b) reject nested batch-execute. We do not throw here if it is null —
            // unknown-tool errors surface per-command, so callers see partial progress.
            var toolManager = UnityCopilotPluginEditor.HasInstance
                ? UnityCopilotPluginEditor.Instance.Tools
                : null;
            if (toolManager == null)
                throw new InvalidOperationException(Error.ToolManagerNotAvailable());

            // Pre-build a name set so we can validate each command without re-enumerating
            // the manager per command. Case-sensitive: tool ids are kebab-case and the
            // protocol treats them as exact strings.
            var knownTools = new HashSet<string>(StringComparer.Ordinal);
            var allTools = toolManager.GetAllTools();
            if (allTools != null)
            {
                foreach (var tool in allTools)
                {
                    if (!string.IsNullOrEmpty(tool?.Name))
                        knownTools.Add(tool!.Name);
                }
            }

            var results = new BatchCommandResult[commands.Length];
            var aborted = false;
            var succeeded = 0;
            var failed = 0;

            // ------ Parallel mode policy ------
            // The plugin's IRunTool surface does not currently expose a ReadOnlyHint
            // accessor that we can read from here. Until it does, we cannot safely
            // prove a batch is destruction-free, so we conservatively downgrade to
            // sequential and report it in the result. This matches the spec's
            // "silent downgrade + reflect in result" requirement.
            var ranInParallel = false;
            string? note = null;
            if (parallel)
            {
                note = "parallel=true requested but downgraded to sequential: " +
                       "IRunTool does not expose ReadOnlyHint to this dispatch path. " +
                       "Sequential execution preserves safety for destructive commands.";
            }

            // ------ Sequential dispatch loop ------
            for (int i = 0; i < commands.Length; i++)
            {
                var cmd = commands[i];
                var perCmd = new BatchCommandResult
                {
                    Tool = cmd?.Tool ?? string.Empty
                };

                // Per-command validation. None of these throw — they all surface as
                // failing BatchCommandResults so the caller sees partial progress.
                if (cmd == null)
                {
                    perCmd.Ok = false;
                    perCmd.Error = Error.CommandNull(i);
                    results[i] = perCmd;
                    failed++;
                    if (failFast)
                    {
                        aborted = true;
                        FillSkipped(commands, results, i + 1);
                        break;
                    }
                    continue;
                }

                if (string.IsNullOrWhiteSpace(cmd.Tool))
                {
                    perCmd.Ok = false;
                    perCmd.Error = Error.CommandToolEmpty(i);
                    results[i] = perCmd;
                    failed++;
                    if (failFast)
                    {
                        aborted = true;
                        FillSkipped(commands, results, i + 1);
                        break;
                    }
                    continue;
                }

                if (string.Equals(cmd.Tool, BatchExecuteToolId, StringComparison.Ordinal))
                {
                    perCmd.Ok = false;
                    perCmd.Error = Error.NestedBatchNotAllowed();
                    results[i] = perCmd;
                    failed++;
                    if (failFast)
                    {
                        aborted = true;
                        FillSkipped(commands, results, i + 1);
                        break;
                    }
                    continue;
                }

                if (!knownTools.Contains(cmd.Tool))
                {
                    perCmd.Ok = false;
                    perCmd.Error = Error.UnknownTool(cmd.Tool);
                    results[i] = perCmd;
                    failed++;
                    if (failFast)
                    {
                        aborted = true;
                        FillSkipped(commands, results, i + 1);
                        break;
                    }
                    continue;
                }

                // ------ Actual dispatch ------
                try
                {
                    // Params may be null when the JSON deserializer drops the property.
                    // RunCallTool requires a non-null parameters dictionary.
                    var parameters = cmd.Params ?? new Dictionary<string, System.Text.Json.JsonElement>();
                    var request = new RequestCallTool(cmd.Tool, parameters);
                    var response = await toolManager.RunCallTool(request).ConfigureAwait(false);

                    if (response == null)
                    {
                        perCmd.Ok = false;
                        perCmd.Error = "Tool runner returned null response.";
                    }
                    else if (response.Status == ResponseStatus.Error)
                    {
                        perCmd.Ok = false;
                        // Prefer the inner ResponseCallTool message when available;
                        // fall back to the outer envelope message.
                        perCmd.Error = response.Value?.Status == ResponseStatus.Error
                            ? (response.Value?.GetMessage() ?? response.Message)
                            : response.Message;
                    }
                    else if (response.Value?.Status == ResponseStatus.Error)
                    {
                        perCmd.Ok = false;
                        perCmd.Error = response.Value.GetMessage() ?? response.Message;
                    }
                    else
                    {
                        perCmd.Ok = true;
                        // Capture the structured/string payload. ResponseCallTool wraps the
                        // tool output as a Message string (JSON-encoded for object returns).
                        // We pass it through verbatim so the caller can re-parse if needed.
                        perCmd.Data = response.Value?.GetMessage() ?? response.Message;
                    }
                }
                catch (OperationCanceledException)
                {
                    perCmd.Ok = false;
                    perCmd.Error = "Cancelled";
                }
                catch (Exception ex)
                {
                    perCmd.Ok = false;
                    perCmd.Error = ex.Message;
                }

                results[i] = perCmd;
                if (perCmd.Ok)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                    if (failFast)
                    {
                        aborted = true;
                        FillSkipped(commands, results, i + 1);
                        break;
                    }
                }
            }

            return new BatchResult
            {
                TotalCommands = commands.Length,
                Succeeded = succeeded,
                Failed = failed,
                Aborted = aborted,
                RanInParallel = ranInParallel,
                Results = results,
                Note = note
            };
        }

        /// <summary>
        /// Marks every command from <paramref name="startIndex"/> onward as skipped.
        /// Used when <c>failFast</c> aborts the batch — the caller still sees one
        /// entry per submitted command, so positional indexing remains intact.
        /// </summary>
        static void FillSkipped(BatchCommand[] commands, BatchCommandResult[] results, int startIndex)
        {
            for (int j = startIndex; j < commands.Length; j++)
            {
                results[j] = new BatchCommandResult
                {
                    Tool = commands[j]?.Tool ?? string.Empty,
                    Ok = false,
                    Skipped = true,
                    Error = "Skipped due to failFast abort on an earlier command."
                };
            }
        }
    }
}
