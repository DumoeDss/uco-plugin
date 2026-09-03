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
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.AtelierAI.Unity.Copilot.Editor.Utils;

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
        [AuthoringCapability(
            MutationKind = AuthoringMutationKind.Modify,
            UndoLevel = AuthoringUndoLevel.Full,
            SupportsValidation = true,
            SupportsPlanning = true,
            ValidatorType = typeof(UnityBatchAuthoringValidator),
            PlannerType = typeof(UnityBatchAuthoringPlanner),
            TransactionFactoryType = typeof(UnityAuthoringTransactionFactory))]
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
            int maxParallelism = 4,

            [ToolCallContext]
            ToolCallContext? context = null
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

            // The pipeline injects the parent context for normal calls. Keep a
            // generated root context for direct/reflected callers that invoke
            // this method without going through McpToolManager.
            var parentContext = context;
            if (parentContext == null)
            {
                var parentRequestId = Guid.NewGuid().ToString();
                parentContext = ToolCallContextNormalizer.Normalize(
                    new RequestCallTool(
                        parentRequestId,
                        BatchExecuteToolId,
                        new Dictionary<string, System.Text.Json.JsonElement>())).Context;
            }

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

            // The outer middleware hands the exact consumed aggregate record
            // to the runner. Validate every child authority and current
            // read-only binding before dispatching the first child, so a stale
            // later command cannot leave an earlier mutation behind.
            ThrowIfStopped(parentContext);
            var approvedChildren = ToolCallInvocationScope.CurrentInvocation?
                .ApprovedPlan?.ChildRecords;
            var preflight = PrepareApprovedChildren(
                commands,
                knownTools,
                toolManager,
                parentContext,
                approvedChildren,
                failFast);
            if (!preflight.Succeeded)
            {
                for (var index = 0; index < commands.Length; index++)
                {
                    var preparation = preflight.Items[index];
                    var result = new BatchCommandResult
                    {
                        Tool = commands[index]?.Tool ?? string.Empty,
                        Ok = false,
                    };
                    if (preparation?.Error != null)
                    {
                        result.Error = preparation.Error;
                        result.ErrorCode = preparation.ErrorCode;
                        failed++;
                    }
                    else
                    {
                        result.Skipped = true;
                        result.Error = "Skipped because batch child preflight failed.";
                    }
                    results[index] = result;
                }

                aborted = true;
                // The parent transaction may already have been opened by the
                // outer middleware. Closing it here prevents an empty or
                // partially-approved aggregate from becoming an Undo step.
                AuthoringTransactionScope.Current?.Abort();
                return new BatchResult
                {
                    TotalCommands = commands.Length,
                    Succeeded = 0,
                    Failed = failed,
                    Aborted = aborted,
                    RanInParallel = ranInParallel,
                    Results = results,
                    Note = note,
                };
            }

            // ------ Sequential dispatch loop ------
            for (int i = 0; i < commands.Length; i++)
            {
                ThrowIfStopped(parentContext);
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
                    var parameters = cmd.Params ?? new Dictionary<string, JsonElement>();
                    var preparation = preflight.Items[i]
                        ?? throw new ToolCallControlException(
                            ToolCallErrorCodes.ConfirmationInvalid,
                            "The approved batch child record is missing.");
                    // Use the exact child context that was preflighted. The
                    // execution request changes only dry-run/confirmation;
                    // identity, deadline, parent, and canonical arguments
                    // therefore remain bound to the inspected action.
                    var childContext = preparation.Context;
                    var childControl = preparation.ToExecutionControl();
                    var request = new RequestCallTool(
                        childContext.RequestID,
                        cmd.Tool,
                        parameters,
                        childControl);
                    var response = await toolManager.RunCallTool(
                        request,
                        childContext.CancellationToken).ConfigureAwait(false);

                    if (response == null)
                    {
                        perCmd.Ok = false;
                        perCmd.Error = "Tool runner returned null response.";
                        perCmd.ErrorCode = ToolCallErrorCodes.ToolExecutionFailed;
                    }
                    else if (response.Status == ResponseStatus.Error)
                    {
                        perCmd.Transaction = CloneTransaction(response.Value?.Transaction);
                        perCmd.Ok = false;
                        perCmd.ErrorCode = response.StructuredError?.Code
                            ?? response.Value?.StructuredError?.Code;
                        // Prefer the inner ResponseCallTool message when available;
                        // fall back to the outer envelope message.
                        perCmd.Error = BoundMessage(response.Value?.Status == ResponseStatus.Error
                            ? (response.Value?.StructuredError?.Message
                                ?? response.Value?.GetMessage()
                                ?? response.Message)
                            : (response.StructuredError?.Message ?? response.Message));
                    }
                    else if (response.Value is { Status: ResponseStatus.Error } childResponse)
                    {
                        perCmd.Transaction = CloneTransaction(childResponse.Transaction);
                        perCmd.Ok = false;
                        perCmd.ErrorCode = childResponse.StructuredError?.Code;
                        perCmd.Error = BoundMessage(childResponse.StructuredError?.Message
                            ?? childResponse.GetMessage()
                            ?? response.Message);
                    }
                    else
                    {
                        perCmd.Transaction = CloneTransaction(response.Value?.Transaction);
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
                    perCmd.ErrorCode = ToolCallErrorCodes.Cancelled;
                }
                catch (Exception)
                {
                    perCmd.Ok = false;
                    // Batch results are caller-visible; do not expose raw
                    // exception text or host paths from a child runner.
                    perCmd.Error = "Child tool execution failed.";
                    perCmd.ErrorCode = ToolCallErrorCodes.ToolExecutionFailed;
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

            // Close only the ambient parent group before serializing the batch
            // result, then replace each pending shared child lifecycle with the
            // final outcome of that same group.
            var parentTransaction = AuthoringTransactionScope.Current;
            if (parentTransaction != null)
            {
                if (failed > 0)
                    parentTransaction.Abort();
                else
                    parentTransaction.Complete();

                FinalizeSharedChildResults(
                    results,
                    parentTransaction.Report,
                    ref succeeded,
                    ref failed);
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

        private sealed class ChildPreparation
        {
            public BatchCommand? Command { get; }
            public ToolCallContext Context { get; }
            public AuthoringChildPlanSummary? Record { get; }
            public string? ErrorCode { get; }
            public string? Error { get; }

            public ChildPreparation(
                BatchCommand? command,
                ToolCallContext context,
                AuthoringChildPlanSummary? record = null,
                string? errorCode = null,
                string? error = null)
            {
                Command = command;
                Context = context;
                Record = record;
                ErrorCode = errorCode;
                Error = error;
            }

            public ToolCallControl ToExecutionControl()
            {
                var record = Record;
                if (record == null)
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.ConfirmationInvalid,
                        "The approved batch child record is missing.");

                return new ToolCallControl
                {
                    Version = ToolCallControl.CurrentVersion,
                    CallId = record.CallId,
                    CorrelationId = record.CorrelationId,
                    ParentCallId = record.ParentCallId,
                    DeadlineUnixMs = record.DeadlineUnixMs,
                    CancellationId = record.CancellationId,
                    IdempotencyKey = record.IdempotencyKey,
                    DryRun = "none",
                    Confirm = record.ConfirmationRequired ? true : (bool?)null,
                    Confirmation = record.ConfirmationRequired
                        ? new ToolCallConfirmation
                        {
                            PlanId = record.PlanId,
                            PlanHash = record.PlanHash,
                            ExpiresAtUnixMs = record.ExpiresAtUnixMs,
                        }
                        : null,
                };
            }
        }

        private sealed class BatchPreflightResult
        {
            public ChildPreparation?[] Items { get; }
            public bool Succeeded { get; }

            public BatchPreflightResult(ChildPreparation?[] items, bool succeeded)
            {
                Items = items;
                Succeeded = succeeded;
            }
        }

        private static BatchPreflightResult PrepareApprovedChildren(
            BatchCommand[] commands,
            HashSet<string> knownTools,
            IToolManager toolManager,
            ToolCallContext parentContext,
            IReadOnlyList<AuthoringChildPlanSummary>? approvedChildren,
            bool failFast)
        {
            var items = new ChildPreparation?[commands.Length];
            if (approvedChildren == null || approvedChildren.Count != commands.Length)
            {
                items[0] = Failure(
                    commands[0],
                    parentContext,
                    ToolCallErrorCodes.ConfirmationInvalid,
                    "The approved batch plan does not contain the exact child record set.");
                return new BatchPreflightResult(items, succeeded: false);
            }

            var policy = AuthoringSafetyPolicyContext.Current;
            if (policy == null)
            {
                items[0] = Failure(
                    commands[0],
                    parentContext,
                    ToolCallErrorCodes.SafetyUnsupported,
                    "The live authoring safety policy is unavailable.");
                return new BatchPreflightResult(items, succeeded: false);
            }

            var failed = false;
            for (var index = 0; index < commands.Length; index++)
            {
                ThrowIfStopped(parentContext);
                var command = commands[index];
                if (command == null)
                {
                    items[index] = Failure(command, parentContext, ToolCallErrorCodes.InvalidControl, Error.CommandNull(index));
                    failed = true;
                    if (failFast) break;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(command.Tool))
                {
                    items[index] = Failure(command, parentContext, ToolCallErrorCodes.InvalidControl, Error.CommandToolEmpty(index));
                    failed = true;
                    if (failFast) break;
                    continue;
                }

                if (string.Equals(command.Tool, BatchExecuteToolId, StringComparison.Ordinal))
                {
                    items[index] = Failure(command, parentContext, ToolCallErrorCodes.SafetyUnsupported, Error.NestedBatchNotAllowed());
                    failed = true;
                    if (failFast) break;
                    continue;
                }

                if (!knownTools.Contains(command.Tool))
                {
                    items[index] = Failure(command, parentContext, ToolCallErrorCodes.SafetyUnsupported, Error.UnknownTool(command.Tool));
                    failed = true;
                    if (failFast) break;
                    continue;
                }

                var record = approvedChildren[index];
                if (record == null
                    || record.Index != index
                    || !string.Equals(record.ToolName, command.Tool, StringComparison.Ordinal)
                    || record.PolicyVersion != AuthoringSafetyPolicy.PolicyVersion
                    || !string.Equals(record.CorrelationId, parentContext.CorrelationId, StringComparison.Ordinal)
                    || !string.Equals(record.ParentCallId, parentContext.CallId, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(record.RequestID)
                    || string.IsNullOrWhiteSpace(record.CallId)
                    || string.IsNullOrWhiteSpace(record.ArgumentsHash)
                    || !HasValidTokenShape(record))
                {
                    items[index] = Failure(
                        command,
                        parentContext,
                        ToolCallErrorCodes.ConfirmationInvalid,
                        "The approved batch child record is malformed or out of order.");
                    failed = true;
                    if (failFast) break;
                    continue;
                }

                var runner = toolManager.GetAllTools()
                    .FirstOrDefault(candidate => candidate != null
                        && string.Equals(candidate.Name, command.Tool, StringComparison.Ordinal));
                if (runner == null)
                {
                    items[index] = Failure(command, parentContext, ToolCallErrorCodes.SafetyUnsupported,
                        Error.UnknownTool(command.Tool));
                    failed = true;
                    if (failFast) break;
                    continue;
                }

                ToolCallContext childContext;
                try
                {
                    childContext = ToolCallContextNormalizer.DeriveChild(
                        parentContext,
                        new ToolCallControl
                        {
                            Version = ToolCallControl.CurrentVersion,
                            CallId = record.CallId,
                            CorrelationId = record.CorrelationId,
                            ParentCallId = record.ParentCallId,
                            DeadlineUnixMs = record.DeadlineUnixMs,
                            CancellationId = record.CancellationId,
                            IdempotencyKey = record.IdempotencyKey,
                        },
                        record.RequestID,
                        parentContext.CancellationToken);

                    var parameters = command.Params ?? new Dictionary<string, JsonElement>();
                    var childInvocation = policy.PrepareInvocation(new AuthoringInvocation(
                        childContext,
                        runner.Name,
                        parameters,
                        runner));
                    var descriptor = childInvocation.Descriptor;
                    AuthoringValidationResult? validation = null;
                    if (descriptor?.Validator != null)
                    {
                        validation = descriptor.Validator.Validate(childInvocation);
                        if (validation == null || !validation.Valid)
                        {
                            var failureCode = validation?.FailureCode;
                            var safeFailureCode = string.IsNullOrWhiteSpace(failureCode)
                                ? "validation_failed"
                                : failureCode ?? "validation_failed";
                            throw new ToolCallControlException(
                                safeFailureCode,
                                "A batch child did not pass validation.");
                        }
                    }

                    var childPlan = descriptor?.Planner?.Plan(childInvocation);
                    if (descriptor?.Planner != null && childPlan == null)
                        throw new ToolCallControlException(
                            ToolCallErrorCodes.SafetyUnsupported,
                            "A batch child planner returned no plan.");

                    var validationTargets = validation?.Targets == null
                        ? Array.Empty<AuthoringTargetSummary>()
                        : validation.Targets.Where(target => target != null).Take(8)
                            .Select(target => target.CloneSafe()).ToArray();
                    var plannedTargets = childPlan?.Targets == null
                        ? Array.Empty<AuthoringTargetSummary>()
                        : childPlan.Targets.Where(target => target != null).Take(8)
                            .Select(target => target.CloneSafe()).ToArray();
                    IReadOnlyList<AuthoringTargetSummary> targets = plannedTargets.Length == 0
                        ? validationTargets
                        : plannedTargets;
                    var plannedEffects = childPlan?.PredictedEffects == null
                        ? Array.Empty<string>()
                        : childPlan.PredictedEffects.Where(effect => !string.IsNullOrWhiteSpace(effect)).Take(8)
                            .Select(effect => effect.Length <= 160 ? effect : effect.Substring(0, 160)).ToArray();
                    IReadOnlyList<string> effects = plannedEffects.Length > 0
                        ? plannedEffects
                        : new[]
                        {
                            descriptor == null || descriptor.IsOpaque
                                ? "undeclared"
                                : descriptor.MutationKind == AuthoringMutationKind.Unknown
                                    ? "authoring change"
                                    : descriptor.MutationKind.ToString().ToLowerInvariant(),
                        };

                    var verified = policy.CreateAggregateChildRecord(
                        index,
                        childInvocation,
                        validation,
                        childPlan,
                        targets,
                        effects,
                        record);
                    items[index] = new ChildPreparation(command, childContext, verified);
                }
                catch (ToolCallControlException exception)
                {
                    items[index] = Failure(command, parentContext, exception.Code, exception.Message);
                    failed = true;
                    if (failFast) break;
                }
                catch
                {
                    items[index] = Failure(
                        command,
                        parentContext,
                        ToolCallErrorCodes.ConfirmationStale,
                        "The approved batch child could not be revalidated.");
                    failed = true;
                    if (failFast) break;
                }
            }

            return new BatchPreflightResult(items, succeeded: !failed);
        }

        private static bool HasValidTokenShape(AuthoringChildPlanSummary record)
            => record.ConfirmationRequired
                ? !string.IsNullOrWhiteSpace(record.PlanId)
                    && !string.IsNullOrWhiteSpace(record.PlanHash)
                    && record.ExpiresAtUnixMs.HasValue
                : string.IsNullOrWhiteSpace(record.PlanId)
                    && string.IsNullOrWhiteSpace(record.PlanHash)
                    && !record.ExpiresAtUnixMs.HasValue;

        private static ChildPreparation Failure(
            BatchCommand? command,
            ToolCallContext parentContext,
            string code,
            string message)
            => new ChildPreparation(
                command,
                parentContext.Clone(),
                errorCode: string.IsNullOrWhiteSpace(code) ? ToolCallErrorCodes.SafetyUnsupported : code,
                error: BoundMessage(message));

        private static void FinalizeSharedChildResults(
            BatchCommandResult[] results,
            AuthoringTransactionReport parentReport,
            ref int succeeded,
            ref int failed)
        {
            var rollback = parentReport.Rollback.ToString().ToLowerInvariant();
            foreach (var result in results)
            {
                if (result?.Transaction == null
                    || !ReadBoolean(result.Transaction, "shared")
                    || !ReadBoolean(result.Transaction, "pending"))
                    continue;

                result.Transaction["rollback"] = rollback;
                result.Transaction["completed"] = parentReport.Completed;
                result.Transaction["aborted"] = parentReport.Aborted;
                result.Transaction["shared"] = true;
                result.Transaction["pending"] = false;

                if (!parentReport.Aborted || !result.Ok || !ReadBoolean(result.Transaction, "mutated"))
                    continue;

                result.Ok = false;
                result.ErrorCode = ToolCallErrorCodes.AuthoringTransactionFailed;
                if (parentReport.Rollback == AuthoringRollbackStatus.Complete)
                {
                    result.Data = null;
                    result.Error = "The shared authoring transaction was aborted; this child's mutation did not remain applied.";
                }
                else
                {
                    result.Data = RetainBoundedDiagnosticData(result.Data);
                    result.Error = "The shared authoring transaction was aborted, but final mutation state is uncertain; rollback was "
                        + rollback + " and some effects may remain applied.";
                }
                succeeded--;
                failed++;
            }
        }

        private static object? RetainBoundedDiagnosticData(object? data)
        {
            if (data == null)
                return null;

            var value = data as string;
            if (string.IsNullOrEmpty(value))
                return null;
            return value.Length <= 1024 ? value : value.Substring(0, 1024);
        }

        private static JsonObject? CloneTransaction(JsonObject? transaction)
        {
            if (transaction == null)
                return null;

            var affected = new JsonArray();
            if (transaction["affectedObjects"] is JsonArray sourceAffected)
            {
                foreach (var item in sourceAffected.OfType<JsonObject>().Take(64))
                {
                    affected.Add(new JsonObject
                    {
                        ["kind"] = ReadBoundedString(item, "kind"),
                        ["name"] = ReadBoundedString(item, "name"),
                        ["relativePath"] = ReadSafeRelativePath(item, "relativePath"),
                    });
                }
            }

            return new JsonObject
            {
                ["undo"] = ReadBoundedString(transaction, "undo"),
                ["mutated"] = ReadBoolean(transaction, "mutated"),
                ["groupId"] = ReadNullableInteger(transaction, "groupId"),
                ["groupLabel"] = ReadBoundedString(transaction, "groupLabel"),
                ["rollback"] = ReadBoundedString(transaction, "rollback"),
                ["completed"] = ReadBoolean(transaction, "completed"),
                ["aborted"] = ReadBoolean(transaction, "aborted"),
                ["shared"] = ReadBoolean(transaction, "shared"),
                ["pending"] = ReadBoolean(transaction, "pending"),
                ["affectedObjects"] = affected,
            };
        }

        private static string? ReadBoundedString(JsonObject source, string name)
        {
            try
            {
                var value = source[name]?.GetValue<string>();
                return value == null ? null : value.Length <= 160 ? value : value.Substring(0, 160);
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadSafeRelativePath(JsonObject source, string name)
        {
            var value = ReadBoundedString(source, name);
            if (value == null
                || value.StartsWith("/", StringComparison.Ordinal)
                || value.StartsWith("\\", StringComparison.Ordinal)
                || (value.Length > 1 && value[1] == ':'))
                return null;
            return value.Replace('\\', '/');
        }

        private static bool ReadBoolean(JsonObject source, string name)
        {
            try { return source[name]?.GetValue<bool>() ?? false; }
            catch { return false; }
        }

        private static int? ReadNullableInteger(JsonObject source, string name)
        {
            try { return source[name]?.GetValue<int>(); }
            catch { return null; }
        }

        private static string BoundMessage(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? "Child preflight failed."
                : value.Length <= 240 ? value : value.Substring(0, 240);

        private static void ThrowIfStopped(ToolCallContext context)
        {
            if (context.CancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(context.CancellationToken);
            if (context.DeadlineUnixMs.HasValue
                && context.DeadlineUnixMs.Value <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                throw new ToolCallControlException(
                    ToolCallErrorCodes.DeadlineExceeded,
                    "Tool call deadline expired.",
                    callId: context.CallId,
                    correlationId: context.CorrelationId);
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
