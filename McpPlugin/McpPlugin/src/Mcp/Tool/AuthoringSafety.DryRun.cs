/*
┌────────────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)                   │
│  Copyright (c) 2025 Ivan Murzak                                        │
│  Licensed under the MIT License.                                      │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using com.IvanMurzak.McpPlugin.Common.Model;

namespace com.IvanMurzak.McpPlugin
{
    internal static class AuthoringSensitiveNames
    {
        private static readonly HashSet<string> CredentialNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "access-token", "authorization", "bearer", "token", "password", "secret", "api-key",
            "android-keystore-base64", "android-keystore-password", "android-key-alias-password",
        };

        public static bool IsCredential(string? value)
            => value != null && CredentialNames.Contains(Canonicalize(value));

        private static string Canonicalize(string value)
        {
            var normalized = new StringBuilder(value.Length + 4);
            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                if (current == '-' || current == '_' || current == '.' || current == ' ')
                {
                    if (normalized.Length > 0 && normalized[normalized.Length - 1] != '-')
                        normalized.Append('-');
                    continue;
                }

                var startsCamelWord = char.IsUpper(current)
                    && index > 0
                    && value[index - 1] != '-'
                    && value[index - 1] != '_'
                    && value[index - 1] != '.'
                    && value[index - 1] != ' '
                    && (char.IsLower(value[index - 1])
                        || (char.IsUpper(value[index - 1])
                            && index + 1 < value.Length
                            && char.IsLower(value[index + 1])));
                if (startsCamelWord && normalized.Length > 0 && normalized[normalized.Length - 1] != '-')
                    normalized.Append('-');

                normalized.Append(char.ToLowerInvariant(current));
            }

            if (normalized.Length > 0 && normalized[normalized.Length - 1] == '-')
                normalized.Length--;
            return normalized.ToString();
        }
    }

    /// <summary>
    /// Clones adapter-provided JSON into a small, safe diagnostic tree. Plan
    /// details are caller-visible, so arbitrary adapter JSON must not be able
    /// to smuggle host paths, credentials, or an unbounded object graph onto
    /// the wire.
    /// </summary>
    internal static class AuthoringSafeJson
    {
        private const int MaxDepth = 6;
        private const int MaxMembers = 32;
        private const int MaxArrayItems = 32;
        private const int MaxStringLength = 160;

        public static JsonObject? CloneObject(JsonObject? value)
            => Clone(value, 0, propertyName: null) as JsonObject;

        private static JsonNode? Clone(JsonNode? value, int depth, string? propertyName)
        {
            if (value == null) return null;
            if (depth > MaxDepth) return null;

            if (value is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(MaxMembers))
                {
                    if (AuthoringSensitiveNames.IsCredential(property.Key))
                        continue;

                    // A property explicitly named as a host/absolute path is
                    // not safe diagnostic data. Relative project paths remain
                    // useful and are bounded by the target/path sanitizers.
                    if (IsSensitivePathName(property.Key)
                        && property.Value is JsonValue pathValue
                        && pathValue.TryGetValue<string>(out var path)
                        && IsAbsoluteLike(path))
                        continue;

                    result[property.Key] = Clone(property.Value, depth + 1, property.Key);
                }
                return result;
            }

            if (value is JsonArray array)
            {
                var result = new JsonArray();
                foreach (var item in array.Take(MaxArrayItems))
                    result.Add(Clone(item, depth + 1, propertyName));
                return result;
            }

            if (value is JsonValue scalar)
            {
                if (scalar.TryGetValue<string>(out var text))
                {
                    var bounded = text.Length <= MaxStringLength
                        ? text
                        : text.Substring(0, MaxStringLength);
                    if (IsSensitivePathName(propertyName)
                        && IsAbsoluteLike(bounded))
                        return null;
                    return JsonValue.Create(bounded);
                }

                // JsonValue.Clone is not available on every Unity runtime
                // supported by the common assembly. Round-trip the scalar
                // through its JSON representation instead.
                try { return JsonNode.Parse(scalar.ToJsonString()); }
                catch { return null; }
            }

            return null;
        }

        private static bool IsSensitivePathName(string? name)
            => name != null
                && (name.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("root", StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool IsAbsoluteLike(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value[0] == '/' || value[0] == '\\') return true;
            if (value.Length > 1 && value[1] == ':') return true;
            return value.StartsWith("//", StringComparison.Ordinal)
                || value.StartsWith("\\\\", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Produces an exact, bounded retry envelope from the normalized context.
    /// Unsafe values are never truncated or redacted because either operation
    /// would change the confirmation binding; the copyable retry is suppressed
    /// instead.
    /// </summary>
    internal static class AuthoringConfirmationRetry
    {
        private const int MaxDepth = 6;
        private const int MaxMembers = 32;
        private const int MaxArrayItems = 32;
        private const int MaxNodes = 256;
        private const int MaxStringLength = 160;
        private const int MaxPayloadBytes = 16_384;

        private static readonly HashSet<string> ReservedControlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "version", "requestID", "callId", "correlationId", "parentCallId", "deadlineUnixMs",
            "cancellationId", "idempotencyKey", "confirm", "dryRun", "confirmation",
            "cancellationToken", "legacy", "issueConfirmationOnly", "unknownMembers", "signal", "abortSignal",
        };

        public static bool TryBuild(
            ToolCallContext context,
            ConfirmationPlanRecord record,
            out JsonObject? retryWith,
            out string? suppressionReason)
        {
            retryWith = null;
            suppressionReason = null;
            var remainingNodes = MaxNodes;
            if (!SafeString(context.RequestID)
                || !SafeString(context.CallId)
                || !SafeString(context.CorrelationId)
                || !SafeOptionalString(context.ParentCallId)
                || !SafeOptionalString(context.CancellationId)
                || !SafeOptionalString(context.IdempotencyKey))
            {
                suppressionReason = "retry_control_sensitive_or_unbounded";
                return false;
            }

            if (context.UnknownMembers.Keys.Any(ReservedControlNames.Contains))
            {
                suppressionReason = "retry_control_member_collision";
                return false;
            }

            if (context.UnknownMembers.Count > MaxMembers)
            {
                suppressionReason = "retry_control_limits_exceeded";
                return false;
            }

            var control = new JsonObject();
            foreach (var member in context.UnknownMembers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var sensitiveNameFound = IsSensitiveName(member.Key);
                if (!SafePropertyName(member.Key)
                    || !IsSafe(member.Value, 0, member.Key, ref remainingNodes, ref sensitiveNameFound))
                {
                    suppressionReason = sensitiveNameFound
                        ? "retry_control_sensitive_or_unbounded"
                        : "retry_control_limits_exceeded";
                    return false;
                }

                try { control[member.Key] = JsonNode.Parse(member.Value.GetRawText()); }
                catch
                {
                    suppressionReason = "retry_control_serialization_failed";
                    return false;
                }
            }

            control["version"] = context.Version;
            control["callId"] = context.CallId;
            control["correlationId"] = context.CorrelationId;
            if (context.ParentCallId != null) control["parentCallId"] = context.ParentCallId;
            if (context.DeadlineUnixMs.HasValue) control["deadlineUnixMs"] = context.DeadlineUnixMs.Value;
            if (context.CancellationId != null) control["cancellationId"] = context.CancellationId;
            if (context.IdempotencyKey != null) control["idempotencyKey"] = context.IdempotencyKey;
            control["confirm"] = true;
            control["dryRun"] = "none";
            control["confirmation"] = new JsonObject
            {
                ["planId"] = record.PlanId,
                ["planHash"] = record.PlanHash,
                ["expiresAtUnixMs"] = record.ExpiresAtUnixMs,
            };

            retryWith = new JsonObject
            {
                ["requestID"] = context.RequestID,
                ["control"] = control,
            };
            try
            {
                if (Encoding.UTF8.GetByteCount(retryWith.ToJsonString()) <= MaxPayloadBytes)
                    return true;
            }
            catch
            {
                suppressionReason = "retry_control_serialization_failed";
                retryWith = null;
                return false;
            }

            suppressionReason = "retry_control_limits_exceeded";
            retryWith = null;
            return false;
        }

        private static bool IsSafe(
            JsonElement value,
            int depth,
            string? propertyName,
            ref int remainingNodes,
            ref bool sensitiveNameFound)
        {
            if (depth > MaxDepth || --remainingNodes < 0) return false;
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    var properties = value.EnumerateObject().ToArray();
                    if (properties.Length > MaxMembers) return false;
                    foreach (var property in properties)
                    {
                        if (IsSensitiveName(property.Name))
                        {
                            sensitiveNameFound = true;
                            return false;
                        }
                        if (!SafePropertyName(property.Name)
                            || !IsSafe(
                                property.Value,
                                depth + 1,
                                property.Name,
                                ref remainingNodes,
                                ref sensitiveNameFound))
                            return false;
                    }
                    return true;
                case JsonValueKind.Array:
                    var items = value.EnumerateArray().ToArray();
                    if (items.Length > MaxArrayItems) return false;
                    foreach (var item in items)
                    {
                        if (!IsSafe(item, depth + 1, propertyName, ref remainingNodes, ref sensitiveNameFound))
                            return false;
                    }
                    return true;
                case JsonValueKind.String:
                    var text = value.GetString() ?? string.Empty;
                    if (!SafeString(text)) return false;
                    return !IsSensitivePathName(propertyName) || !IsAbsoluteLike(text);
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                    return true;
                default:
                    return false;
            }
        }

        private static bool SafePropertyName(string value)
            => value.Length <= MaxStringLength && !IsSensitiveName(value);

        private static bool SafeOptionalString(string? value)
            => value == null || SafeString(value);

        private static bool SafeString(string value)
            => value.Length <= MaxStringLength
                && value.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("access_token=", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("accessToken=", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("api_key=", StringComparison.OrdinalIgnoreCase) < 0;

        private static bool IsSensitiveName(string? value)
            => AuthoringSensitiveNames.IsCredential(value);

        private static bool IsSensitivePathName(string? name)
            => name != null
                && (name.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.IndexOf("root", StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool IsAbsoluteLike(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value[0] == '/' || value[0] == '\\') return true;
            if (value.Length > 1 && value[1] == ':') return true;
            return value.StartsWith("//", StringComparison.Ordinal)
                || value.StartsWith("\\\\", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The process-local record behind one confirmation token.  It contains
    /// only bounded plan data and digests; it is deliberately not an
    /// operation/progress registry and is invalidated when this store is
    /// discarded (for example after a Unity domain reload).
    /// </summary>
    public sealed class ConfirmationPlanRecord
    {
        public string PlanId { get; }
        public string PlanHash { get; }
        public long ExpiresAtUnixMs { get; }
        public int PolicyVersion { get; }
        public string InstanceId { get; }
        public AuthoringPlanSummary Summary { get; }
        /// <summary>Monotonic issue order used for bounded-capacity eviction.</summary>
        internal long Sequence { get; }

        internal ConfirmationPlanRecord(
            string planId,
            string planHash,
            long expiresAtUnixMs,
            int policyVersion,
            string instanceId,
            AuthoringPlanSummary summary,
            long sequence = 0)
        {
            PlanId = planId;
            PlanHash = planHash;
            ExpiresAtUnixMs = expiresAtUnixMs;
            PolicyVersion = policyVersion;
            InstanceId = instanceId;
            Summary = summary.CloneBounded();
            Sequence = sequence;
        }
    }

    /// <summary>
    /// Bounded in-memory confirmation-plan cache.  The cache is intentionally
    /// injectable so tests can use a deterministic clock and a short expiry,
    /// while production gets a five-minute default.  Capacity is explicit:
    /// when the bound is reached the oldest unexpired plan is evicted, so an
    /// unbounded stream of <c>dryRun=plan</c> requests cannot grow Editor
    /// memory.  The store is process-local; a new instance (for example
    /// after a Unity domain reload or restart) has a fresh instance id and
    /// recognises no previously issued token.
    /// </summary>
    public sealed class ConfirmationPlanStore : IDisposable
    {
        public static readonly TimeSpan DefaultPlanLifetime = TimeSpan.FromMinutes(5);
        public const int DefaultMaxRecords = 256;

        private readonly object _gate = new object();
        private readonly Dictionary<string, ConfirmationPlanRecord> _records =
            new Dictionary<string, ConfirmationPlanRecord>(StringComparer.Ordinal);
        private readonly Func<long> _clock;
        private readonly TimeSpan _defaultLifetime;
        private readonly int _maxRecords;
        private readonly string _instanceId = "instance-" + Guid.NewGuid().ToString("N");
        private long _sequence;
        private long _generation;
        private bool _disposed;

        public ConfirmationPlanStore()
            : this(DefaultPlanLifetime, null)
        {
        }

        public ConfirmationPlanStore(
            TimeSpan defaultLifetime,
            Func<long>? clock = null,
            int maxRecords = DefaultMaxRecords)
        {
            if (defaultLifetime <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(defaultLifetime));
            if (maxRecords <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxRecords));

            _defaultLifetime = defaultLifetime;
            _maxRecords = maxRecords;
            _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public string InstanceId => _instanceId;

        public TimeSpan DefaultLifetime => _defaultLifetime;

        public int MaxRecords => _maxRecords;

        /// <summary>
        /// Incremented by <see cref="Invalidate"/>. Diagnostics only; tokens
        /// are never bound to it because an invalidated store simply forgets
        /// every plan.
        /// </summary>
        public long Generation
        {
            get { lock (_gate) return _generation; }
        }

        /// <summary>Current clock value used for deterministic policy tests.</summary>
        public long NowUnixMs() => _clock();

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    PruneExpiredLocked(_clock());
                    return _records.Count;
                }
            }
        }

        /// <summary>Issues a fresh random plan id and stores the binding hash.</summary>
        public ConfirmationPlanRecord Issue(
            string planHash,
            AuthoringPlanSummary summary,
            int policyVersion,
            long? deadlineUnixMs = null)
        {
            if (string.IsNullOrWhiteSpace(planHash))
                throw new ArgumentException("Plan hash must be non-empty.", nameof(planHash));
            if (summary == null)
                throw new ArgumentNullException(nameof(summary));

            var now = _clock();
            var lifetimeMs = checked((long)Math.Max(1d, _defaultLifetime.TotalMilliseconds));
            var expiresAt = checked(now + lifetimeMs);
            if (deadlineUnixMs.HasValue)
                expiresAt = Math.Min(expiresAt, deadlineUnixMs.Value);

            // A plan that is already expired when issued can never be
            // confirmed. Refuse it instead of storing a dead record that a
            // caller could mistake for an approval window.
            if (expiresAt <= now)
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.DeadlineExceeded,
                    "The call deadline leaves no time to confirm a plan.",
                    details: new JsonObject { ["reason"] = "plan_expired_at_issue" });
            }

            var planId = "plan-" + Guid.NewGuid().ToString("N");
            var bounded = summary.CloneBounded();
            bounded.PlanId = planId;
            bounded.PlanHash = planHash;
            bounded.PolicyVersion = policyVersion;
            bounded.ExpiresAtUnixMs = expiresAt;

            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpiredLocked(now);
                var record = new ConfirmationPlanRecord(
                    planId,
                    planHash,
                    expiresAt,
                    policyVersion,
                    _instanceId,
                    bounded,
                    ++_sequence);
                EvictToCapacityLocked();
                _records[planId] = record;
                return record;
            }
        }

        public ConfirmationPlanRecord IssueBound(
            Func<string, long, string> computePlanHash,
            AuthoringPlanSummary summary,
            int policyVersion,
            long? deadlineUnixMs = null)
        {
            if (computePlanHash == null)
                throw new ArgumentNullException(nameof(computePlanHash));
            if (summary == null)
                throw new ArgumentNullException(nameof(summary));

            var now = _clock();
            var lifetimeMs = checked((long)Math.Max(1d, _defaultLifetime.TotalMilliseconds));
            var expiresAt = checked(now + lifetimeMs);
            if (deadlineUnixMs.HasValue)
                expiresAt = Math.Min(expiresAt, deadlineUnixMs.Value);
            if (expiresAt <= now)
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.DeadlineExceeded,
                    "The call deadline leaves no time to confirm a plan.",
                    details: new JsonObject { ["reason"] = "plan_expired_at_issue" });
            }

            var planId = "plan-" + Guid.NewGuid().ToString("N");
            var planHash = computePlanHash(planId, expiresAt);
            if (string.IsNullOrWhiteSpace(planHash))
                throw new InvalidOperationException("The confirmation binding hash was empty.");
            var bounded = summary.CloneBounded();
            bounded.PlanId = planId;
            bounded.PlanHash = planHash;
            bounded.PolicyVersion = policyVersion;
            bounded.ExpiresAtUnixMs = expiresAt;

            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpiredLocked(now);
                var record = new ConfirmationPlanRecord(
                    planId,
                    planHash,
                    expiresAt,
                    policyVersion,
                    _instanceId,
                    bounded,
                    ++_sequence);
                EvictToCapacityLocked();
                _records[planId] = record;
                return record;
            }
        }

        /// <summary>
        /// Convenience overload used by adapters that already have an
        /// absolute expiry (for example, a test or an imported plan).
        /// </summary>
        public ConfirmationPlanRecord Issue(
            string planId,
            string planHash,
            long expiresAtUnixMs,
            int policyVersion,
            AuthoringPlanSummary summary)
        {
            if (string.IsNullOrWhiteSpace(planId))
                throw new ArgumentException("Plan id must be non-empty.", nameof(planId));
            if (string.IsNullOrWhiteSpace(planHash))
                throw new ArgumentException("Plan hash must be non-empty.", nameof(planHash));
            if (summary == null)
                throw new ArgumentNullException(nameof(summary));

            var bounded = summary.CloneBounded();
            bounded.PlanId = planId;
            bounded.PlanHash = planHash;
            bounded.PolicyVersion = policyVersion;
            bounded.ExpiresAtUnixMs = expiresAtUnixMs;

            lock (_gate)
            {
                ThrowIfDisposed();
                PruneExpiredLocked(_clock());
                var record = new ConfirmationPlanRecord(
                    planId,
                    planHash,
                    expiresAtUnixMs,
                    policyVersion,
                    _instanceId,
                    bounded,
                    ++_sequence);
                if (!_records.ContainsKey(planId))
                    EvictToCapacityLocked();
                _records[planId] = record;
                return record;
            }
        }

        public bool TryGet(string planId, out ConfirmationPlanRecord? record)
        {
            record = null;
            if (string.IsNullOrWhiteSpace(planId))
                return false;

            lock (_gate)
            {
                if (_disposed)
                    return false;
                // Do not prune the requested record here.  The policy needs to
                // distinguish a known-but-expired token from an unknown one.
                return _records.TryGetValue(planId, out record);
            }
        }

        public bool Remove(string planId)
        {
            if (string.IsNullOrWhiteSpace(planId))
                return false;
            lock (_gate)
                return !_disposed && _records.Remove(planId);
        }

        public bool TryConsume(
            string planId,
            string planHash,
            long expiresAtUnixMs,
            out ConfirmationPlanRecord? record)
        {
            record = null;
            if (string.IsNullOrWhiteSpace(planId) || string.IsNullOrWhiteSpace(planHash))
                return false;

            lock (_gate)
            {
                if (_disposed || !_records.TryGetValue(planId, out var candidate))
                    return false;
                if (candidate.ExpiresAtUnixMs <= _clock()
                    || candidate.ExpiresAtUnixMs != expiresAtUnixMs
                    || !AuthoringConfirmationBinding.FixedTimeEquals(candidate.PlanHash, planHash))
                    return false;
                _records.Remove(planId);
                record = candidate;
                return true;
            }
        }

        /// <summary>
        /// Consumes a plan after it authorised one execution. A token is an
        /// approval of one observed action; it is not reusable as a standing
        /// capability, so a replayed token is reported as unknown.
        /// </summary>
        public bool Consume(string planId) => Remove(planId);

        public void Clear()
        {
            lock (_gate)
                _records.Clear();
        }

        /// <summary>
        /// Forgets every plan. Unity calls this before a domain reload so a
        /// token issued by the previous AppDomain cannot authorise a call
        /// after scripts, objects, or policy code may have changed.
        /// </summary>
        public void Invalidate()
        {
            lock (_gate)
            {
                _records.Clear();
                _generation++;
            }
        }

        public void PruneExpired()
        {
            lock (_gate)
            {
                if (!_disposed)
                    PruneExpiredLocked(_clock());
            }
        }

        private void PruneExpiredLocked(long now)
        {
            foreach (var key in _records
                .Where(pair => pair.Value.ExpiresAtUnixMs <= now)
                .Select(pair => pair.Key)
                .ToArray())
            {
                _records.Remove(key);
            }
        }

        private void EvictToCapacityLocked()
        {
            while (_records.Count >= _maxRecords)
            {
                string? oldestKey = null;
                var oldestSequence = long.MaxValue;
                foreach (var pair in _records)
                {
                    if (pair.Value.Sequence < oldestSequence)
                    {
                        oldestSequence = pair.Value.Sequence;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey == null)
                    break;
                _records.Remove(oldestKey);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ConfirmationPlanStore));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _records.Clear();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Canonical binding implementation shared by plan issuance and
    /// confirmation.  JSON objects are sorted recursively and paths are
    /// represented by policy-owned canonical bindings.  The resulting digest
    /// is safe to expose because only the SHA-256 value crosses the wire.
    /// </summary>
    public static class AuthoringConfirmationBinding
    {
        public static string Compute(
            AuthoringInvocation invocation,
            int policyVersion,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            AuthoringPlanLevel planLevel,
            string planId,
            long expiresAtUnixMs,
            IReadOnlyList<AuthoringTargetSummary>? targets = null,
            string? instanceId = null,
            IReadOnlyList<AuthoringChildPlanSummary>? childRecords = null,
            IReadOnlyList<string>? predictedEffects = null,
            bool supportsSharedTransaction = true)
        {
            if (invocation == null)
                throw new ArgumentNullException(nameof(invocation));

            var builder = new StringBuilder(1024);
            // Start with a fixed member because the small AppendProperty
            // helpers intentionally prepend commas.  Keeping the material
            // valid JSON makes the binding easy to audit and deterministic
            // across runtimes.
            builder.Append("{\"bindingVersion\":3");
            AppendProperty(builder, "instance", instanceId);
            AppendProperty(builder, "planId", planId);
            AppendProperty(builder, "expiresAtUnixMs", expiresAtUnixMs);
            AppendProperty(builder, "tool", invocation.Name);
            AppendBoundArguments(builder, invocation);
            AppendProperty(builder, "requestID", invocation.Context.RequestID);
            AppendProperty(builder, "callId", invocation.Context.CallId);
            AppendProperty(builder, "correlationId", invocation.Context.CorrelationId);
            AppendProperty(builder, "parentCallId", invocation.Context.ParentCallId);
            AppendProperty(builder, "deadlineUnixMs", invocation.Context.DeadlineUnixMs);
            AppendProperty(builder, "cancellationId", invocation.Context.CancellationId);
            AppendProperty(builder, "idempotencyKey", invocation.Context.IdempotencyKey);
            AppendProperty(builder, "legacy", invocation.Context.Legacy);
            AppendProperty(builder, "policyVersion", policyVersion);
            AppendProperty(builder, "risk", risk.ToWireValue());
            AppendProperty(builder, "undo", undo.ToWireValue());
            AppendProperty(builder, "planLevel", planLevel.ToWireValue());

            builder.Append(",\"unknownControl\":");
            AppendDictionary(builder, invocation.Context.UnknownMembers);

            builder.Append(",\"paths\":{");
            var firstPath = true;
            foreach (var binding in invocation.PathBindings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!firstPath) builder.Append(',');
                firstPath = false;
                AppendString(builder, binding.Key);
                builder.Append(':');
                builder.Append('{');
                AppendProperty(builder, "root", binding.Value.Resolution.RootCategory);
                AppendProperty(builder, "intent", binding.Value.Resolution.Intent.ToString());
                AppendProperty(builder, "lexicalIdentity", binding.Value.Resolution.LexicalIdentity);
                AppendProperty(builder, "targetIdentity", binding.Value.Resolution.TargetIdentity);
                builder.Append('}');
            }
            builder.Append('}');

            builder.Append(",\"targets\":");
            AppendTargets(builder, targets);
            builder.Append(",\"predictedEffects\":");
            AppendStrings(builder, predictedEffects);
            AppendProperty(builder, "supportsSharedTransaction", supportsSharedTransaction);
            builder.Append(",\"childRecords\":");
            AppendChildRecords(builder, childRecords);
            builder.Append('}');
            return Hash(builder.ToString());
        }

        public static string ComputeArgumentsHash(
            IReadOnlyDictionary<string, JsonElement> arguments)
        {
            if (arguments == null)
                throw new ArgumentNullException(nameof(arguments));
            var builder = new StringBuilder(512);
            AppendDictionary(builder, arguments);
            return Hash(builder.ToString());
        }

        public static bool FixedTimeEquals(string? left, string? right)
        {
            if (left == null || right == null)
                return left == right;
            var leftBytes = Encoding.UTF8.GetBytes(left);
            var rightBytes = Encoding.UTF8.GetBytes(right);
            return leftBytes.Length == rightBytes.Length
                && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }

        private static string Hash(string value)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (var valueByte in bytes)
                hex.Append(valueByte.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return "sha256-" + hex;
        }

        private static void AppendBoundArguments(
            StringBuilder builder,
            AuthoringInvocation invocation)
        {
            builder.Append(",\"arguments\":{");
            var first = true;
            foreach (var argument in invocation.Arguments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!first) builder.Append(',');
                first = false;
                AppendString(builder, argument.Key);
                builder.Append(':');
                if (invocation.PathBindings.ContainsKey(argument.Key))
                    AppendString(builder, "$canonical-project-path");
                else
                    AppendJson(builder, argument.Value);
            }
            builder.Append('}');
        }

        private static void AppendProperty(StringBuilder builder, string name, string? value)
        {
            builder.Append(',');
            AppendString(builder, name);
            builder.Append(':');
            if (value == null) builder.Append("null");
            else AppendString(builder, value);
        }

        private static void AppendProperty(StringBuilder builder, string name, bool value)
        {
            builder.Append(',');
            AppendString(builder, name);
            builder.Append(':').Append(value ? "true" : "false");
        }

        private static void AppendProperty(StringBuilder builder, string name, int value)
        {
            builder.Append(',');
            AppendString(builder, name);
            builder.Append(':').Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        private static void AppendProperty(StringBuilder builder, string name, long? value)
        {
            builder.Append(',');
            AppendString(builder, name);
            builder.Append(':');
            if (value.HasValue)
                builder.Append(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else
                builder.Append("null");
        }

        private static void AppendProperty(
            StringBuilder builder,
            string name,
            IReadOnlyDictionary<string, JsonElement> values)
        {
            builder.Append(',');
            AppendString(builder, name);
            builder.Append(':');
            AppendDictionary(builder, values);
        }

        private static void AppendDictionary(
            StringBuilder builder,
            IEnumerable<KeyValuePair<string, JsonElement>> values)
        {
            builder.Append('{');
            var first = true;
            foreach (var value in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!first) builder.Append(',');
                first = false;
                AppendString(builder, value.Key);
                builder.Append(':');
                AppendJson(builder, value.Value);
            }
            builder.Append('}');
        }

        private static void AppendJson(StringBuilder builder, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    builder.Append('{');
                    var firstObject = true;
                    foreach (var property in value.EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal))
                    {
                        if (!firstObject) builder.Append(',');
                        firstObject = false;
                        AppendString(builder, property.Name);
                        builder.Append(':');
                        AppendJson(builder, property.Value);
                    }
                    builder.Append('}');
                    return;
                case JsonValueKind.Array:
                    builder.Append('[');
                    var firstArray = true;
                    foreach (var item in value.EnumerateArray())
                    {
                        if (!firstArray) builder.Append(',');
                        firstArray = false;
                        AppendJson(builder, item);
                    }
                    builder.Append(']');
                    return;
                case JsonValueKind.String:
                    AppendString(builder, value.GetString());
                    return;
                case JsonValueKind.Number:
                    builder.Append(value.GetRawText());
                    return;
                case JsonValueKind.True:
                    builder.Append("true");
                    return;
                case JsonValueKind.False:
                    builder.Append("false");
                    return;
                default:
                    builder.Append("null");
                    return;
            }
        }

        private static void AppendTargets(
            StringBuilder builder,
            IReadOnlyList<AuthoringTargetSummary>? targets)
        {
            builder.Append('[');
            var first = true;
            if (targets != null)
            {
                foreach (var target in targets.Take(32))
                {
                    if (target == null) continue;
                    if (!first) builder.Append(',');
                    first = false;
                    var safe = target.CloneSafe();
                    builder.Append('{');
                    AppendProperty(builder, "kind", safe.Kind);
                    AppendProperty(builder, "name", safe.Name);
                    AppendProperty(builder, "relativePath", safe.RelativePath);
                    AppendProperty(builder, "fingerprint", safe.Fingerprint);
                    builder.Append('}');
                }
            }
            builder.Append(']');
        }

        private static void AppendStrings(StringBuilder builder, IReadOnlyList<string>? values)
        {
            builder.Append('[');
            var first = true;
            if (values != null)
            {
                foreach (var value in values.Where(value => value != null).Take(32))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    AppendString(builder, value.Length <= 160 ? value : value.Substring(0, 160));
                }
            }
            builder.Append(']');
        }

        private static void AppendChildRecords(
            StringBuilder builder,
            IReadOnlyList<AuthoringChildPlanSummary>? children)
        {
            builder.Append('[');
            var first = true;
            if (children != null)
            {
                foreach (var child in children.Where(child => child != null)
                    .Take(100)
                    .OrderBy(child => child.Index))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    var safe = child.CloneBounded();
                    builder.Append('{');
                    AppendProperty(builder, "index", safe.Index);
                    AppendProperty(builder, "tool", safe.ToolName);
                    AppendProperty(builder, "confirmationRequired", safe.ConfirmationRequired);
                    AppendProperty(builder, "planId", safe.PlanId);
                    AppendProperty(builder, "planHash", safe.PlanHash);
                    AppendProperty(builder, "expiresAtUnixMs", safe.ExpiresAtUnixMs);
                    AppendProperty(builder, "policyVersion", safe.PolicyVersion);
                    AppendProperty(builder, "requestID", safe.RequestID);
                    AppendProperty(builder, "callId", safe.CallId);
                    AppendProperty(builder, "correlationId", safe.CorrelationId);
                    AppendProperty(builder, "parentCallId", safe.ParentCallId);
                    AppendProperty(builder, "deadlineUnixMs", safe.DeadlineUnixMs);
                    AppendProperty(builder, "cancellationId", safe.CancellationId);
                    AppendProperty(builder, "idempotencyKey", safe.IdempotencyKey);
                    AppendProperty(builder, "argumentsHash", safe.ArgumentsHash);
                    AppendProperty(builder, "risk", safe.Risk);
                    AppendProperty(builder, "undo", safe.Undo);
                    AppendProperty(builder, "planLevel", safe.PlanLevel);
                    builder.Append(",\"targets\":");
                    AppendTargets(builder, safe.Targets);
                    builder.Append(",\"predictedEffects\":");
                    AppendStrings(builder, safe.PredictedEffects);
                    AppendProperty(builder, "supportsSharedTransaction", safe.SupportsSharedTransaction);
                    builder.Append('}');
                }
            }
            builder.Append(']');
        }

        private static void AppendString(StringBuilder builder, string? value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }
            builder.Append(JsonSerializer.Serialize(value));
        }
    }

    public sealed partial class AuthoringSafetyPolicy
    {
        public AuthoringChildPlanSummary CreateAggregateChildRecord(
            int index,
            AuthoringInvocation invocation,
            AuthoringValidationResult? validatorSummary,
            AuthoringPlanSummary? plannerSummary,
            IReadOnlyList<AuthoringTargetSummary> targets,
            IReadOnlyList<string> predictedEffects,
            AuthoringChildPlanSummary? existingAuthority = null)
        {
            if (invocation == null) throw new ArgumentNullException(nameof(invocation));
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            if (predictedEffects == null) throw new ArgumentNullException(nameof(predictedEffects));

            var classification = AuthoringSafetyPolicyClassifier.ClassifyDetailed(
                invocation.Runner.ReadOnlyHint,
                invocation.Runner.DestructiveHint,
                invocation.Descriptor);
            var planLevel = GetPlanLevel(invocation.Descriptor);
            var undo = GetEffectiveUndo(classification.Risk, invocation.Descriptor);
            if (planLevel == AuthoringPlanLevel.Plan && plannerSummary?.HasUndoOverride == true)
                undo = LowerUndo(undo, plannerSummary.UndoLevel);
            var confirmationRequired = RequiresConfirmation(classification.Risk, undo);
            if (confirmationRequired && planLevel == AuthoringPlanLevel.None)
                undo = AuthoringUndoLevel.None;

            var boundedTargets = targets.Where(target => target != null).Take(8)
                .Select(target => target.CloneSafe()).ToArray();
            var boundedEffects = predictedEffects.Where(effect => effect != null).Take(8)
                .Select(effect => effect!.Length <= 160 ? effect : effect.Substring(0, 160))
                .ToArray();
            _ = validatorSummary;
            var source = plannerSummary?.CloneBounded() ?? new AuthoringPlanSummary();
            source.Targets = boundedTargets;
            source.PredictedEffects = boundedEffects;
            var summary = BoundPlan(
                source,
                invocation,
                classification.Risk,
                undo,
                boundedTargets);
            summary.Targets = boundedTargets;
            summary.PredictedEffects = boundedEffects;
            summary.PlanLevelValue = planLevel;
            summary.SupportsSharedTransaction = SupportsSharedTransaction(
                invocation,
                classification.Risk,
                undo,
                plannerSummary?.SupportsSharedTransaction ?? true);

            var child = new AuthoringChildPlanSummary
            {
                Index = Math.Max(0, index),
                ToolName = invocation.Name,
                ConfirmationRequired = confirmationRequired,
                PolicyVersion = PolicyVersion,
                RequestID = invocation.Context.RequestID,
                CallId = invocation.Context.CallId,
                CorrelationId = invocation.Context.CorrelationId,
                ParentCallId = invocation.Context.ParentCallId,
                DeadlineUnixMs = invocation.Context.DeadlineUnixMs,
                CancellationId = invocation.Context.CancellationId,
                IdempotencyKey = invocation.Context.IdempotencyKey,
                ArgumentsHash = summary.ArgumentsHash,
                RiskLevel = classification.Risk,
                UndoLevel = undo,
                PlanLevelValue = planLevel,
                Targets = summary.Targets.Take(8).Select(target => target.CloneSafe()).ToArray(),
                PredictedEffects = summary.PredictedEffects.Take(8).ToArray(),
                SupportsSharedTransaction = summary.SupportsSharedTransaction,
            }.CloneBounded();

            if (existingAuthority != null)
            {
                if (existingAuthority.Index != index
                    || !string.Equals(existingAuthority.ToolName, child.ToolName, StringComparison.Ordinal)
                    || existingAuthority.ConfirmationRequired != child.ConfirmationRequired
                    || !HasSameChildBindingFacts(existingAuthority, child))
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.ConfirmationStale,
                        "A batch child confirmation record no longer matches its invocation.");
                }

                if (!confirmationRequired)
                {
                    if (!string.IsNullOrWhiteSpace(existingAuthority.PlanId)
                        || !string.IsNullOrWhiteSpace(existingAuthority.PlanHash)
                        || existingAuthority.ExpiresAtUnixMs.HasValue)
                    {
                        throw new ToolCallControlException(
                            ToolCallErrorCodes.ConfirmationInvalid,
                            "A frictionless batch child must not carry a confirmation token.");
                    }
                    return child;
                }

                if (string.IsNullOrWhiteSpace(existingAuthority.PlanId)
                    || string.IsNullOrWhiteSpace(existingAuthority.PlanHash)
                    || !existingAuthority.ExpiresAtUnixMs.HasValue
                    || !ConfirmationPlans.TryGet(existingAuthority.PlanId!, out var existingRecord)
                    || existingRecord == null
                    || existingRecord.PolicyVersion != PolicyVersion
                    || !string.Equals(existingRecord.InstanceId, ConfirmationPlans.InstanceId, StringComparison.Ordinal)
                    || existingRecord.ExpiresAtUnixMs != existingAuthority.ExpiresAtUnixMs.Value
                    || existingRecord.ExpiresAtUnixMs <= ConfirmationPlans.NowUnixMs()
                    || !AuthoringConfirmationBinding.FixedTimeEquals(existingRecord.PlanHash, existingAuthority.PlanHash))
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.ConfirmationInvalid,
                        "A batch child confirmation record could not be verified.");
                }

                var currentHash = AuthoringConfirmationBinding.Compute(
                    invocation,
                    PolicyVersion,
                    classification.Risk,
                    undo,
                    planLevel,
                    existingRecord.PlanId,
                    existingRecord.ExpiresAtUnixMs,
                    summary.Targets,
                    ConfirmationPlans.InstanceId,
                    predictedEffects: summary.PredictedEffects,
                    supportsSharedTransaction: summary.SupportsSharedTransaction);
                if (!AuthoringConfirmationBinding.FixedTimeEquals(currentHash, existingRecord.PlanHash))
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.ConfirmationStale,
                        "A batch child confirmation record no longer matches its invocation.");
                }

                child.PlanId = existingRecord.PlanId;
                child.PlanHash = existingRecord.PlanHash;
                child.ExpiresAtUnixMs = existingRecord.ExpiresAtUnixMs;
                return child.CloneBounded();
            }

            if (!confirmationRequired)
                return child;

            var record = ConfirmationPlans.IssueBound(
                (planId, expiresAt) => AuthoringConfirmationBinding.Compute(
                    invocation,
                    PolicyVersion,
                    classification.Risk,
                    undo,
                    planLevel,
                    planId,
                    expiresAt,
                    summary.Targets,
                    ConfirmationPlans.InstanceId,
                    predictedEffects: summary.PredictedEffects,
                    supportsSharedTransaction: summary.SupportsSharedTransaction),
                summary,
                PolicyVersion,
                invocation.Context.DeadlineUnixMs);
            child.PlanId = record.PlanId;
            child.PlanHash = record.PlanHash;
            child.ExpiresAtUnixMs = record.ExpiresAtUnixMs;
            return child.CloneBounded();
        }

        private static bool HasSameChildBindingFacts(
            AuthoringChildPlanSummary existing,
            AuthoringChildPlanSummary current)
        {
            var left = existing.CloneBounded();
            var right = current.CloneBounded();
            return left.Index == right.Index
                && string.Equals(left.ToolName, right.ToolName, StringComparison.Ordinal)
                && left.ConfirmationRequired == right.ConfirmationRequired
                && left.PolicyVersion == right.PolicyVersion
                && string.Equals(left.RequestID, right.RequestID, StringComparison.Ordinal)
                && string.Equals(left.CallId, right.CallId, StringComparison.Ordinal)
                && string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal)
                && string.Equals(left.ParentCallId, right.ParentCallId, StringComparison.Ordinal)
                && left.DeadlineUnixMs == right.DeadlineUnixMs
                && string.Equals(left.CancellationId, right.CancellationId, StringComparison.Ordinal)
                && string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal)
                && AuthoringConfirmationBinding.FixedTimeEquals(left.ArgumentsHash, right.ArgumentsHash)
                && left.RiskLevel == right.RiskLevel
                && left.UndoLevel == right.UndoLevel
                && left.PlanLevelValue == right.PlanLevelValue
                && left.SupportsSharedTransaction == right.SupportsSharedTransaction
                && HaveSameTargets(left.Targets, right.Targets)
                && left.PredictedEffects.SequenceEqual(right.PredictedEffects, StringComparer.Ordinal);
        }

        private static bool HaveSameTargets(
            IReadOnlyList<AuthoringTargetSummary> left,
            IReadOnlyList<AuthoringTargetSummary> right)
        {
            if (left.Count != right.Count)
                return false;
            for (var index = 0; index < left.Count; index++)
            {
                var leftTarget = left[index];
                var rightTarget = right[index];
                if (!string.Equals(leftTarget.Kind, rightTarget.Kind, StringComparison.Ordinal)
                    || !string.Equals(leftTarget.Name, rightTarget.Name, StringComparison.Ordinal)
                    || !string.Equals(leftTarget.RelativePath, rightTarget.RelativePath, StringComparison.Ordinal)
                    || !string.Equals(leftTarget.Fingerprint, rightTarget.Fingerprint, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private AuthoringPolicyResult EvaluateDryRun(
            AuthoringInvocation invocation,
            AuthoringClassification classification,
            AuthoringUndoLevel undo)
        {
            var context = invocation.Context;
            var descriptor = invocation.Descriptor;
            var validator = descriptor?.Validator;
            var validatorSupported = descriptor != null
                && !descriptor.IsOpaque
                && (descriptor.SupportsValidation || validator != null);

            if (!validatorSupported || validator == null)
                return DryRunUnsupported(context, classification.Risk, undo, descriptor, "validator_unavailable");

            AuthoringValidationResult validation;
            try
            {
                validation = validator.Validate(invocation);
            }
            catch (ToolCallControlException exception)
            {
                return AuthoringPolicyResult.Reject(
                    exception.Code,
                    SafeFailureMessage(exception.Code),
                    classification.Risk,
                    undo,
                    context,
                    details: SafeDetails(exception.Details));
            }
            catch
            {
                return DryRunUnsupported(context, classification.Risk, undo, descriptor, "validator_failed");
            }

            if (validation == null)
                return DryRunUnsupported(context, classification.Risk, undo, descriptor, "validator_returned_null");

            var boundedValidation = BoundValidation(validation);
            if (!boundedValidation.Valid)
            {
                var code = IsSafeFailureCode(boundedValidation.FailureCode)
                    ? boundedValidation.FailureCode!
                    : "validation_failed";
                var result = AuthoringPolicyResult.Reject(
                    code,
                    "The authoring call did not pass validation.",
                    classification.Risk,
                    undo,
                    context,
                    details: boundedValidation.Details);
                result.DryRun = context.DryRun;
                result.Validation = boundedValidation;
                result.Targets = boundedValidation.Targets;
                return result;
            }

            if (string.Equals(context.DryRun, "validate", StringComparison.Ordinal))
            {
                var result = AuthoringPolicyResult.Allow(classification.Risk, undo, context);
                result.DryRun = "validate";
                result.Validation = boundedValidation;
                result.Targets = boundedValidation.Targets;
                return result;
            }

            var planner = descriptor?.Planner;
            var plannerSupported = descriptor != null
                && !descriptor.IsOpaque
                && (descriptor.SupportsPlanning || planner != null);
            if (!plannerSupported || planner == null)
                return DryRunUnsupported(context, classification.Risk, undo, descriptor, "planner_unavailable");

            AuthoringPlanSummary plan;
            try
            {
                plan = planner.Plan(invocation);
            }
            catch (ToolCallControlException exception)
            {
                return AuthoringPolicyResult.Reject(
                    exception.Code,
                    SafeFailureMessage(exception.Code),
                    classification.Risk,
                    undo,
                    context,
                    details: SafeDetails(exception.Details));
            }
            catch
            {
                return DryRunUnsupported(context, classification.Risk, undo, descriptor, "planner_failed");
            }

            if (plan == null)
                return DryRunUnsupported(context, classification.Risk, undo, descriptor, "planner_returned_null");
            if (plan.HasUndoOverride)
                undo = LowerUndo(undo, plan.UndoLevel);

            var boundedPlan = BoundPlan(plan, invocation, classification.Risk, undo, boundedValidation.Targets);
            boundedPlan.PlanLevelValue = AuthoringPlanLevel.Plan;
            boundedPlan.SupportsSharedTransaction = SupportsSharedTransaction(
                invocation,
                classification.Risk,
                undo,
                plan.SupportsSharedTransaction);
            ConfirmationPlanRecord record;
            try
            {
                record = ConfirmationPlans.IssueBound(
                    (planId, expiresAt) => AuthoringConfirmationBinding.Compute(
                        invocation,
                        PolicyVersion,
                        classification.Risk,
                        undo,
                        AuthoringPlanLevel.Plan,
                        planId,
                        expiresAt,
                        boundedPlan.Targets,
                        ConfirmationPlans.InstanceId,
                        childRecords: boundedPlan.ChildRecords,
                        predictedEffects: boundedPlan.PredictedEffects,
                        supportsSharedTransaction: boundedPlan.SupportsSharedTransaction),
                    boundedPlan,
                    PolicyVersion,
                    context.DeadlineUnixMs);
            }
            catch (ToolCallControlException exception)
            {
                return AuthoringPolicyResult.Reject(
                    exception.Code,
                    exception.Message,
                    classification.Risk,
                    undo,
                    context,
                    details: SafeDetails(exception.Details));
            }
            boundedPlan = record.Summary.CloneBounded();

            var planResult = AuthoringPolicyResult.Allow(classification.Risk, undo, context);
            planResult.DryRun = "plan";
            planResult.Validation = boundedValidation;
            planResult.ConfirmationPlan = boundedPlan.CloneBounded();
            planResult.Targets = boundedPlan.Targets;
            planResult.PredictedEffects = boundedPlan.PredictedEffects;
            return planResult;
        }

        private AuthoringPolicyResult IssueConfirmation(
            AuthoringInvocation invocation,
            AuthoringClassification classification,
            AuthoringUndoLevel undo)
        {
            var context = invocation.Context;
            var descriptor = invocation.Descriptor;
            if (descriptor != null
                && ((descriptor.SupportsPlanning && descriptor.Planner == null)
                    || (descriptor.SupportsValidation && descriptor.Validator == null
                        && descriptor.Planner == null)))
            {
                return BindingUnavailable(
                    context,
                    classification.Risk,
                    undo,
                    "declared_adapter_unavailable");
            }

            var planLevel = GetPlanLevel(descriptor);
            var targets = Array.Empty<AuthoringTargetSummary>();
            AuthoringPlanSummary source;

            if (planLevel == AuthoringPlanLevel.None)
            {
                undo = AuthoringUndoLevel.None;
                source = new AuthoringPlanSummary
                {
                    Targets = targets,
                    PredictedEffects = new[] { "undeclared" },
                };
            }
            else
            {
                if (descriptor!.Validator != null)
                {
                    AuthoringValidationResult validation;
                    try
                    {
                        validation = descriptor.Validator.Validate(invocation);
                    }
                    catch (ToolCallControlException exception)
                    {
                        return AuthoringPolicyResult.Reject(
                            exception.Code,
                            SafeFailureMessage(exception.Code),
                            classification.Risk,
                            undo,
                            context,
                            details: SafeDetails(exception.Details));
                    }
                    catch
                    {
                        return BindingUnavailable(context, classification.Risk, undo, "validator_failed");
                    }

                    if (validation == null)
                        return BindingUnavailable(context, classification.Risk, undo, "validator_returned_null");
                    var boundedValidation = BoundValidation(validation);
                    if (!boundedValidation.Valid)
                    {
                        return AuthoringPolicyResult.Reject(
                            IsSafeFailureCode(boundedValidation.FailureCode)
                                ? boundedValidation.FailureCode!
                                : "validation_failed",
                            "The authoring call did not pass validation.",
                            classification.Risk,
                            undo,
                            context,
                            details: boundedValidation.Details);
                    }
                    targets = boundedValidation.Targets.ToArray();
                }

                if (planLevel == AuthoringPlanLevel.Plan)
                {
                    try
                    {
                        source = descriptor.Planner!.Plan(invocation);
                    }
                    catch (ToolCallControlException exception)
                    {
                        return AuthoringPolicyResult.Reject(
                            exception.Code,
                            SafeFailureMessage(exception.Code),
                            classification.Risk,
                            undo,
                            context,
                            details: SafeDetails(exception.Details));
                    }
                    catch
                    {
                        return BindingUnavailable(context, classification.Risk, undo, "planner_failed");
                    }
                    if (source == null)
                        return BindingUnavailable(context, classification.Risk, undo, "planner_returned_null");
                    if (source.HasUndoOverride)
                        undo = LowerUndo(undo, source.UndoLevel);
                }
                else
                {
                    source = new AuthoringPlanSummary
                    {
                        Targets = targets,
                        PredictedEffects = new[] { DefaultEffect(descriptor.MutationKind) },
                    };
                }
            }

            var summary = BoundPlan(source, invocation, classification.Risk, undo, targets);
            summary.PlanLevelValue = planLevel;
            summary.SupportsSharedTransaction = SupportsSharedTransaction(
                invocation,
                classification.Risk,
                undo,
                source.SupportsSharedTransaction);
            if (planLevel == AuthoringPlanLevel.None)
            {
                summary.Targets = Array.Empty<AuthoringTargetSummary>();
                summary.PredictedEffects = new[] { "undeclared" };
                summary.BeforeSummary = null;
                summary.AfterSummary = null;
            }

            ConfirmationPlanRecord record;
            try
            {
                record = ConfirmationPlans.IssueBound(
                    (planId, expiresAt) => AuthoringConfirmationBinding.Compute(
                        invocation,
                        PolicyVersion,
                        classification.Risk,
                        undo,
                        planLevel,
                        planId,
                        expiresAt,
                        summary.Targets,
                        ConfirmationPlans.InstanceId,
                        childRecords: summary.ChildRecords,
                        predictedEffects: summary.PredictedEffects,
                        supportsSharedTransaction: summary.SupportsSharedTransaction),
                    summary,
                    PolicyVersion,
                    context.DeadlineUnixMs);
            }
            catch (ToolCallControlException exception)
            {
                return AuthoringPolicyResult.Reject(
                    exception.Code,
                    exception.Message,
                    classification.Risk,
                    undo,
                    context,
                    details: SafeDetails(exception.Details));
            }

            var publicPlan = record.Summary.CloneBounded();
            var details = new JsonObject
            {
                ["confirmationPlan"] = JsonNode.Parse(JsonSerializer.Serialize(publicPlan)),
            };
            if (AuthoringConfirmationRetry.TryBuild(
                context,
                record,
                out var retryWith,
                out var suppressionReason))
            {
                details["retryWith"] = retryWith;
            }
            else
            {
                details["retrySuppressedReason"] = suppressionReason;
            }

            return AuthoringPolicyResult.Reject(
                ToolCallErrorCodes.ConfirmationRequired,
                "This tool call requires confirmation before it can execute.",
                classification.Risk,
                undo,
                context,
                requiresConfirmation: true,
                details: details);
        }

        private AuthoringPolicyResult VerifyConfirmation(
            AuthoringInvocation invocation,
            AuthoringClassification classification,
            AuthoringUndoLevel undo)
        {
            var context = invocation.Context;
            var token = context.Confirmation;
            if (context.Confirm != true || token == null)
            {
                return AuthoringPolicyResult.Reject(
                    ToolCallErrorCodes.ConfirmationRequired,
                    "A confirmation plan is required for this tool call.",
                    classification.Risk,
                    undo,
                    context,
                    requiresConfirmation: true,
                    details: new JsonObject
                    {
                        ["retryWith"] = "dryRun=plan, then confirm=true with confirmation",
                    });
            }

            if (string.IsNullOrWhiteSpace(token.PlanId)
                || string.IsNullOrWhiteSpace(token.PlanHash)
                || !token.ExpiresAtUnixMs.HasValue
                || token.ExpiresAtUnixMs.Value < 0)
            {
                return ConfirmationFailure(
                    ToolCallErrorCodes.ConfirmationInvalid,
                    classification,
                    undo,
                    context,
                    "malformed_token");
            }

            if (!ConfirmationPlans.TryGet(token.PlanId!, out var record) || record == null)
            {
                var expired = token.ExpiresAtUnixMs.Value <= ConfirmationPlans.NowUnixMs();
                return ConfirmationFailure(
                    expired ? ToolCallErrorCodes.ConfirmationExpired : ToolCallErrorCodes.ConfirmationInvalid,
                    classification,
                    undo,
                    context,
                    expired ? "expired_token" : "unknown_plan");
            }

            var now = ConfirmationPlans.NowUnixMs();
            if (record.ExpiresAtUnixMs <= now || token.ExpiresAtUnixMs.Value <= now)
            {
                return ConfirmationFailure(
                    ToolCallErrorCodes.ConfirmationExpired,
                    classification,
                    undo,
                    context,
                    "expired_plan");
            }

            if (!string.Equals(record.InstanceId, ConfirmationPlans.InstanceId, StringComparison.Ordinal))
            {
                // A record that was not issued by this store instance (for
                // example an imported plan) is never an approval here.
                return ConfirmationFailure(
                    ToolCallErrorCodes.ConfirmationInvalid,
                    classification,
                    undo,
                    context,
                    "cross_instance_plan");
            }

            if (record.PolicyVersion != PolicyVersion
                || token.ExpiresAtUnixMs.Value != record.ExpiresAtUnixMs
                || !AuthoringConfirmationBinding.FixedTimeEquals(token.PlanHash, record.PlanHash))
            {
                return ConfirmationFailure(
                    ToolCallErrorCodes.ConfirmationInvalid,
                    classification,
                    undo,
                    context,
                    "token_binding_mismatch");
            }

            var currentLevel = GetPlanLevel(invocation.Descriptor);
            if (currentLevel != record.Summary.PlanLevelValue)
            {
                return ConfirmationFailure(
                    ToolCallErrorCodes.ConfirmationStale,
                    classification,
                    undo,
                    context,
                    "plan_level_changed");
            }

            IReadOnlyList<AuthoringTargetSummary> currentTargets = Array.Empty<AuthoringTargetSummary>();
            IReadOnlyList<string> currentEffects = record.Summary.PredictedEffects;
            IReadOnlyList<AuthoringChildPlanSummary>? currentChildRecords = record.Summary.ChildRecords;
            var currentSupportsSharedTransaction = SupportsSharedTransaction(
                invocation,
                classification.Risk,
                undo,
                plannerSupportsSharedTransaction: true);
            if (currentLevel == AuthoringPlanLevel.Validate)
            {
                try
                {
                    var validation = invocation.Descriptor!.Validator!.Validate(invocation);
                    if (validation == null || !validation.Valid)
                        throw new InvalidOperationException();
                    currentTargets = BoundValidation(validation).Targets;
                }
                catch
                {
                    return ConfirmationFailure(
                        ToolCallErrorCodes.ConfirmationStale,
                        classification,
                        undo,
                        context,
                        "target_inspection_failed");
                }
            }
            else if (currentLevel == AuthoringPlanLevel.Plan)
            {
                try
                {
                    IReadOnlyList<AuthoringTargetSummary> validationTargets = Array.Empty<AuthoringTargetSummary>();
                    if (invocation.Descriptor!.Validator != null)
                    {
                        var validation = invocation.Descriptor.Validator.Validate(invocation);
                        if (validation == null || !validation.Valid)
                            throw new InvalidOperationException();
                        validationTargets = BoundValidation(validation).Targets;
                    }

                    invocation.PlanBeingVerified = record.Summary.CloneBounded();
                    AuthoringPlanSummary current;
                    try
                    {
                        current = invocation.Descriptor.Planner!.Plan(invocation);
                    }
                    finally
                    {
                        invocation.PlanBeingVerified = null;
                    }
                    if (current == null)
                        throw new InvalidOperationException();
                    if (current.HasUndoOverride)
                        undo = LowerUndo(undo, current.UndoLevel);
                    currentSupportsSharedTransaction = SupportsSharedTransaction(
                        invocation,
                        classification.Risk,
                        undo,
                        current.SupportsSharedTransaction);
                    var boundedCurrent = BoundPlan(
                        current,
                        invocation,
                        classification.Risk,
                        undo,
                        validationTargets);
                    currentChildRecords = boundedCurrent.ChildRecords;
                    currentTargets = boundedCurrent.Targets;
                    currentEffects = boundedCurrent.PredictedEffects;
                }
                catch
                {
                    return ConfirmationFailure(
                        ToolCallErrorCodes.ConfirmationStale,
                        classification,
                        undo,
                        context,
                        "target_inspection_failed");
                }
            }
            else
            {
                undo = AuthoringUndoLevel.None;
            }

            if (invocation.Descriptor?.PathBindings != null
                && invocation.Descriptor.PathBindings.Any(binding =>
                    binding != null && binding.Intent != AuthoringPathAccessIntent.Read))
            {
                try
                {
                    if (PathPolicy == null)
                        return BindingUnavailable(context, classification.Risk, undo, "path_policy_unconfigured");
                    invocation = PathPolicy.ReResolveInvocationBeforeWrite(invocation);
                }
                catch (ToolCallControlException exception)
                {
                    var failure = AuthoringPolicyResult.Reject(
                        exception.Code,
                        exception.Message,
                        classification.Risk,
                        undo,
                        context,
                        requiresConfirmation: true,
                        details: exception.Details as JsonObject);
                    failure.Retryable = exception.Retryable;
                    return failure;
                }
            }

            var currentHash = AuthoringConfirmationBinding.Compute(
                invocation,
                PolicyVersion,
                classification.Risk,
                undo,
                currentLevel,
                record.PlanId,
                record.ExpiresAtUnixMs,
                currentTargets,
                ConfirmationPlans.InstanceId,
                childRecords: currentChildRecords,
                predictedEffects: currentEffects,
                supportsSharedTransaction: currentSupportsSharedTransaction);
            if (!AuthoringConfirmationBinding.FixedTimeEquals(currentHash, record.PlanHash))
            {
                return ConfirmationFailure(
                    ToolCallErrorCodes.ConfirmationStale,
                    classification,
                    undo,
                    context,
                    "arguments_or_targets_changed");
            }

            if (currentLevel == AuthoringPlanLevel.Plan
                && currentSupportsSharedTransaction != record.Summary.SupportsSharedTransaction)
            {
                return ConfirmationFailure(
                    ToolCallErrorCodes.ConfirmationStale,
                    classification,
                    undo,
                    context,
                    "transaction_compatibility_changed");
            }

            if (currentLevel == AuthoringPlanLevel.Plan
                && !currentSupportsSharedTransaction)
                invocation.SuppressTransaction = true;
            invocation.EffectiveUndoLevel = undo;
            invocation.PendingConfirmation = record;
            invocation.ApprovedPlan = record.Summary.CloneBounded();

            var result = AuthoringPolicyResult.Allow(classification.Risk, undo, context);
            result.Targets = currentTargets;
            result.PredictedEffects = currentEffects;
            return result;
        }

        private static AuthoringPolicyResult BindingUnavailable(
            ToolCallContext context,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            string reason)
            => AuthoringPolicyResult.Reject(
                ToolCallErrorCodes.SafetyUnsupported,
                "The authoring policy could not bind this tool call safely.",
                risk,
                undo,
                context,
                details: new JsonObject
                {
                    ["reason"] = "binding_unavailable",
                    ["stage"] = reason,
                });

        private static AuthoringPolicyResult DryRunUnsupported(
            ToolCallContext context,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            AuthoringCapabilityDescriptor? descriptor,
            string reason)
        {
            var result = AuthoringPolicyResult.Reject(
                ToolCallErrorCodes.DryRunUnsupported,
                "The requested dry-run mode is not supported by this tool.",
                risk,
                undo,
                context,
                details: new JsonObject
                {
                    ["requestedMode"] = context.DryRun,
                    ["reason"] = reason,
                    ["supportedLevel"] = GetPlanLevel(descriptor).ToWireValue(),
                });
            result.DryRun = context.DryRun;
            return result;
        }

        private static AuthoringPolicyResult ConfirmationFailure(
            string code,
            AuthoringClassification classification,
            AuthoringUndoLevel undo,
            ToolCallContext context,
            string reason)
            => AuthoringPolicyResult.Reject(
                code,
                code == ToolCallErrorCodes.ConfirmationExpired
                    ? "The confirmation plan has expired."
                    : code == ToolCallErrorCodes.ConfirmationStale
                        ? "The confirmation plan no longer matches this call."
                        : "The confirmation plan could not be verified.",
                classification.Risk,
                undo,
                context,
                requiresConfirmation: true,
                details: new JsonObject { ["reason"] = reason });

        private static AuthoringValidationResult BoundValidation(AuthoringValidationResult value)
        {
            var details = SafeDetails(value.Details);
            return new AuthoringValidationResult
            {
                Valid = value.Valid,
                FailureCode = IsSafeFailureCode(value.FailureCode) ? value.FailureCode : null,
                FailureReason = null,
                Details = details,
                Targets = value.Targets == null
                    ? Array.Empty<AuthoringTargetSummary>()
                    : value.Targets.Where(target => target != null).Take(32)
                        .Select(target => target.CloneSafe()).ToArray(),
            };
        }

        private static AuthoringPlanSummary BoundPlan(
            AuthoringPlanSummary value,
            AuthoringInvocation invocation,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            IReadOnlyList<AuthoringTargetSummary> fallbackTargets)
        {
            var bounded = value.CloneBounded();
            bounded.ToolName = invocation.Name;
            bounded.RequestID = invocation.Context.RequestID;
            bounded.CallId = invocation.Context.CallId;
            bounded.CorrelationId = invocation.Context.CorrelationId;
            bounded.PolicyVersion = PolicyVersion;
            bounded.RiskLevel = risk;
            bounded.UndoLevel = undo;
            bounded.ArgumentsHash = AuthoringConfirmationBinding.ComputeArgumentsHash(invocation.Arguments);
            if (bounded.Targets == null || bounded.Targets.Count == 0)
                bounded.Targets = fallbackTargets;
            bounded.Targets = bounded.Targets.Where(target => target != null).Take(32)
                .Select(target => target.CloneSafe()).ToArray();
            if (bounded.PredictedEffects == null || bounded.PredictedEffects.Count == 0)
                bounded.PredictedEffects = new[] { DefaultEffect(invocation.Descriptor?.MutationKind ?? AuthoringMutationKind.Unknown) };
            bounded.PredictedEffects = bounded.PredictedEffects.Take(32)
                .Where(effect => effect != null)
                .Select(effect => effect!.Length <= 160 ? effect : effect.Substring(0, 160))
                .ToArray();
            return bounded;
        }

        private static AuthoringUndoLevel LowerUndo(AuthoringUndoLevel declared, AuthoringUndoLevel observed)
            => (AuthoringUndoLevel)Math.Min((int)declared, (int)observed);

        private static bool SupportsSharedTransaction(
            AuthoringInvocation invocation,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo,
            bool plannerSupportsSharedTransaction)
            => risk == AuthoringRiskLevel.Read
                || (undo != AuthoringUndoLevel.None
                    && invocation.Descriptor?.TransactionFactory != null
                    && plannerSupportsSharedTransaction);

        private static string DefaultEffect(AuthoringMutationKind kind)
            => kind == AuthoringMutationKind.Unknown ? "authoring change" : kind.ToString().ToLowerInvariant();

        private static JsonObject? SafeDetails(JsonNode? details)
        {
            var objectDetails = details as JsonObject;
            return objectDetails == null
                ? null
                : AuthoringSafeJson.CloneObject(objectDetails);
        }

        private static string SafeFailureMessage(string? code)
            => code == ToolCallErrorCodes.PathPolicyViolation
                ? "The requested path is not allowed by project policy."
                : "The authoring call was rejected by policy.";

        private static bool IsSafeFailureCode(string? code)
            => code == ToolCallErrorCodes.InvalidControl
                || code == ToolCallErrorCodes.PathPolicyViolation
                || code == ToolCallErrorCodes.SafetyUnsupported
                || code == ToolCallErrorCodes.DryRunUnsupported
                || code == ToolCallErrorCodes.UndoUnavailable
                || code == "validation_failed";
    }

    internal static class AuthoringDryRunResponseExtensions
    {
        private static readonly JsonSerializerOptions PlanJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static ResponseCallTool ToDryRunResponse(this AuthoringPolicyResult result)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            var payload = new JsonObject
            {
                ["dryRun"] = result.DryRun,
                ["valid"] = result.Validation?.Valid ?? true,
                ["risk"] = result.Risk,
                ["undo"] = result.Undo,
            };

            if (result.Validation != null)
            {
                var validation = new JsonObject
                {
                    ["valid"] = result.Validation.Valid,
                    ["targets"] = Targets(result.Validation.Targets),
                };
                if (result.Validation.FailureCode != null)
                    validation["code"] = result.Validation.FailureCode;
                if (result.Validation.Details != null)
                    validation["details"] = JsonNode.Parse(result.Validation.Details.ToJsonString());
                payload["validation"] = validation;
            }

            if (result.ConfirmationPlan != null)
            {
                var plan = JsonNode.Parse(JsonSerializer.Serialize(
                    result.ConfirmationPlan,
                    PlanJsonOptions)) as JsonObject;
                payload["confirmationPlan"] = plan;
            }

            if (result.PredictedEffects != null && result.PredictedEffects.Count > 0)
                payload["predictedEffects"] = new JsonArray(
                    result.PredictedEffects.Take(32).Select(effect => (JsonNode?)effect).ToArray());

            return ResponseCallTool.SuccessStructured(payload).SetRequestID(result.RequestID ?? string.Empty);
        }

        private static JsonArray Targets(IReadOnlyList<AuthoringTargetSummary>? values)
        {
            var array = new JsonArray();
            if (values == null) return array;
            foreach (var value in values.Take(32))
            {
                if (value == null) continue;
                var safe = value.CloneSafe();
                array.Add(new JsonObject
                {
                    ["kind"] = safe.Kind,
                    ["name"] = safe.Name,
                    ["relativePath"] = safe.RelativePath,
                    ["fingerprint"] = safe.Fingerprint,
                });
            }
            return array;
        }
    }
}
