/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Repository: GitHub (https://github.com/IvanMurzak/MCP-Plugin-dotnet)  │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the Apache License, Version 2.0.                       │
│  See the LICENSE file in the project root for more information.        │
└────────────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Hub.Client;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.McpPlugin.Common.Utils;
using com.IvanMurzak.ReflectorNet;
using com.IvanMurzak.ReflectorNet.Utils;
using Microsoft.Extensions.Logging;

namespace com.IvanMurzak.McpPlugin
{
    /// <summary>
    /// Manages system tools — internal tools available via HTTP API but NOT exposed to MCP clients.
    /// System tools are discovered via <see cref="McpPluginToolAttribute"/> with
    /// <see cref="McpPluginToolAttribute.ToolType"/> set to <see cref="McpToolType.System"/>.
    /// </summary>
    public class McpSystemToolManager : ISystemToolManager
    {
        readonly ILogger _logger;
        readonly SystemToolRunnerCollection _tools;
        readonly ToolExecutionPipeline _executionPipeline;

        public ToolExecutionPipeline ExecutionPipeline => _executionPipeline;

        public McpSystemToolManager(
            ILogger<McpSystemToolManager> logger,
            SystemToolRunnerCollection tools,
            ToolExecutionPipeline? executionPipeline = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogTrace("Ctor");
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _executionPipeline = executionPipeline ?? new ToolExecutionPipeline();

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Registered system tools [{0}]:", tools.Count);
                foreach (var kvp in tools)
                    _logger.LogTrace("System tool: {0}", kvp.Key);
            }
        }

        public int TotalToolsCount => _tools.Count;

        public IEnumerable<IRunTool> GetAllTools()
            => _tools.Keys.ToArray().Select(GetGuardedRunner).ToList();

        private IRunTool GetGuardedRunner(string name)
        {
            var runner = _tools[name];
            var guarded = GuardedRunTool.Wrap(runner);
            if (!ReferenceEquals(runner, guarded))
                _tools[name] = guarded;
            return guarded;
        }

        public bool HasTool(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            return _tools.ContainsKey(name);
        }

        public async Task<ResponseData<ResponseCallTool>> RunSystemTool(RequestCallTool request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                return ResponseData<ResponseCallTool>.Error(string.Empty, "Request is null.");

            var name = request.Name;
            if (request.Control == null && string.IsNullOrWhiteSpace(name))
                return ResponseData<ResponseCallTool>.Error(request.RequestID, "System tool name is empty.");

            ToolCallNormalizationResult normalized;
            try
            {
                normalized = ToolCallContextNormalizer.Normalize(request, cancellationToken);
            }
            catch (ToolCallControlException ex)
            {
                if (request.Control != null)
                    return CreateControlledError(request, ex);

                return ResponseData<ResponseCallTool>.Error(request.RequestID, ex.Message);
            }

            var normalizedRequest = normalized.Request;
            var context = normalized.Context;
            if (!_tools.ContainsKey(normalizedRequest.Name))
            {
                _logger.LogWarning("System tool '{name}' not found. Available: [{available}]",
                    normalizedRequest.Name, string.Join(", ", _tools.Keys.OrderBy(k => k)));
                return ResponseData<ResponseCallTool>.Error(normalizedRequest.RequestID, $"System tool '{normalizedRequest.Name}' not found.");
            }
            var tool = GetGuardedRunner(normalizedRequest.Name);

            try
            {
                _logger.LogDebug("Executing system tool '{name}'.", normalizedRequest.Name);
                var authoringInvocation = new AuthoringInvocation(
                    context,
                    normalizedRequest.Name,
                    normalizedRequest.Arguments ?? new Dictionary<string, JsonElement>(),
                    tool);
                using var invocationScope = ToolCallInvocationScope.Push(context, authoringInvocation);
                var result = await _executionPipeline.InvokeAsync(
                    context,
                    async invocationContext =>
                    {
                        var terminalInvocation = ToolCallInvocationScope.CurrentInvocation
                            ?? authoringInvocation.WithContext(invocationContext);
                        using var terminalScope = ToolCallInvocationScope.Push(
                            invocationContext,
                            terminalInvocation.WithContext(invocationContext));
                        using var executionAuthorization = ToolCallInvocationScope.AuthorizeRunnerExecution(tool);
                        return await tool.Run(
                            invocationContext.CallId,
                            terminalInvocation.Arguments,
                            invocationContext.CancellationToken).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                if (result == null)
                {
                    if (!context.Legacy)
                    {
                        return CreateControlledError(
                            normalizedRequest,
                            new ToolCallControlException(
                                ToolCallErrorCodes.ToolExecutionFailed,
                                $"System tool '{normalizedRequest.Name}' returned null result.",
                                callId: context.CallId,
                                correlationId: context.CorrelationId),
                            context);
                    }

                    return ResponseData<ResponseCallTool>.Error(
                        normalizedRequest.RequestID,
                        $"System tool '{normalizedRequest.Name}' returned null result.");
                }

                if (!context.Legacy && result.Status == ResponseStatus.Error)
                {
                    result.StructuredError ??= new ToolCallError(
                        ToolCallErrorCodes.ToolExecutionFailed,
                        $"System tool '{normalizedRequest.Name}' failed.",
                        callId: context.CallId,
                        correlationId: context.CorrelationId);
                    EnsureErrorCorrelation(result.StructuredError, context);
                }

                return result.Pack(normalizedRequest.RequestID);
            }
            catch (ToolCallControlException ex)
            {
                if (!context.Legacy)
                {
                    _logger.LogWarning(ex, "Controlled middleware rejected or interrupted system tool '{name}'.", normalizedRequest.Name);
                    return CreateControlledError(normalizedRequest, ex, context);
                }

                return ResponseData<ResponseCallTool>.Error(normalizedRequest.RequestID, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "System tool '{name}' failed.", normalizedRequest.Name);
                if (!context.Legacy)
                {
                    var response = CreateControlledError(
                        normalizedRequest,
                        new ToolCallControlException(
                            ToolCallErrorCodes.ToolExecutionFailed,
                            $"System tool '{normalizedRequest.Name}' failed.",
                            callId: context.CallId,
                            correlationId: context.CorrelationId),
                        context);
                    return response;
                }

                return ResponseData<ResponseCallTool>.Error(normalizedRequest.RequestID, $"System tool '{normalizedRequest.Name}' failed: {ex.Message}");
            }
        }

        private static ResponseData<ResponseCallTool> CreateControlledError(
            RequestCallTool request,
            ToolCallControlException exception,
            ToolCallContext? context = null)
        {
            var callId = FirstNonEmpty(
                exception.CallId,
                context?.CallId,
                request.Control?.CallId,
                request.RequestID);
            var correlationId = FirstNonEmpty(
                exception.CorrelationId,
                context?.CorrelationId,
                request.Control?.CorrelationId,
                callId);
            var error = new ToolCallError(
                exception.Code,
                exception.Message,
                exception.Retryable,
                callId,
                correlationId,
                exception.Details);
            var responseRequestId = FirstNonEmpty(
                request.RequestID,
                context?.RequestID,
                callId) ?? string.Empty;
            var response = ResponseData<ResponseCallTool>.Error(responseRequestId, error.Message);
            response.StructuredError = error;
            return response;
        }

        private static void EnsureErrorCorrelation(ToolCallError error, ToolCallContext context)
        {
            if (string.IsNullOrWhiteSpace(error.CallId))
                error.CallId = context.CallId;
            if (string.IsNullOrWhiteSpace(error.CorrelationId))
                error.CorrelationId = context.CorrelationId;
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        public Task<ResponseData<ResponseListTool[]>> RunListSystemTool(RequestListTool request, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Listing system tools.");
                var result = _tools.Keys.ToArray()
                    .Select(GetGuardedRunner)
                    .Select(tool =>
                    {
                        var response = new ResponseListTool()
                        {
                            Name = tool.Name,
                            Enabled = tool.Enabled,
                            Title = tool.Title,
                            Description = tool.Description,
                            InputSchema = tool.InputSchema.ToJsonElement() ?? Common.Consts.MCP.EmptyInputSchema,
                            ReadOnlyHint = tool.ReadOnlyHint,
                            DestructiveHint = tool.DestructiveHint,
                            IdempotentHint = tool.IdempotentHint,
                            OpenWorldHint = tool.OpenWorldHint
                        };
                        if (tool.OutputSchema == null)
                            return response;

                        if (tool.OutputSchema is not JsonNode jn)
                            return response;

                        if (jn.GetValueKind() != JsonValueKind.Object)
                            return response;

                        if (jn[JsonSchema.Type]?.GetValue<string>() != JsonSchema.Object)
                            return response;

                        response.OutputSchema = jn.ToJsonElement();
                        return response;
                    })
                    .ToArray();
                _logger.LogDebug("{0} System tools listed.", result.Length);

                return result
                    .Log(_logger)
                    .Pack(request.RequestID)
                    .TaskFromResult();
            }
            catch (Exception ex)
            {
                return ResponseData<ResponseListTool[]>.Error(request.RequestID, $"Failed to list system tools. Exception: {ex}")
                    .Log(_logger, "RunListSystemTool", ex)
                    .TaskFromResult();
            }
        }
    }
}
