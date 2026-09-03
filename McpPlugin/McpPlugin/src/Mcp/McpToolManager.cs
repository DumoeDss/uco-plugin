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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using com.IvanMurzak.ReflectorNet.Utils;
using Microsoft.Extensions.Logging;
using R3;

namespace com.IvanMurzak.McpPlugin
{
    public class McpToolManager : IToolManager
    {
        protected readonly ILogger _logger;
        protected readonly Reflector _reflector;
        protected readonly CompositeDisposable _disposables = new();
        private ulong toolCallsCount = 0;

        readonly ToolRunnerCollection _tools;
        readonly ToolExecutionPipeline _executionPipeline;
        readonly Subject<Unit> _onToolsUpdated = new();

        public Reflector Reflector => _reflector;
        public Observable<Unit> OnToolsUpdated => _onToolsUpdated;
        public ToolExecutionPipeline ExecutionPipeline => _executionPipeline;

        public IEnumerable<IRunTool> GetAllTools()
            => _tools.Keys.ToArray().Select(GetGuardedRunner).ToList();
        public ulong ToolCallsCount => (ulong)Interlocked.Read(ref Unsafe.As<ulong, long>(ref toolCallsCount));

        private IRunTool GetGuardedRunner(string name)
        {
            var runner = _tools[name];
            var guarded = GuardedRunTool.Wrap(runner);
            if (!ReferenceEquals(runner, guarded))
                _tools[name] = guarded;
            return guarded;
        }

        public McpToolManager(
            ILogger<McpToolManager> logger,
            Reflector reflector,
            ToolRunnerCollection tools,
            ToolExecutionPipeline? executionPipeline = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.LogTrace("Ctor");
            _reflector = reflector ?? throw new ArgumentNullException(nameof(reflector));
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _executionPipeline = executionPipeline ?? new ToolExecutionPipeline();

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Registered tools [{0}]:", tools.Count);
                foreach (var kvp in tools)
                    _logger.LogTrace("Tool: {0}", kvp.Key);
            }
        }

        #region Tools
        public int EnabledToolsCount => GetAllTools().Count(tool => tool.Enabled);
        public int TotalToolsCount => _tools.Count;

        /// <summary>
        /// Gets the total token count for all enabled tools.
        /// This is calculated as the sum of TokenCount for each enabled tool.
        /// Recalculates on each access but should be performant for typical tool counts.
        /// </summary>
        public int EnabledToolsTokenCount => GetAllTools()
            .Where(tool => tool.Enabled)
            .Sum(tool => tool.TokenCount);

        public bool HasTool(string name) => _tools.ContainsKey(name);
        public bool AddTool(string name, IRunTool runner)
        {
            if (HasTool(name))
            {
                _logger.LogWarning("Tool with Name '{0}' already exists. Skipping addition.", name);
                return false;
            }

            _tools[name] = GuardedRunTool.Wrap(runner);
            _onToolsUpdated.OnNext(Unit.Default);
            return true;
        }
        public bool RemoveTool(string name)
        {
            if (!HasTool(name))
            {
                _logger.LogWarning("Tool with Name '{0}' not found. Cannot remove.", name);
                return false;
            }

            var removed = _tools.Remove(name);
            if (removed)
                _onToolsUpdated.OnNext(Unit.Default);

            return removed;
        }
        public bool IsToolEnabled(string name)
        {
            if (!_tools.ContainsKey(name))
            {
                _logger.LogWarning("Tool with Name '{0}' not found.", name);
                return false;
            }

            return GetGuardedRunner(name).Enabled;
        }
        public bool SetToolEnabled(string name, bool enabled)
        {
            if (!_tools.ContainsKey(name))
            {
                _logger.LogWarning("Tool with Name '{0}' not found.", name);
                return false;
            }

            var runner = GetGuardedRunner(name);
            runner.Enabled = enabled;
            _onToolsUpdated.OnNext(Unit.Default);

            return true;
        }

        public Task<ResponseData<ResponseCallTool>> RunCallTool(RequestCallTool data) => RunCallTool(data, default);
        public async Task<ResponseData<ResponseCallTool>> RunCallTool(RequestCallTool data, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Unsafe.As<ulong, long>(ref toolCallsCount));
            if (data == null)
                return ResponseData<ResponseCallTool>.Error(Common.Consts.Guid.Zero, "Tool data is null.")
                    .Log(_logger);

            if (data.Control == null && string.IsNullOrEmpty(data.Name))
                return ResponseData<ResponseCallTool>.Error(data.RequestID, "Tool.Name is null.")
                    .Log(_logger);

            ToolCallNormalizationResult normalized;
            try
            {
                normalized = ToolCallContextNormalizer.Normalize(data, cancellationToken);
            }
            catch (ToolCallControlException ex)
            {
                if (data.Control != null)
                    return CreateControlledError(data, ex).Log(_logger);

                return ResponseData<ResponseCallTool>.Error(data.RequestID, ex.Message)
                    .Log(_logger);
            }

            var request = normalized.Request;
            var context = normalized.Context;
            if (!_tools.ContainsKey(request.Name))
                return ResponseData<ResponseCallTool>.Error(request.RequestID, $"Tool with Name '{request.Name}' not found.")
                    .Log(_logger);
            var runner = GetGuardedRunner(request.Name);
            try
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var message = request.Arguments == null
                        ? $"Run tool '{request.Name}' with no parameters."
                        : $"Run tool '{request.Name}' with parameters[{request.Arguments.Count}]:\n{string.Join(",\n", request.Arguments)}\n";
                    _logger.LogInformation(message);
                }

                // Publish runner metadata before entering the pipeline so
                // the outermost authoring policy can classify this exact
                // registration without a second name-keyed safety registry.
                var authoringInvocation = new AuthoringInvocation(
                    context,
                    request.Name,
                    request.Arguments ?? new Dictionary<string, JsonElement>(),
                    runner);
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
                        using var executionAuthorization = ToolCallInvocationScope.AuthorizeRunnerExecution(runner);
                        return await runner.Run(
                            invocationContext.CallId,
                            terminalInvocation.Arguments,
                            invocationContext.CancellationToken).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                if (result == null)
                {
                    if (!context.Legacy)
                    {
                        return CreateControlledError(
                            request,
                            new ToolCallControlException(
                                ToolCallErrorCodes.ToolExecutionFailed,
                                $"Tool '{request.Name}' returned null result.",
                                callId: context.CallId,
                                correlationId: context.CorrelationId),
                            context).Log(_logger);
                    }

                    return ResponseData<ResponseCallTool>.Error(request.RequestID, $"Tool '{request.Name}' returned null result.")
                        .Log(_logger);
                }

                if (!context.Legacy && result.Status == ResponseStatus.Error)
                {
                    result.StructuredError ??= new ToolCallError(
                        ToolCallErrorCodes.ToolExecutionFailed,
                        $"Failed to run tool '{request.Name}'.",
                        callId: context.CallId,
                        correlationId: context.CorrelationId);
                    EnsureErrorCorrelation(result.StructuredError, context);
                }

                result.Log(_logger);

                return result.Pack(request.RequestID);
            }
            catch (ToolCallControlException ex)
            {
                if (!context.Legacy)
                {
                    _logger.LogWarning(ex, "Controlled middleware rejected or interrupted RunCallTool[{name}].", request.Name);
                    return CreateControlledError(request, ex, context);
                }

                return ResponseData<ResponseCallTool>.Error(request.RequestID, ex.Message)
                    .Log(_logger, $"RunCallTool[{request.Name}]", ex);
            }
            catch (Exception ex)
            {
                // Preserve the existing string-oriented response for legacy
                // callers, while controlled callers receive a stable error.
                if (!context.Legacy)
                {
                    var response = CreateControlledError(
                        request,
                        new ToolCallControlException(
                            ToolCallErrorCodes.ToolExecutionFailed,
                            $"Failed to run tool '{request.Name}'.",
                            callId: context.CallId,
                            correlationId: context.CorrelationId),
                        context);
                    _logger.LogError(ex, "RunCallTool[{name}] failed.", request.Name);
                    return response;
                }

                return ResponseData<ResponseCallTool>.Error(request.RequestID, $"Failed to run tool '{request.Name}'. Exception: {ex}")
                    .Log(_logger, $"RunCallTool[{request.Name}]", ex);
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

        public Task<ResponseData<ResponseListTool[]>> RunListTool(RequestListTool data) => RunListTool(data, default);
        public Task<ResponseData<ResponseListTool[]>> RunListTool(RequestListTool data, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Listing tools.");
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
                _logger.LogDebug("{0} Tools listed.", result.Length);

                return result
                    .Log(_logger)
                    .Pack(data.RequestID)
                    .TaskFromResult();
            }
            catch (Exception ex)
            {
                // Handle or log the exception as needed
                return ResponseData<ResponseListTool[]>.Error(data.RequestID, $"Failed to list tools. Exception: {ex}")
                    .Log(_logger, "RunListTool", ex)
                    .TaskFromResult();
            }
        }
        #endregion

        public void Dispose()
        {
            _disposables.Dispose();
            _tools.Clear();
        }
    }
}
