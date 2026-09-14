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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace com.AtelierAI.Uco.Framework.Common.Model
{
    /// <summary>Normalized request and its runtime-only execution context.</summary>
    public sealed class ToolCallNormalizationResult
    {
        public RequestCallTool Request { get; }
        public ToolCallContext Context { get; }

        public ToolCallNormalizationResult(RequestCallTool request, ToolCallContext context)
        {
            Request = request ?? throw new ArgumentNullException(nameof(request));
            Context = context ?? throw new ArgumentNullException(nameof(context));
        }
    }

    /// <summary>
    /// Shared v1 normalization for Node/Unity call adapters.  It keeps the
    /// compatibility request id separate from the logical call id and never
    /// treats a WebSocket envelope id as either one.
    /// </summary>
    public static class ToolCallContextNormalizer
    {
        public const int SupportedVersion = ToolCallControl.CurrentVersion;

        /// <summary>
        /// Normalize a raw JSON request and map malformed known members to the
        /// same controlled error used by typed callers.
        /// </summary>
        public static ToolCallNormalizationResult Normalize(
            JsonElement requestElement,
            CancellationToken cancellationToken = default,
            Func<string>? idFactory = null)
        {
            if (requestElement.ValueKind != JsonValueKind.Object)
                throw Invalid("Tool call request must be an object.");

            try
            {
                // System.Text.Json treats JSON null as the default for some
                // non-nullable value properties. Inspect the raw control
                // object first so an explicit null version is rejected as a
                // malformed known field instead of being mistaken for a
                // supported/default version.
                var rawControl = TryGetProperty(requestElement, "control");
                var rawVersion = rawControl.HasValue && rawControl.Value.ValueKind == JsonValueKind.Object
                    ? TryGetProperty(rawControl.Value, "version")
                    : null;
                if (rawVersion.HasValue && rawVersion.Value.ValueKind == JsonValueKind.Null)
                {
                    var identity = ExtractRequestIdentity(requestElement);
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.InvalidControl,
                        "Tool call control version must be an integer.",
                        callId: identity.CallId ?? identity.RequestId,
                        correlationId: identity.CorrelationId ?? identity.CallId ?? identity.RequestId);
                }

                // Nullable CLR properties cannot distinguish an omitted
                // member from an explicit JSON null. Inspect the raw object
                // so malformed authoring controls fail before lookup or
                // execution rather than silently becoming defaults.
                if (rawControl.HasValue && rawControl.Value.ValueKind == JsonValueKind.Object)
                {
                    var identity = ExtractRequestIdentity(requestElement);
                    foreach (var member in new[] { "confirm", "dryRun", "confirmation" })
                    {
                        var rawValue = TryGetProperty(rawControl.Value, member);
                        if (rawValue.HasValue && rawValue.Value.ValueKind == JsonValueKind.Null)
                            throw new ToolCallControlException(
                                ToolCallErrorCodes.InvalidControl,
                                member + " must not be null when supplied.",
                                callId: identity.CallId ?? identity.RequestId,
                                correlationId: identity.CorrelationId ?? identity.CallId ?? identity.RequestId);
                    }
                }

                var request = JsonSerializer.Deserialize<RequestCallTool>(
                    requestElement.GetRawText(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return Normalize(request!, cancellationToken, idFactory);
            }
            catch (JsonException exception)
            {
                var identity = ExtractRequestIdentity(requestElement);
                throw new ToolCallControlException(
                    ToolCallErrorCodes.InvalidControl,
                    "Tool call control metadata is invalid.",
                    callId: identity.CallId ?? identity.RequestId,
                    correlationId: identity.CorrelationId ?? identity.CallId ?? identity.RequestId,
                    innerException: exception);
            }
        }

        public static ToolCallNormalizationResult Normalize(
            RequestCallTool request,
            CancellationToken cancellationToken = default,
            Func<string>? idFactory = null)
        {
            if (request == null)
                throw Invalid("Tool call request must not be null.");
            if (string.IsNullOrWhiteSpace(request.Name))
                throw Invalid("Tool call name must be a non-empty string.");
            if (request.Arguments == null)
                throw Invalid("Tool call arguments must not be null.");

            if (request.Control == null)
            {
                var requestId = NormalizeLegacyRequestId(request.RequestID, idFactory);
                var normalizedRequest = new RequestCallTool(requestId, request.Name, request.Arguments);
                var context = CreateContext(
                    requestId: requestId,
                    callId: requestId,
                    correlationId: requestId,
                    parentCallId: null,
                    deadlineUnixMs: null,
                    cancellationId: null,
                    idempotencyKey: null,
                    confirm: null,
                    dryRun: "none",
                    confirmation: null,
                    cancellationToken: cancellationToken,
                    legacy: true,
                    unknownMembers: null);
                return new ToolCallNormalizationResult(normalizedRequest, context);
            }

            var normalized = NormalizeControl(
                request.Control,
                request.RequestID,
                cancellationToken,
                idFactory);
            var controlledRequest = new RequestCallTool(
                normalized.Context.RequestID,
                request.Name,
                request.Arguments,
                normalized.Control);
            return new ToolCallNormalizationResult(controlledRequest, normalized.Context);
        }

        public static ToolCallNormalizationResult NormalizeRequest(
            RequestCallTool request,
            CancellationToken cancellationToken = default,
            Func<string>? idFactory = null)
            => Normalize(request, cancellationToken, idFactory);

        /// <summary>Normalize a standalone control envelope into a runtime context.</summary>
        public static ToolCallContext Normalize(
            ToolCallControl control,
            string? requestId = null,
            CancellationToken cancellationToken = default,
            Func<string>? idFactory = null)
        {
            if (control == null)
                throw Invalid("Tool call control must not be null.");

            return NormalizeControl(control, requestId, cancellationToken, idFactory).Context;
        }

        public static ToolCallContext NormalizeContext(
            RequestCallTool request,
            CancellationToken cancellationToken = default,
            Func<string>? idFactory = null)
            => Normalize(request, cancellationToken, idFactory).Context;

        /// <summary>
        /// Derive one internal/batch child context.  Child ids are fresh,
        /// correlation is inherited, deadlines are bounded by the parent,
        /// and idempotency keys are intentionally not inherited.
        /// </summary>
        public static ToolCallContext DeriveChild(
            ToolCallContext parent,
            ToolCallControl? childControl = null,
            string? requestId = null,
            CancellationToken cancellationToken = default,
            Func<string>? idFactory = null)
        {
            if (parent == null)
                throw Invalid("Parent tool call context must not be null.");
            ValidateRequiredId(parent.CallId, "callId");
            ValidateRequiredId(parent.CorrelationId, "correlationId");

            var child = childControl ?? new ToolCallControl();
            var checkedChild = NormalizeControl(
                child,
                requestId,
                cancellationToken.CanBeCanceled ? cancellationToken : parent.CancellationToken,
                idFactory);

            var callId = checkedChild.Context.CallId;
            if (string.Equals(callId, parent.CallId, StringComparison.Ordinal))
                callId = GenerateFreshId(idFactory, parent.CallId);

            var effectiveRequestId = !string.IsNullOrWhiteSpace(requestId)
                ? requestId!
                : callId;
            ValidateRequiredId(effectiveRequestId, "requestID");

            var derived = CreateContext(
                requestId: effectiveRequestId,
                callId: callId,
                correlationId: parent.CorrelationId,
                parentCallId: parent.CallId,
                deadlineUnixMs: EarlierDeadline(parent.DeadlineUnixMs, checkedChild.Context.DeadlineUnixMs),
                cancellationId: checkedChild.Context.CancellationId,
                idempotencyKey: checkedChild.Context.IdempotencyKey,
                confirm: checkedChild.Context.Confirm,
                dryRun: checkedChild.Context.DryRun,
                confirmation: checkedChild.Context.Confirmation,
                cancellationToken: cancellationToken.CanBeCanceled ? cancellationToken : parent.CancellationToken,
                legacy: false,
                unknownMembers: checkedChild.Context.UnknownMembers);
            derived.IssueConfirmationOnly = child.IssueConfirmationOnly;
            return derived;
        }

        public static ToolCallContext DeriveChildContext(
            ToolCallContext parent,
            ToolCallControl? childControl = null,
            string? requestId = null,
            CancellationToken cancellationToken = default,
            Func<string>? idFactory = null)
            => DeriveChild(parent, childControl, requestId, cancellationToken, idFactory);

        private static (ToolCallControl Control, ToolCallContext Context) NormalizeControl(
            ToolCallControl control,
            string? requestId,
            CancellationToken cancellationToken,
            Func<string>? idFactory)
        {
            var effectiveRequestId = NormalizeOptionalRequestId(requestId);
            var suppliedCallId = NormalizeOptionalId(control.CallId, "callId");
            var suppliedCorrelationId = NormalizeOptionalId(control.CorrelationId, "correlationId");

            // Validate the version after extracting valid logical ids so a
            // future-version rejection remains correlated to the call that
            // supplied it. Do not generate ids solely for a rejection path.
            ValidateVersion(
                control.Version,
                suppliedCallId ?? effectiveRequestId,
                suppliedCorrelationId ?? suppliedCallId ?? effectiveRequestId);

            var callId = suppliedCallId
                ?? effectiveRequestId
                ?? GenerateId(idFactory);
            ValidateRequiredId(callId, "callId");

            var correlationId = suppliedCorrelationId ?? callId;
            ValidateRequiredId(correlationId, "correlationId");

            var parentCallId = NormalizeOptionalId(control.ParentCallId, "parentCallId");
            var deadline = NormalizeDeadline(control.DeadlineUnixMs);
            var effectiveCancellationId = NormalizeOpaqueString(control.CancellationId);
            var idempotencyKey = NormalizeOpaqueString(control.IdempotencyKey);
            var confirm = NormalizeConfirm(control.Confirm);
            var dryRun = NormalizeDryRun(control.DryRun);
            var confirmation = NormalizeConfirmation(control.Confirmation);
            effectiveRequestId ??= callId;

            var unknownMembers = CloneUnknownMembers(control.UnknownMembers);
            var normalizedControl = new ToolCallControl
            {
                Version = SupportedVersion,
                CallId = callId,
                CorrelationId = correlationId,
                ParentCallId = parentCallId,
                DeadlineUnixMs = deadline,
                CancellationId = effectiveCancellationId,
                IdempotencyKey = idempotencyKey,
                Confirm = confirm,
                DryRun = control.DryRun == null ? null : dryRun,
                Confirmation = confirmation,
                IssueConfirmationOnly = control.IssueConfirmationOnly,
                UnknownMembers = unknownMembers,
            };

            var context = CreateContext(
                requestId: effectiveRequestId,
                callId: callId,
                correlationId: correlationId,
                parentCallId: parentCallId,
                deadlineUnixMs: deadline,
                cancellationId: effectiveCancellationId,
                idempotencyKey: idempotencyKey,
                confirm: confirm,
                dryRun: dryRun,
                confirmation: confirmation,
                cancellationToken: cancellationToken,
                legacy: false,
                unknownMembers: unknownMembers);
            context.IssueConfirmationOnly = control.IssueConfirmationOnly;
            return (normalizedControl, context);
        }

        private static ToolCallContext CreateContext(
            string requestId,
            string callId,
            string correlationId,
            string? parentCallId,
            long? deadlineUnixMs,
            string? cancellationId,
            string? idempotencyKey,
            bool? confirm,
            string dryRun,
            ToolCallConfirmation? confirmation,
            CancellationToken cancellationToken,
            bool legacy,
            IDictionary<string, JsonElement>? unknownMembers)
        {
            var context = new ToolCallContext
            {
                Version = SupportedVersion,
                RequestID = requestId,
                CallId = callId,
                CorrelationId = correlationId,
                ParentCallId = parentCallId,
                DeadlineUnixMs = deadlineUnixMs,
                CancellationId = cancellationId,
                IdempotencyKey = idempotencyKey,
                Confirm = confirm,
                DryRun = dryRun,
                Confirmation = confirmation?.Clone(),
                CancellationToken = cancellationToken,
                Legacy = legacy,
            };

            if (unknownMembers != null)
            {
                foreach (var member in unknownMembers)
                    context.UnknownMembers[member.Key] = member.Value.Clone();
            }

            return context;
        }

        private static string NormalizeLegacyRequestId(string requestId, Func<string>? idFactory)
        {
            if (!string.IsNullOrWhiteSpace(requestId)) return requestId;
            return GenerateId(idFactory);
        }

        private static string? NormalizeOptionalRequestId(string? requestId)
        {
            if (requestId == null || requestId.Length == 0) return null;
            ValidateRequiredId(requestId, "requestID");
            return requestId;
        }

        private static string? NormalizeOptionalId(string? value, string member)
        {
            if (value == null) return null;
            ValidateRequiredId(value, member);
            return value;
        }

        private static string? NormalizeOpaqueString(string? value)
        {
            if (value == null) return null;
            if (value.Length == 0)
                return value;
            // Opaque metadata must remain a string, and is intentionally not
            // trimmed or otherwise interpreted.
            return value;
        }

        private static long? NormalizeDeadline(long? value)
        {
            if (!value.HasValue) return null;
            if (value.Value < 0)
                throw Invalid("deadlineUnixMs must be a non-negative integer.");
            return value;
        }

        private static bool? NormalizeConfirm(bool? value)
            => value;

        private static string NormalizeDryRun(string? value)
        {
            // A typed caller cannot distinguish omitted from null. The raw
            // JsonElement overload rejects explicit null before deserialization;
            // typed callers receive the documented omitted-value default.
            if (value == null) return "none";
            if (value == "none" || value == "validate" || value == "plan")
                return value;
            throw Invalid("dryRun must be one of: none, validate, plan.");
        }

        private static ToolCallConfirmation? NormalizeConfirmation(ToolCallConfirmation? value)
        {
            if (value == null) return null;
            if (string.IsNullOrWhiteSpace(value.PlanId))
                throw Invalid("confirmation.planId must be a non-empty string.");
            if (string.IsNullOrWhiteSpace(value.PlanHash))
                throw Invalid("confirmation.planHash must be a non-empty string.");
            if (!value.ExpiresAtUnixMs.HasValue || value.ExpiresAtUnixMs.Value < 0)
                throw Invalid("confirmation.expiresAtUnixMs must be a non-negative integer.");
            return value.Clone();
        }

        private static void ValidateVersion(
            int version,
            string? callId = null,
            string? correlationId = null)
        {
            if (version == SupportedVersion) return;
            throw new ToolCallControlException(
                ToolCallErrorCodes.UnsupportedControlVersion,
                "Tool call control version is not supported.",
                retryable: false,
                callId: callId,
                correlationId: correlationId,
                details: new JsonObject
                {
                    ["supportedVersion"] = SupportedVersion,
                    ["receivedVersion"] = version,
                });
        }

        private static void ValidateRequiredId(string? value, string member)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw Invalid($"{member} must be a non-empty string.");
        }

        private static string GenerateId(Func<string>? idFactory)
        {
            var id = idFactory == null ? Guid.NewGuid().ToString() : idFactory();
            ValidateRequiredId(id, "generated id");
            return id;
        }

        private static string GenerateFreshId(Func<string>? idFactory, string existingId)
        {
            var id = GenerateId(idFactory);
            if (string.Equals(id, existingId, StringComparison.Ordinal))
                throw Invalid("Child call id must differ from its parent call id.");
            return id;
        }

        private static long? EarlierDeadline(long? parent, long? child)
        {
            if (!parent.HasValue) return child;
            if (!child.HasValue) return parent;
            return Math.Min(parent.Value, child.Value);
        }

        private static Dictionary<string, JsonElement> CloneUnknownMembers(
            IDictionary<string, JsonElement>? members)
        {
            var clone = new Dictionary<string, JsonElement>();
            if (members == null) return clone;
            foreach (var member in members)
                clone[member.Key] = member.Value.Clone();
            return clone;
        }

        private static ToolCallControlException Invalid(string message)
            => new ToolCallControlException(ToolCallErrorCodes.InvalidControl, message);

        /// <summary>
        /// Extracts only valid string identity fields from a malformed raw
        /// request. This is deliberately best-effort: it is used solely to
        /// correlate a validation error and must never make malformed input
        /// executable.
        /// </summary>
        private static (string? RequestId, string? CallId, string? CorrelationId)
            ExtractRequestIdentity(JsonElement requestElement)
        {
            if (requestElement.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            var requestId = TryGetStringProperty(requestElement, "requestID");
            string? callId = null;
            string? correlationId = null;

            var control = TryGetProperty(requestElement, "control");
            if (control.HasValue && control.Value.ValueKind == JsonValueKind.Object)
            {
                callId = TryGetStringProperty(control.Value, "callId");
                correlationId = TryGetStringProperty(control.Value, "correlationId");
            }

            return (requestId, callId, correlationId);
        }

        private static JsonElement? TryGetProperty(JsonElement element, string name)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }

            return null;
        }

        private static string? TryGetStringProperty(JsonElement element, string name)
        {
            var property = TryGetProperty(element, name);
            return property.HasValue && property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
        }
    }
}
