/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>
    /// Registration-owned execution guard for every reflected and custom tool.
    /// Mutation-capable runners execute only from the policy-approved manager
    /// terminal; metadata remains the canonical registration metadata.
    /// </summary>
    internal sealed class GuardedRunTool : IRunTool
    {
        private readonly IRunTool _inner;

        private GuardedRunTool(IRunTool inner)
            => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public static IRunTool Wrap(IRunTool runner)
        {
            if (runner == null)
                throw new ArgumentNullException(nameof(runner));

            return runner is GuardedRunTool ? runner : new GuardedRunTool(runner);
        }

        public string Name => _inner.Name;
        public bool Enabled
        {
            get => _inner.Enabled;
            set => _inner.Enabled = value;
        }
        public string? Title => _inner.Title;
        public string? Description => _inner.Description;
        public MethodInfo? Method => _inner.Method;
        public string? SkillDescription => _inner.SkillDescription;
        public string? SkillBody => _inner.SkillBody;
        public JsonNode? InputSchema => _inner.InputSchema;
        public JsonNode? OutputSchema => _inner.OutputSchema;
        public UcoToolType ToolType => _inner.ToolType;
        public bool? ReadOnlyHint => _inner.ReadOnlyHint;
        public bool? DestructiveHint => _inner.DestructiveHint;
        public bool? IdempotentHint => _inner.IdempotentHint;
        public bool? OpenWorldHint => _inner.OpenWorldHint;
        public AuthoringCapabilityDescriptor? AuthoringCapability => _inner.AuthoringCapability;
        public ToolExecutionSchedulingMetadata? ExecutionScheduling
            => _inner.ExecutionScheduling
                ?? (_inner is IToolExecutionSchedulingMetadata metadata
                    ? new ToolExecutionSchedulingMetadata
                    {
                        ExecutionAffinity = metadata.ExecutionAffinity,
                        ThreadSafeRead = metadata.ThreadSafeRead
                    }
                    : null);
        public bool ReturnsDurableOperationHandle => _inner.ReturnsDurableOperationHandle;
        public int TokenCount => _inner.TokenCount;

        public async Task<ResponseCallTool> Run(
            string requestId,
            IReadOnlyDictionary<string, JsonElement>? namedParameters,
            CancellationToken cancellationToken = default)
        {
            var mutationCapable = AuthoringSafetyPolicyClassifier.Classify(this) != AuthoringRiskLevel.Read;
            var invocation = ToolCallInvocationScope.CurrentInvocation;
            if (mutationCapable
                && (invocation == null
                    || !invocation.PolicyApproved
                    || !ReferenceEquals(invocation.Runner, this)
                    || !ToolCallInvocationScope.IsRunnerExecutionAuthorized))
            {
                var context = ToolCallInvocationScope.Current;
                return ResponseCallTool.Error(new ToolCallError(
                    ToolCallErrorCodes.SafetyUnsupported,
                    "Tool invocation must enter the authoring safety pipeline.",
                    callId: context?.CallId ?? requestId,
                    correlationId: context?.CorrelationId ?? requestId,
                    details: new JsonObject { ["reason"] = "direct_runner_bypass" }))
                    .SetRequestID(requestId);
            }

            if (invocation == null || !ReferenceEquals(invocation.Runner, this))
                return await _inner.Run(requestId, namedParameters, cancellationToken).ConfigureAwait(false);

            using var innerScope = ToolCallInvocationScope.Push(
                invocation.Context,
                invocation.WithRunner(_inner));
            return await _inner.Run(requestId, namedParameters, cancellationToken).ConfigureAwait(false);
        }
    }
}
