/*
┌──────────────────────────────────────────────────────────────────┐
│  Durable project-local operation tracking for Editor automation. │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using com.AtelierAI.Uco.Framework.Common.Model;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    [Serializable]
    public sealed class EditorOperationInfo : IDurableOperationHandle
    {
        public string OperationId = string.Empty;
        public string Kind = string.Empty;
        public string OwnerId = string.Empty;
        public string ReloadBehavior = "interrupt";
        public string InstanceId = string.Empty;
        public string Status = string.Empty;
        public string Phase = string.Empty;
        public string CreatedAtUtc = string.Empty;
        public string UpdatedAtUtc = string.Empty;
        public string? CompletedAtUtc;
        public string? StartedAtUtc;
        public int EditorPid;
        public string DomainGeneration = string.Empty;
        public string? SourceRevision;
        public double Progress = -1;
        public bool CancellationRequested;
        public string? CancellationRequestedAtUtc;
        public bool CancellationPending;
        public string? ResultJson;
        public string? ErrorCode;
        public string? ErrorMessage;
        public string? MetadataJson;
        public string? DiagnosticsJson;

        public bool IsTerminal => EditorOperationRegistry.IsTerminalStatus(Status);

        /// <summary>
        /// Every durable operation is executed by its own owner pipeline; no
        /// result reuse or filter-keyed cache exists. The envelope states this
        /// so callers can stop hand-rolling cache avoidance (COCli-03).
        /// </summary>
        public string Execution => "fresh";

        /// <summary>
        /// Structured blocked cause for nonterminal operations waiting on an
        /// external condition (compilation, a previous run settling, capacity
        /// admission, cancellation pending). Null while running freely or
        /// terminal — blocked is never a failure.
        /// </summary>
        public EditorOperationBlocked? Blocked => EditorOperationRegistry.BlockedCauseFor(this);

        internal EditorOperationInfo Clone()
            => (EditorOperationInfo)MemberwiseClone();
    }

    [Serializable]
    public sealed class EditorOperationBlocked
    {
        public string Cause = string.Empty;
        public int RetryAfterMs = 250;
    }

    [Serializable]
    internal sealed class EditorOperationStore
    {
        public int SchemaVersion = EditorOperationRegistry.SchemaVersion;
        public List<EditorOperationInfo> Operations = new();
    }

    public sealed class EditorOperationCapacityException : ToolCallControlException
    {
        public int RetryAfterMs { get; }

        public EditorOperationCapacityException(string message, int retryAfterMs = 250)
            : base(
                ToolCallErrorCodes.OperationCapacityExceeded,
                message,
                retryable: true,
                details: new JsonObject
                {
                    ["retryAfterMs"] = Math.Max(50, Math.Min(5000, retryAfterMs))
                })
        {
            RetryAfterMs = Math.Max(50, Math.Min(5000, retryAfterMs));
        }
    }

    /// <summary>
    /// The only durable operation state store. Process-local owner behavior lives in
    /// <see cref="EditorOperationOwnerRegistry"/> and never duplicates these records.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorOperationRegistry
    {
        public const int SchemaVersion = 2;
        public const int MaxRetainedOperations = 200;
        public const int MaxActiveOperationsPerInstance = 64;
        public const int MaxListResults = 100;
        public const int MaxPayloadBytes = 64 * 1024;
        public const int MaxDiagnosticBytes = 512;

        static readonly object s_gate = new();
        static readonly Dictionary<string, EditorOperationInfo> s_operations =
            new(StringComparer.Ordinal);
        static readonly UTF8Encoding s_utf8 = new(false, true);
        static readonly string s_domainGeneration = Guid.NewGuid().ToString("N");
        static string? s_storePathOverride;
        static Func<string>? s_instanceIdProvider;

        public static string DomainGeneration => s_domainGeneration;
        public static int EditorPid => Process.GetCurrentProcess().Id;
        public static string InstanceId => BoundIdentifier(
            s_instanceIdProvider?.Invoke(), 160,
            "editor-" + EditorPid.ToString(CultureInfo.InvariantCulture));
        public static string StorePath => s_storePathOverride ?? Path.Combine(
            Path.GetDirectoryName(Application.dataPath) ?? Environment.CurrentDirectory,
            "Library", "UnityCopilot", "editor-operations.json");

        static EditorOperationRegistry()
        {
            lock (s_gate)
                LoadLocked();
        }

        public static void ConfigureInstanceIdProvider(Func<string>? provider)
        {
            lock (s_gate)
                s_instanceIdProvider = provider;
        }

        public static void Schedule(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            void RunOnce()
            {
                EditorApplication.update -= RunOnce;
                action();
            }

            EditorApplication.update += RunOnce;
        }

        public static EditorOperationInfo Create(
            string kind,
            string phase = "queued",
            string? metadataJson = null,
            string? ownerId = null,
            string reloadBehavior = "interrupt",
            string? sourceRevision = null)
        {
            var normalizedKind = BoundRequiredIdentifier(kind, nameof(kind));
            var normalizedOwner = BoundIdentifier(ownerId, 160, normalizedKind);
            var normalizedReload = NormalizeReloadBehavior(reloadBehavior);

            lock (s_gate)
            {
                TrimLocked();
                var instanceId = InstanceId;
                var activeCount = s_operations.Values.Count(operation =>
                    !operation.IsTerminal
                    && string.Equals(operation.InstanceId, instanceId, StringComparison.Ordinal));
                if (activeCount >= MaxActiveOperationsPerInstance)
                {
                    throw new EditorOperationCapacityException(
                        $"Editor instance has reached its {MaxActiveOperationsPerInstance}-operation active capacity.");
                }
                if (s_operations.Count >= MaxRetainedOperations
                    && !s_operations.Values.Any(operation => operation.IsTerminal))
                {
                    throw new EditorOperationCapacityException(
                        "Operation retention is full of active work and cannot evict a live record.", 500);
                }

                var now = UtcNow();
                var operation = new EditorOperationInfo
                {
                    OperationId = Guid.NewGuid().ToString("N"),
                    Kind = normalizedKind,
                    OwnerId = normalizedOwner,
                    ReloadBehavior = normalizedReload,
                    InstanceId = instanceId,
                    Status = "queued",
                    Phase = BoundPhase(phase, "queued"),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                    EditorPid = EditorPid,
                    DomainGeneration = DomainGeneration,
                    SourceRevision = string.IsNullOrWhiteSpace(sourceRevision)
                        ? null
                        : BoundIdentifier(sourceRevision, 200, null),
                    MetadataJson = BoundJson(metadataJson)
                };
                s_operations[operation.OperationId] = operation;
                PersistLocked();
                return operation.Clone();
            }
        }

        public static EditorOperationInfo? Get(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId)) return null;
            lock (s_gate)
                return s_operations.TryGetValue(operationId, out var operation)
                    ? operation.Clone()
                    : null;
        }

        public static EditorOperationInfo[] List(
            string? kind = null,
            bool includeTerminal = true,
            int limit = MaxListResults)
        {
            var boundedLimit = Math.Max(1, Math.Min(MaxListResults, limit));
            lock (s_gate)
            {
                return s_operations.Values
                    .Where(operation => string.IsNullOrWhiteSpace(kind)
                        || string.Equals(operation.Kind, kind, StringComparison.OrdinalIgnoreCase))
                    .Where(operation => includeTerminal || !operation.IsTerminal)
                    .OrderByDescending(operation => operation.CreatedAtUtc, StringComparer.Ordinal)
                    .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                    .Take(boundedLimit)
                    .Select(operation => operation.Clone())
                    .ToArray();
            }
        }

        internal static EditorOperationInfo[] ListAllForReconciliation()
        {
            lock (s_gate)
                return s_operations.Values
                    .OrderBy(operation => operation.CreatedAtUtc, StringComparer.Ordinal)
                    .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                    .Select(operation => operation.Clone())
                    .ToArray();
        }

        public static EditorOperationInfo Claim(string operationId, string phase)
        {
            lock (s_gate)
            {
                var operation = RequireLocked(operationId);
                if (operation.IsTerminal) return operation.Clone();
                operation.EditorPid = EditorPid;
                operation.DomainGeneration = DomainGeneration;
                operation.InstanceId = InstanceId;
                operation.Phase = BoundPhase(phase, operation.Phase);
                operation.UpdatedAtUtc = UtcNow();
                PersistLocked();
                return operation.Clone();
            }
        }

        public static EditorOperationInfo Start(string operationId, string phase = "running")
            => Transition(operationId, "running", phase);

        public static EditorOperationInfo Update(
            string operationId,
            string phase,
            double progress = -1,
            bool cancellationPending = false,
            string? diagnosticsJson = null)
        {
            lock (s_gate)
            {
                var operation = RequireLocked(operationId);
                if (operation.IsTerminal) return operation.Clone();
                operation.Phase = BoundPhase(phase, operation.Phase);
                operation.Progress = progress < 0 ? -1 : Math.Max(0d, Math.Min(1d, progress));
                operation.CancellationPending = cancellationPending
                    || (operation.CancellationRequested && operation.Status == "running");
                operation.DiagnosticsJson = BoundJson(diagnosticsJson);
                operation.UpdatedAtUtc = UtcNow();
                PersistLocked();
                return operation.Clone();
            }
        }

        public static EditorOperationInfo Succeed(string operationId, string? resultJson = null)
        {
            var result = Transition(operationId, "succeeded", "completed", resultJson: resultJson);
            EditorOperationOwnerRegistry.Release(operationId);
            return result;
        }

        public static EditorOperationInfo Fail(
            string operationId,
            string errorCode,
            string errorMessage,
            string phase = "failed",
            string? resultJson = null)
        {
            var result = Transition(operationId, "failed", phase,
                resultJson: resultJson,
                errorCode: BoundIdentifier(errorCode, 160, "operation_failed"),
                errorMessage: BoundDiagnostic(errorMessage));
            EditorOperationOwnerRegistry.Release(operationId);
            return result;
        }

        public static EditorOperationInfo Interrupt(
            string operationId,
            string errorMessage,
            string phase = "interrupted",
            string errorCode = "operation_interrupted")
        {
            var result = Transition(operationId, "interrupted", phase,
                errorCode: BoundIdentifier(errorCode, 160, "operation_interrupted"),
                errorMessage: BoundDiagnostic(errorMessage));
            EditorOperationOwnerRegistry.Release(operationId);
            return result;
        }

        public static EditorOperationInfo RequestCancellation(string operationId)
        {
            lock (s_gate)
            {
                var operation = RequireLocked(operationId);
                if (operation.IsTerminal) return operation.Clone();

                operation.UpdatedAtUtc = UtcNow();
                if (!operation.CancellationRequested
                    || string.IsNullOrEmpty(operation.CancellationRequestedAtUtc))
                {
                    operation.CancellationRequestedAtUtc = operation.UpdatedAtUtc;
                }
                operation.CancellationRequested = true;
                if (operation.Status == "queued")
                {
                    operation.Status = "cancelled";
                    operation.Phase = "cancelled-before-start";
                    operation.CompletedAtUtc = operation.UpdatedAtUtc;
                    operation.CancellationPending = false;
                }
                else
                {
                    operation.CancellationPending = true;
                }
                PersistLocked();
                return operation.Clone();
            }
        }

        public static EditorOperationInfo CancelRunning(
            string operationId,
            string phase = "cancelled",
            string? resultJson = null)
        {
            var result = Transition(operationId, "cancelled", phase, resultJson: resultJson);
            EditorOperationOwnerRegistry.Release(operationId);
            return result;
        }

        public static bool IsCancellationRequested(string operationId)
        {
            lock (s_gate)
                return s_operations.TryGetValue(operationId, out var operation)
                    && operation.CancellationRequested;
        }

        public static bool IsTerminalStatus(string? status)
            => status == "succeeded" || status == "failed"
                || status == "cancelled" || status == "interrupted";

        /// <summary>
        /// Project a structured blocked cause for a nonterminal operation from
        /// its phase vocabulary. Blocked is waiting-on-an-external-condition —
        /// never a failure — and carries a retry-after hint when a bound is
        /// known (COCli-03).
        /// </summary>
        public static EditorOperationBlocked? BlockedCauseFor(EditorOperationInfo operation)
        {
            if (operation == null || operation.IsTerminal) return null;
            var phase = operation.Phase ?? string.Empty;
            if (phase.StartsWith("waiting-for-compilation", StringComparison.Ordinal))
                return new EditorOperationBlocked { Cause = "compilation", RetryAfterMs = 500 };
            if (phase.StartsWith("waiting-for-previous-test-run", StringComparison.Ordinal))
                return new EditorOperationBlocked { Cause = "previous-run-settling", RetryAfterMs = 250 };
            if (phase.StartsWith("cancellation-pending", StringComparison.Ordinal)
                || phase.StartsWith("cancelling", StringComparison.Ordinal)
                || phase.StartsWith("execution-timeout-cancellation-pending", StringComparison.Ordinal))
                return new EditorOperationBlocked { Cause = "cancellation-pending", RetryAfterMs = 250 };
            if (phase.StartsWith("executing", StringComparison.Ordinal)
                || phase.StartsWith("awaiting", StringComparison.Ordinal)
                || phase.StartsWith("running", StringComparison.Ordinal))
                return null;
            // queued / preparing / scheduled — admission and owner scheduling.
            return new EditorOperationBlocked { Cause = "capacity-admission", RetryAfterMs = 250 };
        }

        // ── compile epoch (COCli-03 sourceRevision) ─────────────────────────
        // Monotonic per-Editor-session compilation counter, persisted through
        // SessionState so it survives the domain reloads it counts. The
        // InitializeOnLoad hook below seeds it at 1 (the startup compile).
        const string CompileEpochKey = "UnityCopilot.ScriptCompilationEpoch";

        internal static int CompileEpoch
        {
            get
            {
                var value = SessionState.GetInt(CompileEpochKey, 0);
                if (value <= 0)
                {
                    // Seed the startup compile so the epoch is observable from boot.
                    value = 1;
                    SessionState.SetInt(CompileEpochKey, value);
                }
                return value;
            }
        }

        internal static void NoteCompilationFinished()
            => SessionState.SetInt(CompileEpochKey, CompileEpoch + 1);

        /// <summary>
        /// Capture the compile epoch that produced the code an operation is
        /// about to execute: the Editor's compilation counter when observable,
        /// else the max write time of loaded non-dynamic assemblies.
        /// </summary>
        public static string CaptureSourceRevision()
        {
            var domain = DomainGeneration;
            int epoch;
            try
            {
                epoch = CompileEpoch;
            }
            catch
            {
                epoch = 0;
            }
            if (epoch > 0)
                return $"compile:{domain}:{epoch}";
            return $"compile:{domain}:asm-{MaxReferencedAssemblyWriteTicks().ToString(CultureInfo.InvariantCulture)}";
        }

        static long MaxReferencedAssemblyWriteTicks()
        {
            long max = 0;
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.IsDynamic) continue;
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location)) continue;
                    try
                    {
                        var ticks = File.GetLastWriteTimeUtc(location).Ticks;
                        if (ticks > max) max = ticks;
                    }
                    catch { }
                }
            }
            catch { }
            return max;
        }

        /// <summary>
        /// Compatibility entry point. Reconciliation is owner-routed and may only run after
        /// deterministic owner registration.
        /// </summary>
        public static void ReconcileActiveOperations()
            => EditorOperationOwnerRegistry.ReconcilePriorGeneration();

        internal static void ConfigureStoreForTests(string? path)
        {
            lock (s_gate)
            {
                s_storePathOverride = path;
                s_operations.Clear();
                LoadLocked();
                EditorOperationOwnerRegistry.ResetReconciliationEpoch();
            }
        }

        static EditorOperationInfo Transition(
            string operationId,
            string status,
            string phase,
            string? resultJson = null,
            string? errorCode = null,
            string? errorMessage = null)
        {
            lock (s_gate)
            {
                var operation = RequireLocked(operationId);
                if (operation.IsTerminal)
                {
                    if (!string.Equals(operation.Status, status, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operationId}' is already terminal ({operation.Status}).");
                    }
                    return operation.Clone();
                }

                if (status != "running" && !IsTerminalStatus(status))
                    throw new ArgumentException($"Unsupported operation status '{status}'.", nameof(status));
                if (operation.Status == "running" && status == "running")
                {
                    operation.Phase = BoundPhase(phase, operation.Phase);
                    operation.UpdatedAtUtc = UtcNow();
                    PersistLocked();
                    return operation.Clone();
                }
                if (operation.Status != "queued" && status == "running")
                {
                    throw new InvalidOperationException(
                        $"Operation '{operationId}' cannot start from status '{operation.Status}'.");
                }

                operation.Status = status;
                operation.Phase = BoundPhase(phase, status);
                operation.UpdatedAtUtc = UtcNow();
                operation.ResultJson = BoundJson(resultJson);
                operation.ErrorCode = errorCode;
                operation.ErrorMessage = BoundDiagnostic(errorMessage);
                if (status == "running" && string.IsNullOrEmpty(operation.StartedAtUtc))
                {
                    operation.StartedAtUtc = operation.UpdatedAtUtc;
                }
                if (IsTerminalStatus(status))
                {
                    operation.CompletedAtUtc = operation.UpdatedAtUtc;
                    operation.CancellationPending = false;
                    operation.Progress = status == "succeeded" ? 1 : operation.Progress;
                }
                PersistLocked();
                return operation.Clone();
            }
        }

        static EditorOperationInfo RequireLocked(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("Operation ID must be non-empty.", nameof(operationId));
            if (!s_operations.TryGetValue(operationId, out var operation))
            {
                throw new KeyNotFoundException(
                    $"Editor operation '{BoundIdentifier(operationId, 160, "unknown")}' was not found.");
            }
            return operation;
        }

        static void LoadLocked()
        {
            s_operations.Clear();
            var path = StorePath;
            if (!File.Exists(path)) return;

            try
            {
                string json;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(
                    stream, s_utf8, detectEncodingFromByteOrderMarks: false))
                {
                    json = reader.ReadToEnd();
                }

                var store = JsonUtility.FromJson<EditorOperationStore>(json);
                if (store == null || store.Operations == null
                    || (store.SchemaVersion != 1 && store.SchemaVersion != SchemaVersion))
                {
                    throw new InvalidDataException("Unsupported or empty operation-store schema.");
                }

                foreach (var operation in store.Operations)
                {
                    if (operation == null || string.IsNullOrWhiteSpace(operation.OperationId)) continue;
                    NormalizeLoaded(operation);
                    s_operations[operation.OperationId] = operation;
                }
                TrimLocked();

                if (store.SchemaVersion != SchemaVersion)
                    PersistLocked();
            }
            catch
            {
                QuarantineCorruptStore(path);
                UnityEngine.Debug.LogWarning(
                    "[EditorOperationRegistry] unreadable operation store was quarantined.");
                s_operations.Clear();
            }
        }

        static void NormalizeLoaded(EditorOperationInfo operation)
        {
            operation.OperationId = BoundIdentifier(
                operation.OperationId, 160, Guid.NewGuid().ToString("N"));
            operation.Kind = BoundIdentifier(operation.Kind, 160, "unknown");
            operation.OwnerId = BoundIdentifier(operation.OwnerId, 160, operation.Kind);
            operation.ReloadBehavior = NormalizeReloadBehavior(operation.ReloadBehavior);
            operation.InstanceId = BoundIdentifier(
                operation.InstanceId, 160,
                "editor-" + operation.EditorPid.ToString(CultureInfo.InvariantCulture));
            operation.Status = IsTerminalStatus(operation.Status)
                || operation.Status == "queued" || operation.Status == "running"
                    ? operation.Status
                    : "interrupted";
            operation.Phase = BoundPhase(operation.Phase, operation.Status);
            operation.SourceRevision = string.IsNullOrWhiteSpace(operation.SourceRevision)
                ? null
                : BoundIdentifier(operation.SourceRevision, 200, null);
            operation.MetadataJson = BoundJson(operation.MetadataJson);
            operation.ResultJson = BoundJson(operation.ResultJson);
            operation.DiagnosticsJson = BoundJson(operation.DiagnosticsJson);
            operation.ErrorCode = BoundOptionalIdentifier(operation.ErrorCode, 160);
            operation.ErrorMessage = BoundDiagnostic(operation.ErrorMessage);
        }

        static void QuarantineCorruptStore(string path)
        {
            try
            {
                var quarantine = path + ".corrupt-"
                    + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
                File.Move(path, quarantine);
            }
            catch { }
        }

        static void PersistLocked()
        {
            TrimLocked();
            var path = StorePath;
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Operation store path has no parent directory.");
            Directory.CreateDirectory(directory);

            var store = new EditorOperationStore
            {
                SchemaVersion = SchemaVersion,
                Operations = s_operations.Values
                    .OrderBy(operation => operation.CreatedAtUtc, StringComparer.Ordinal)
                    .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                    .Select(operation => operation.Clone())
                    .ToList()
            };
            var json = JsonUtility.ToJson(store, true);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, json, s_utf8);
            try
            {
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        static void TrimLocked()
        {
            if (s_operations.Count <= MaxRetainedOperations) return;
            var removable = s_operations.Values
                .Where(operation => operation.IsTerminal)
                .OrderBy(operation => operation.UpdatedAtUtc, StringComparer.Ordinal)
                .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                .Take(Math.Max(0, s_operations.Count - MaxRetainedOperations))
                .Select(operation => operation.OperationId)
                .ToArray();
            foreach (var id in removable) s_operations.Remove(id);
        }

        static string BoundRequiredIdentifier(string? value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Value must be non-empty.", parameterName);
            return BoundIdentifier(value, 160, "unknown");
        }

        static string BoundIdentifier(string? value, int maxLength, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        static string? BoundOptionalIdentifier(string? value, int maxLength)
            => string.IsNullOrWhiteSpace(value)
                ? null
                : BoundIdentifier(value, maxLength, "unknown");

        static string BoundPhase(string? value, string fallback)
            => BoundIdentifier(value, 160, fallback);

        static string NormalizeReloadBehavior(string? value)
            => value == "resume" || value == "complete-after-reload"
                ? value
                : "interrupt";

        static string? BoundDiagnostic(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            var safe = value.Replace('\r', ' ').Replace('\n', ' ');
            safe = RedactInlineSecrets(safe);
            safe = Regex.Replace(
                safe,
                @"(?i)(?:[A-Z]:\\|/Users/|/home/)[^\s\""']+",
                "[HOST_PATH]",
                RegexOptions.CultureInvariant);
            return BoundUtf8(safe, MaxDiagnosticBytes);
        }

        static string? BoundJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                var parsed = JsonNode.Parse(json);
                var sanitized = SanitizeNode(parsed, 0, null);
                var serialized = sanitized?.ToJsonString() ?? "null";
                var bytes = s_utf8.GetByteCount(serialized);
                if (bytes <= MaxPayloadBytes) return serialized;

                using var sha = SHA256.Create();
                var digest = BitConverter.ToString(sha.ComputeHash(s_utf8.GetBytes(serialized)))
                    .Replace("-", string.Empty).ToLowerInvariant();
                return new JsonObject
                {
                    ["truncated"] = true,
                    ["sha256"] = digest,
                    ["originalBytes"] = bytes
                }.ToJsonString();
            }
            catch
            {
                return new JsonObject { ["invalid"] = true }.ToJsonString();
            }
        }

        static JsonNode? SanitizeNode(JsonNode? node, int depth, string? propertyName)
        {
            if (node == null || depth > 8) return null;
            if (node is JsonObject obj)
            {
                var result = new JsonObject();
                foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(128))
                {
                    if (IsCredentialName(property.Key)) continue;
                    result[property.Key] = SanitizeNode(property.Value, depth + 1, property.Key);
                }
                return result;
            }
            if (node is JsonArray array)
            {
                var result = new JsonArray();
                foreach (var item in array.Take(256))
                    result.Add(SanitizeNode(item, depth + 1, propertyName));
                return result;
            }
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                if (IsPathName(propertyName) && IsAbsolutePath(text)) return null;
                return JsonValue.Create(BoundUtf8(RedactInlineSecrets(text), 4096));
            }
            try { return JsonNode.Parse(node.ToJsonString()); }
            catch { return null; }
        }

        static string RedactInlineSecrets(string value)
            => Regex.Replace(
                value,
                @"(?i)(bearer\s+|access[_-]?token=|api[_-]?key=|token=)[^\s&]+",
                "$1[REDACTED]",
                RegexOptions.CultureInvariant);

        static bool IsCredentialName(string value)
        {
            var canonical = new string(value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant).ToArray());
            return canonical.Contains("password") || canonical.Contains("secret")
                || canonical.Contains("authorization") || canonical.Contains("bearer")
                || canonical.Contains("accesstoken") || canonical.Contains("apikey")
                || canonical.Contains("keystorebase64");
        }

        static bool IsPathName(string? value)
            => value != null && (value.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("root", StringComparison.OrdinalIgnoreCase) >= 0);

        static bool IsAbsolutePath(string value)
            => !string.IsNullOrEmpty(value) && (value[0] == '/' || value[0] == '\\'
                || (value.Length > 1 && value[1] == ':'));

        static string BoundUtf8(string value, int maxBytes)
        {
            if (s_utf8.GetByteCount(value) <= maxBytes) return value;
            var result = new StringBuilder(Math.Min(value.Length, maxBytes));
            var used = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var length = char.IsHighSurrogate(value[index])
                    && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])
                        ? 2
                        : 1;
                var part = value.Substring(index, length);
                var partBytes = s_utf8.GetByteCount(part);
                if (used + partBytes > maxBytes) break;
                result.Append(part);
                used += partBytes;
                index += length - 1;
            }
            return result.ToString();
        }

        static string UtcNow() => DateTime.UtcNow.ToString("O");
    }

    /// <summary>
    /// Maintains the per-session script-compilation epoch used by
    /// <see cref="EditorOperationRegistry.CaptureSourceRevision"/>. The counter
    /// survives domain reloads through SessionState; each finished compilation
    /// (including the reload-triggering one) increments it.
    /// </summary>
    [InitializeOnLoad]
    internal static class EditorCompileEpochCounter
    {
        static EditorCompileEpochCounter()
        {
            // Seed the startup compile so the epoch is observable from boot.
            _ = EditorOperationRegistry.CompileEpoch;
            CompilationPipeline.compilationFinished += _ => EditorOperationRegistry.NoteCompilationFinished();
        }
    }
}
