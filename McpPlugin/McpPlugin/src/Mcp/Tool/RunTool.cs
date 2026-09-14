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
using com.AtelierAI.Uco.Framework.Utils;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging;

namespace com.AtelierAI.Uco.Framework
{
    /// <summary>
    /// Provides functionality to execute methods dynamically, supporting both static and instance methods.
    /// Allows for parameter passing by position or by name, with support for default parameter values.
    /// </summary>
    public partial class RunTool : MethodWrapper, IRunTool
    {
        public string Name { get; private set; }
        public bool Enabled { get; set; } = true;
        public UcoToolType ToolType { get; protected set; } = UcoToolType.Standard;
        public string? Title { get; protected set; }
        public bool? ReadOnlyHint { get; protected set; }
        public bool? DestructiveHint { get; protected set; }
        public bool? IdempotentHint { get; protected set; }
        public bool? OpenWorldHint { get; protected set; }
        public AuthoringCapabilityDescriptor? AuthoringCapability { get; protected set; }
        public ToolExecutionSchedulingMetadata? ExecutionScheduling { get; protected set; }
        public bool ReturnsDurableOperationHandle { get; protected set; }

        /// <summary>
        /// Reads <see cref="UcoSkillDescriptionAttribute"/> from the underlying method, if present.
        /// Used by <see cref="Skills.SkillFileGenerator"/> in place of <see cref="MethodWrapper.Description"/>
        /// when building the SKILL.md YAML <c>description:</c> field.
        /// </summary>
        public string? SkillDescription
            => Method?.GetCustomAttribute<UcoSkillDescriptionAttribute>()?.Description;

        /// <summary>
        /// Reads <see cref="UcoSkillBodyAttribute"/> from the underlying method, if present.
        /// Used by <see cref="Skills.SkillFileGenerator"/> to inject long-form markdown into the SKILL.md body
        /// between the description paragraph and the <c>## How to Call</c> section.
        /// </summary>
        public string? SkillBody
            => Method?.GetCustomAttribute<UcoSkillBodyAttribute>()?.Body;

        public MethodInfo Method => _methodInfo;

        /// <summary>
        /// Cached lookup dictionary for case-insensitive parameter name matching.
        /// Built once during construction for performance.
        /// </summary>
        private readonly Dictionary<string, string>? _paramNameLookup;

        public RunTool(
            Reflector reflector,
            ILogger? logger,
            string name,
            MethodInfo methodInfo,
            AuthoringCapabilityDescriptor? authoringCapability = null,
            ToolExecutionSchedulingMetadata? executionScheduling = null,
            bool returnsDurableOperationHandle = false) : base(reflector, logger, methodInfo)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _paramNameLookup = ParameterNameUtils.BuildParameterNameLookup(methodInfo?.GetParameters());
            AuthoringCapability = authoringCapability;
            ExecutionScheduling = executionScheduling?.Clone();
            ReturnsDurableOperationHandle = returnsDurableOperationHandle;
        }

        public RunTool(
            Reflector reflector,
            ILogger? logger,
            string name,
            object targetInstance,
            MethodInfo methodInfo,
            AuthoringCapabilityDescriptor? authoringCapability = null,
            ToolExecutionSchedulingMetadata? executionScheduling = null,
            bool returnsDurableOperationHandle = false) : base(reflector, logger, targetInstance, methodInfo)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _paramNameLookup = ParameterNameUtils.BuildParameterNameLookup(methodInfo?.GetParameters());
            AuthoringCapability = authoringCapability;
            ExecutionScheduling = executionScheduling?.Clone();
            ReturnsDurableOperationHandle = returnsDurableOperationHandle;
        }

        public RunTool(
            Reflector reflector,
            ILogger? logger,
            string name,
            Type classType,
            MethodInfo methodInfo,
            AuthoringCapabilityDescriptor? authoringCapability = null,
            ToolExecutionSchedulingMetadata? executionScheduling = null,
            bool returnsDurableOperationHandle = false) : base(reflector, logger, classType, methodInfo)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _paramNameLookup = ParameterNameUtils.BuildParameterNameLookup(methodInfo?.GetParameters());
            AuthoringCapability = authoringCapability;
            ExecutionScheduling = executionScheduling?.Clone();
            ReturnsDurableOperationHandle = returnsDurableOperationHandle;
        }

        protected override object? GetParameterValue(Reflector reflector, ParameterInfo paramInfo, object? value)
        {
            if (paramInfo.GetCustomAttribute<RequestIDAttribute>() != null)
            {
                var requestId = ToolCallInvocationScope.Current?.RequestID;
                _logger?.LogTrace("Injecting RequestID parameter: {RequestID}", requestId);
                return requestId;
            }
            if (paramInfo.GetCustomAttribute<ToolCallContextAttribute>() != null)
            {
                var context = ToolCallInvocationScope.Current;
                _logger?.LogTrace("Injecting tool call context: {CallId}", context?.CallId);
                return context;
            }
            return FixEnumConversion(paramInfo, base.GetParameterValue(reflector, paramInfo, value));
        }
        protected override object? GetParameterValue(Reflector reflector, ParameterInfo paramInfo, IReadOnlyDictionary<string, object?>? namedParameters)
        {
            if (paramInfo.GetCustomAttribute<RequestIDAttribute>() != null)
            {
                var requestId = ToolCallInvocationScope.Current?.RequestID;
                _logger?.LogTrace("Injecting RequestID parameter: {RequestID}", requestId);
                return requestId;
            }
            if (paramInfo.GetCustomAttribute<ToolCallContextAttribute>() != null)
            {
                var context = ToolCallInvocationScope.Current;
                _logger?.LogTrace("Injecting tool call context: {CallId}", context?.CallId);
                return context;
            }

            return FixEnumConversion(paramInfo, base.GetParameterValue(reflector, paramInfo, namedParameters));
        }
        protected override object? GetDefaultParameterValue(Reflector reflector, ParameterInfo methodParameter)
        {
            if (methodParameter.GetCustomAttribute<RequestIDAttribute>() != null)
            {
                var requestId = ToolCallInvocationScope.Current?.RequestID;
                _logger?.LogTrace("Injecting RequestID parameter: {RequestID}", requestId);
                return requestId;
            }
            if (methodParameter.GetCustomAttribute<ToolCallContextAttribute>() != null)
            {
                var context = ToolCallInvocationScope.Current;
                _logger?.LogTrace("Injecting tool call context: {CallId}", context?.CallId);
                return context;
            }
            return FixEnumConversion(methodParameter, base.GetDefaultParameterValue(reflector, methodParameter));
        }

        private object? FixEnumConversion(ParameterInfo paramInfo, object? value)
        {
            if (value != null)
            {
                var paramType = paramInfo.ParameterType;
                var underlyingType = Nullable.GetUnderlyingType(paramType) ?? paramType;

                if (underlyingType.IsEnum && value.GetType() != underlyingType)
                {
                    return Enum.ToObject(underlyingType, value);
                }
            }
            return value;
        }

        protected ResponseCallTool ProcessInvokeResult(string requestId, object? result)
        {
            if (result is ResponseCallTool response)
                return response.SetRequestID(requestId);

            if (result == null)
            {
                return ResponseCallTool
                    .Success()
                    .SetRequestID(requestId);
            }

            var structured = ResponseCallTool.SuccessStructured(
                System.Text.Json.JsonSerializer.SerializeToNode(
                    result, _reflector.JsonSerializerOptions));
            if (ReturnsDurableOperationHandle && result is IDurableOperationHandle)
                structured.Status = ResponseStatus.Processing;
            return structured.SetRequestID(requestId);
        }

        /// <summary>
        /// Executes the target static method with the provided arguments.
        /// </summary>
        /// <param name="requestId">The unique identifier for this request.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <param name="parameters">The arguments to pass to the method.</param>
        /// <returns>The result of the method execution, or null if the method is void.</returns>
        public async Task<ResponseCallTool> Run(string requestId, CancellationToken cancellationToken = default, params object?[] parameters)
        {
            var validationResult = ValidateRunParameters(requestId, parameters);
            if (validationResult != null)
                return validationResult;

            var pathBindingResult = ValidateCanonicalPathScope(requestId);
            if (pathBindingResult != null)
                return pathBindingResult;

            var authoringScopeResult = ValidateAuthoringScope(requestId);
            if (authoringScopeResult != null)
                return authoringScopeResult;

            using var invocationScope = ToolCallInvocationScope.PushIfMissing(requestId, cancellationToken);
            try
            {
                // Invoke the method (static or instance)
                var result = await Invoke(cancellationToken, parameters);
                return ProcessInvokeResult(requestId, result);
            }
            catch (ArgumentException ex)
            {
                var errorMessage = $"Parameter validation failed for tool '{Title ?? this.Method?.Name}': {ex.Message}";
                _logger?.LogError(ex, errorMessage);
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }
            catch (TargetParameterCountException ex)
            {
                var errorMessage = $"Parameter count mismatch for tool '{Title ?? this.Method?.Name}'. Expected {this.Method?.GetParameters().Length} parameters, but received {parameters?.Length}";
                _logger?.LogError(ex, errorMessage);
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }
            catch (ToolCallControlException) when (!(ToolCallInvocationScope.Current?.Legacy ?? true))
            {
                // Preserve stable policy errors raised by a path-aware
                // adapter so the shared pipeline can map them without
                // exposing the underlying exception.
                throw;
            }
            catch (ToolCallControlException ex)
            {
                return ResponseCallTool
                    .Error(ex.Message)
                    .SetRequestID(requestId);
            }
            catch (OperationCanceledException) when (!(ToolCallInvocationScope.Current?.Legacy ?? true))
            {
                // Let the controlled execution pipeline distinguish a caller
                // cancellation from a deadline expiry. Legacy direct callers
                // retain the historical string-oriented error below.
                throw;
            }
            catch (Exception ex)
            {
                var controlException = FindControlException(ex);
                if (controlException != null)
                {
                    if (!(ToolCallInvocationScope.Current?.Legacy ?? true))
                        throw controlException;

                    return ResponseCallTool
                        .Error(controlException.Message)
                        .SetRequestID(requestId);
                }

                var errorMessage = $"Tool execution failed for '{Title ?? this.Method?.Name}': {(ex.InnerException ?? ex).Message}";
                _logger?.LogError(ex, $"{errorMessage}\n{ex.StackTrace}");
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }
        }

        /// <summary>
        /// Executes the target method with named parameters.
        /// Missing parameters will be filled with their default values or the type's default value if no default is defined.
        /// </summary>
        /// <param name="requestId">The unique identifier for this request.</param>
        /// <param name="namedParameters">A dictionary mapping parameter names to their values.</param>
        /// <param name="cancellationToken">Token to cancel the operation.</param>
        /// <returns>The result of the method execution, or null if the method is void.</returns>
        public async Task<ResponseCallTool> Run(string requestId, IReadOnlyDictionary<string, JsonElement>? namedParameters, CancellationToken cancellationToken = default)
        {
            var validationResult = ValidateRunParameters(requestId, namedParameters);
            if (validationResult != null)
                return validationResult;

            var pathBindingResult = ValidateCanonicalPathScope(requestId);
            if (pathBindingResult != null)
                return pathBindingResult;

            var authoringScopeResult = ValidateAuthoringScope(requestId);
            if (authoringScopeResult != null)
                return authoringScopeResult;

            using var invocationScope = ToolCallInvocationScope.PushIfMissing(requestId, cancellationToken);
            try
            {
                var finalParameters = ConvertNamedParameters(namedParameters);

                // Invoke the method (static or instance)
                var result = await InvokeDict(finalParameters, cancellationToken);
                return ProcessInvokeResult(requestId, result);
            }
            catch (ArgumentException ex)
            {
                var errorMessage = $"Parameter validation failed for tool '{Title ?? this.Method?.Name}': {ex.Message}";
                _logger?.LogError(ex, errorMessage);
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }
            catch (JsonException ex)
            {
                var errorMessage = $"JSON parameter parsing failed for tool '{Title ?? this.Method?.Name}': {ex.Message}";
                _logger?.LogError(ex, errorMessage);
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }
            catch (ToolCallControlException) when (!(ToolCallInvocationScope.Current?.Legacy ?? true))
            {
                // Preserve stable policy errors raised by a path-aware
                // adapter so the shared pipeline can map them without
                // exposing the underlying exception.
                throw;
            }
            catch (ToolCallControlException ex)
            {
                return ResponseCallTool
                    .Error(ex.Message)
                    .SetRequestID(requestId);
            }
            catch (OperationCanceledException) when (!(ToolCallInvocationScope.Current?.Legacy ?? true))
            {
                // See the positional overload above: controlled calls must
                // surface cancellation to the shared pipeline.
                throw;
            }
            catch (Exception ex)
            {
                var controlException = FindControlException(ex);
                if (controlException != null)
                {
                    if (!(ToolCallInvocationScope.Current?.Legacy ?? true))
                        throw controlException;

                    return ResponseCallTool
                        .Error(controlException.Message)
                        .SetRequestID(requestId);
                }

                var errorMessage = $"Tool execution failed for '{Title ?? this.Method?.Name}': {(ex.InnerException ?? ex).Message}";
                _logger?.LogError(ex, $"{errorMessage}\n{ex.StackTrace}");
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }
        }

        /// <summary>
        /// Validates common parameters for tool execution.
        /// </summary>
        /// <param name="requestId">The request identifier to validate.</param>
        /// <param name="parameters">Additional parameters for context.</param>
        /// <returns>An error response if validation fails, null if validation passes.</returns>
        private ResponseCallTool? ValidateRunParameters(string requestId, object? parameters = null)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                var errorMessage = $"Request ID cannot be null or empty for tool '{Title ?? this.Method?.Name}'";
                _logger?.LogError(errorMessage);
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }

            if (this.Method == null)
            {
                var errorMessage = $"Method information is not available for tool '{Title}'";
                _logger?.LogError(errorMessage);
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }

            // Validate method is accessible
            if (!this.Method.IsPublic && !this.Method.IsFamily)
            {
                var errorMessage = $"Method '{this.Method.Name}' in tool '{Title}' is not accessible (must be public or protected)";
                _logger?.LogError(errorMessage);
                return ResponseCallTool
                    .Error(errorMessage)
                    .SetRequestID(requestId);
            }

            return null; // Validation passed
        }

        /// <summary>
        /// A path-sensitive reflected runner is only safe when entered through
        /// the policy middleware with canonical arguments. This protects the
        /// direct RunTool API and custom pipelines that accidentally omit the
        /// authoring middleware; raw caller paths are never passed to a tool.
        /// </summary>
        private ResponseCallTool? ValidateCanonicalPathScope(string requestId)
        {
            var declarations = AuthoringCapability?.PathBindings;
            if (declarations == null || declarations.Count == 0)
                return null;

            var invocation = ToolCallInvocationScope.CurrentInvocation;
            var isControlled = ToolCallInvocationScope.Current?.Legacy == false;
            if (invocation == null
                || !invocation.HasCanonicalArguments
                || !ReferenceEquals(invocation.Runner, this)
                || !string.Equals(invocation.Name, Name, StringComparison.Ordinal))
            {
                var error = new ToolCallError(
                    ToolCallErrorCodes.PathPolicyViolation,
                    "A canonical project path binding is required before this tool can run.",
                    callId: ToolCallInvocationScope.Current?.CallId,
                    correlationId: ToolCallInvocationScope.Current?.CorrelationId,
                    details: new JsonObject
                    {
                        ["reason"] = invocation == null || !invocation.HasCanonicalArguments
                            ? "missing_canonical_binding"
                            : "scope_runner_mismatch",
                    });
                if (isControlled)
                    throw new ToolCallControlException(
                        error.Code,
                        error.Message,
                        callId: error.CallId,
                        correlationId: error.CorrelationId,
                        details: error.Details);
                return ResponseCallTool.Error(error).SetRequestID(requestId);
            }

            foreach (var declaration in declarations)
            {
                if (declaration == null || string.IsNullOrWhiteSpace(declaration.ArgumentName))
                    continue;

                var hasRawArgument = TryGetArgument(
                    invocation.RawArguments,
                    declaration.ArgumentName,
                    out var rawArgument);
                var hasCanonicalBinding = invocation.PathBindings.TryGetValue(
                    declaration.ArgumentName,
                    out var canonicalBinding);
                var optionalEmptyArgument = hasRawArgument
                    && (rawArgument.ValueKind == JsonValueKind.Null
                        || (rawArgument.ValueKind == JsonValueKind.String
                            && string.Equals(rawArgument.GetString(), string.Empty, StringComparison.Ordinal)));

                if (!hasCanonicalBinding && (declaration.Required || (hasRawArgument && !optionalEmptyArgument)))
                {
                    var error = new ToolCallError(
                        ToolCallErrorCodes.PathPolicyViolation,
                        declaration.Required
                            ? "A required canonical project path binding is missing."
                            : "A canonical project path binding is missing.",
                        callId: invocation.Context.CallId,
                        correlationId: invocation.Context.CorrelationId,
                        details: new JsonObject
                        {
                            ["reason"] = declaration.Required
                                ? "required_canonical_binding_missing"
                                : "canonical_binding_missing",
                        });
                    if (isControlled)
                        throw new ToolCallControlException(
                            error.Code,
                            error.Message,
                            callId: error.CallId,
                            correlationId: error.CorrelationId,
                            details: error.Details);
                    return ResponseCallTool.Error(error).SetRequestID(requestId);
                }

                if (hasCanonicalBinding
                    && (!hasRawArgument
                        || canonicalBinding == null
                        || canonicalBinding.Resolution.Intent != (ProjectPathAccessIntent)declaration.Intent
                        || !TryGetArgument(
                            invocation.Arguments,
                            declaration.ArgumentName,
                            out var canonicalArgument)
                        || canonicalArgument.ValueKind != JsonValueKind.String
                        || !string.Equals(
                            canonicalArgument.GetString(),
                            canonicalBinding.RelativePath,
                            StringComparison.Ordinal)))
                {
                    var error = new ToolCallError(
                        ToolCallErrorCodes.PathPolicyViolation,
                        "The canonical project path binding does not match this tool call.",
                        callId: invocation.Context.CallId,
                        correlationId: invocation.Context.CorrelationId,
                        details: new JsonObject { ["reason"] = "canonical_binding_mismatch" });
                    if (isControlled)
                        throw new ToolCallControlException(
                            error.Code,
                            error.Message,
                            callId: error.CallId,
                            correlationId: error.CorrelationId,
                            details: error.Details);
                    return ResponseCallTool.Error(error).SetRequestID(requestId);
                }
            }

            return null;
        }

        /// <summary>
        /// A reflected runner must not be callable directly when its
        /// registration can mutate state. Managers enter an invocation scope
        /// before the shared safety middleware; direct callers have no trusted
        /// policy decision and therefore fail closed.
        /// </summary>
        private ResponseCallTool? ValidateAuthoringScope(string requestId)
        {
            var capability = AuthoringCapability;
            var mutationCapable = capability == null
                ? ReadOnlyHint != true
                : capability.IsOpaque || (capability.MutationKind != AuthoringMutationKind.Read);
            if (!mutationCapable)
                return null;

            var invocation = ToolCallInvocationScope.CurrentInvocation;
            if (invocation != null
                && invocation.PolicyApproved
                && ReferenceEquals(invocation.Runner, this)
                && string.Equals(invocation.Name, Name, StringComparison.Ordinal))
                return null;

            var context = ToolCallInvocationScope.Current;
            var error = new ToolCallError(
                ToolCallErrorCodes.SafetyUnsupported,
                "Tool invocation must enter the authoring safety pipeline.",
                callId: context?.CallId,
                correlationId: context?.CorrelationId,
                details: new JsonObject { ["reason"] = "direct_runner_bypass" });
            if (context?.Legacy == false)
                throw new ToolCallControlException(
                    error.Code,
                    error.Message,
                    callId: error.CallId,
                    correlationId: error.CorrelationId,
                    details: error.Details);
            return ResponseCallTool.Error(error).SetRequestID(requestId);
        }

        private static bool TryGetArgument(
            IReadOnlyDictionary<string, JsonElement> arguments,
            string argumentName,
            out JsonElement value)
        {
            if (arguments.TryGetValue(argumentName, out value))
                return true;

            foreach (var argument in arguments)
            {
                if (string.Equals(argument.Key, argumentName, StringComparison.OrdinalIgnoreCase))
                {
                    value = argument.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static ToolCallControlException? FindControlException(Exception exception)
        {
            var current = exception;
            while (current != null)
            {
                if (current is ToolCallControlException controlException)
                    return controlException;
                current = current.InnerException;
            }

            return null;
        }

        /// <summary>
        /// Converts named parameters from JsonElement dictionary to object dictionary with improved error handling.
        /// Also normalizes parameter names using case-insensitive matching when there's no conflict.
        /// </summary>
        /// <param name="namedParameters">The named parameters to convert.</param>
        /// <returns>A dictionary with object values.</returns>
        private Dictionary<string, object?>? ConvertNamedParameters(IReadOnlyDictionary<string, JsonElement>? namedParameters)
        {
            if (namedParameters == null)
                return null;

            try
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var kvp in namedParameters)
                {
                    var normalizedKey = ParameterNameUtils.NormalizeParameterName(kvp.Key, _paramNameLookup);

                    // Check for duplicate keys after normalization (e.g., LLM provided both "value" and "VALUE")
                    if (result.ContainsKey(normalizedKey))
                    {
                        throw new ArgumentException(
                            $"Duplicate parameter detected after case-insensitive normalization: '{kvp.Key}' normalizes to '{normalizedKey}' which already exists. " +
                            $"Please provide each parameter only once.");
                    }

                    result[normalizedKey] = kvp.Value;
                }

                return result;
            }
            catch (ArgumentException)
            {
                throw; // Re-throw ArgumentException as-is
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Failed to convert named parameters: {ex.Message}", ex);
            }
        }
    }
}
