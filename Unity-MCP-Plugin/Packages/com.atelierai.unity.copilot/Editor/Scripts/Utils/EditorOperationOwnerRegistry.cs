/*
┌──────────────────────────────────────────────────────────────────┐
│  Process-local ownership and recovery for durable Editor work.   │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    public enum EditorOperationReloadBehavior
    {
        Interrupt,
        Resume,
        CompleteAfterReload
    }

    public enum EditorOperationRecoveryDisposition
    {
        Interrupted,
        Resumed,
        Completed,
        Failed
    }

    public sealed class EditorOperationRecoveryResult
    {
        public EditorOperationRecoveryDisposition Disposition { get; }
        public string Phase { get; }
        public string? ErrorCode { get; }
        public string? Message { get; }
        public string? ResultJson { get; }

        EditorOperationRecoveryResult(
            EditorOperationRecoveryDisposition disposition,
            string phase,
            string? errorCode = null,
            string? message = null,
            string? resultJson = null)
        {
            Disposition = disposition;
            Phase = string.IsNullOrWhiteSpace(phase) ? "recovery" : phase;
            ErrorCode = errorCode;
            Message = message;
            ResultJson = resultJson;
        }

        public static EditorOperationRecoveryResult Interrupt(
            string phase = "recovery-interrupted",
            string message = "The prior operation owner could not prove safe recovery.")
            => new(EditorOperationRecoveryDisposition.Interrupted, phase,
                "operation_interrupted", message);

        public static EditorOperationRecoveryResult Resume(string phase)
            => new(EditorOperationRecoveryDisposition.Resumed, phase);

        public static EditorOperationRecoveryResult Complete(string resultJson, string phase = "completed-after-reload")
            => new(EditorOperationRecoveryDisposition.Completed, phase,
                resultJson: resultJson);

        public static EditorOperationRecoveryResult Fail(
            string errorCode,
            string message,
            string phase = "recovery-failed")
            => new(EditorOperationRecoveryDisposition.Failed, phase, errorCode, message);
    }

    public interface IEditorOperationOwner
    {
        string Kind { get; }
        string OwnerId { get; }
        EditorOperationReloadBehavior ReloadBehavior { get; }
        void Start(EditorOperationContext operation);
        void RequestCancellation(EditorOperationContext operation, string reason);
        EditorOperationRecoveryResult Recover(EditorOperationContext operation);
    }

    public sealed class EditorOperationContext : IDisposable
    {
        readonly CancellationTokenSource _ownerCancellation = new();
        readonly CancellationTokenSource _linkedCancellation;
        IToolExecutionLease? _executionLease;
        int _disposed;

        internal EditorOperationContext(
            IEditorOperationOwner owner,
            EditorOperationInfo operation,
            CancellationToken cancellationToken = default)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            OperationId = operation.OperationId;
            Kind = operation.Kind;
            _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _ownerCancellation.Token, cancellationToken);
            if (operation.CancellationRequested)
                _ownerCancellation.Cancel(throwOnFirstException: false);
        }

        public IEditorOperationOwner Owner { get; }
        public string OperationId { get; }
        public string Kind { get; }
        public CancellationToken CancellationToken => _linkedCancellation.Token;
        public EditorOperationInfo? Current => EditorOperationRegistry.Get(OperationId);

        public EditorOperationInfo Start(string phase = "running")
        {
            EnsureOwner();
            return EditorOperationRegistry.Start(OperationId, phase);
        }

        public async Task AcquireExecutionAsync()
        {
            EnsureOwner();
            if (_executionLease != null) return;
            var context = new ToolCallContext
            {
                RequestID = OperationId,
                CallId = OperationId,
                CorrelationId = OperationId,
                CancellationToken = CancellationToken,
                DryRun = "none",
                Legacy = false
            };
            var runner = new OperationSchedulingRunner(Kind);
            var lease = await EditorToolExecutionScheduler.Shared.AcquireAsync(
                new ToolExecutionSchedulingRequest(
                    context, runner.Name, runner,
                    AuthoringRiskLevel.Mutating, AuthoringUndoLevel.None),
                CancellationToken);
            if (Interlocked.CompareExchange(ref _executionLease, lease, null) != null)
                lease.Dispose();
        }

        /// <summary>
        /// Releases the serialized-lane lease while keeping the owner registration and
        /// cancellation plumbing alive. Durable owners call this when their exclusive
        /// execution section has finished but the operation must keep waiting on Editor
        /// pacing (e.g. a deferred script compilation) that must never hold the lane.
        /// Resume paths re-acquire via <see cref="AcquireExecutionAsync"/>.
        /// </summary>
        public void ReleaseExecution()
        {
            Interlocked.Exchange(ref _executionLease, null)?.Dispose();
        }

        public EditorOperationInfo Update(
            string phase,
            double progress = -1,
            bool cancellationPending = false,
            string? diagnosticsJson = null)
        {
            EnsureOwner();
            return EditorOperationRegistry.Update(
                OperationId, phase, progress, cancellationPending, diagnosticsJson);
        }

        public EditorOperationInfo Succeed(string? resultJson = null)
        {
            EnsureOwner();
            var result = EditorOperationRegistry.Succeed(OperationId, resultJson);
            EditorOperationOwnerRegistry.Release(OperationId, this);
            return result;
        }

        public EditorOperationInfo Fail(
            string errorCode,
            string message,
            string phase = "failed",
            string? resultJson = null)
        {
            EnsureOwner();
            var result = EditorOperationRegistry.Fail(
                OperationId, errorCode, message, phase, resultJson);
            EditorOperationOwnerRegistry.Release(OperationId, this);
            return result;
        }

        public EditorOperationInfo Cancel(string phase = "cancelled", string? resultJson = null)
        {
            EnsureOwner();
            var result = EditorOperationRegistry.CancelRunning(OperationId, phase, resultJson);
            EditorOperationOwnerRegistry.Release(OperationId, this);
            return result;
        }

        public EditorOperationInfo Interrupt(
            string message,
            string phase = "interrupted")
        {
            EnsureOwner();
            var result = EditorOperationRegistry.Interrupt(OperationId, message, phase);
            EditorOperationOwnerRegistry.Release(OperationId, this);
            return result;
        }

        internal void CancelToken()
        {
            try { _ownerCancellation.Cancel(throwOnFirstException: false); }
            catch (ObjectDisposedException) { }
        }

        internal void EnsureOwner()
        {
            var current = EditorOperationRegistry.Get(OperationId)
                ?? throw new InvalidOperationException(
                    $"Operation '{OperationId}' is no longer registered.");
            if (!string.Equals(current.Kind, Kind, StringComparison.Ordinal)
                || !string.Equals(current.OwnerId, Owner.OwnerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Owner '{Owner.OwnerId}' cannot mutate operation '{OperationId}'.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Interlocked.Exchange(ref _executionLease, null)?.Dispose();
            _linkedCancellation.Dispose();
            _ownerCancellation.Dispose();
        }

        sealed class OperationSchedulingRunner : IRunTool
        {
            public OperationSchedulingRunner(string name) => Name = name;
            public string Name { get; }
            public bool Enabled { get; set; } = true;
            public string? Title => Name;
            public string? Description => null;
            public System.Reflection.MethodInfo? Method => null;
            public string? SkillDescription => null;
            public string? SkillBody => null;
            public System.Text.Json.Nodes.JsonNode? InputSchema => null;
            public System.Text.Json.Nodes.JsonNode? OutputSchema => null;
            public bool? ReadOnlyHint => false;
            public bool? DestructiveHint => null;
            public bool? IdempotentHint => false;
            public bool? OpenWorldHint => false;
            public int TokenCount => 0;
            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, System.Text.Json.JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
                => throw new InvalidOperationException(
                    "Operation scheduling metadata runners are never executed.");
        }
    }

    /// <summary>
    /// Deterministic kind-to-behavior registry. Durable records remain exclusively in
    /// <see cref="EditorOperationRegistry"/>.
    /// </summary>
    public static class EditorOperationOwnerRegistry
    {
        static readonly object s_gate = new();
        static readonly Dictionary<string, IEditorOperationOwner> s_owners =
            new(StringComparer.Ordinal);
        static readonly Dictionary<string, EditorOperationContext> s_active =
            new(StringComparer.Ordinal);
        static bool s_registrationComplete;
        static bool s_reconciled;

        public static bool RegistrationComplete
        {
            get { lock (s_gate) return s_registrationComplete; }
        }

        public static void BeginRegistration()
        {
            lock (s_gate)
            {
                foreach (var context in s_active.Values) context.Dispose();
                s_active.Clear();
                s_owners.Clear();
                s_registrationComplete = false;
                s_reconciled = false;
            }
        }

        public static void Register(IEditorOperationOwner owner)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            var kind = Normalize(owner.Kind, nameof(owner.Kind));
            var ownerId = Normalize(owner.OwnerId, nameof(owner.OwnerId));
            lock (s_gate)
            {
                if (s_registrationComplete)
                    throw new InvalidOperationException(
                        "Operation-owner registration has already completed.");
                if (s_owners.ContainsKey(kind))
                    throw new InvalidOperationException(
                        $"An operation owner is already registered for kind '{kind}'.");
                if (owner.ReloadBehavior != EditorOperationReloadBehavior.Interrupt
                    && owner.ReloadBehavior != EditorOperationReloadBehavior.Resume
                    && owner.ReloadBehavior != EditorOperationReloadBehavior.CompleteAfterReload)
                {
                    throw new InvalidOperationException(
                        $"Owner '{ownerId}' has an unsupported reload behavior.");
                }
                s_owners.Add(kind, owner);
            }
        }

        public static void CompleteRegistrationAndReconcile()
        {
            lock (s_gate)
                s_registrationComplete = true;
            ReconcilePriorGeneration();
        }

        public static EditorOperationInfo Create(
            string kind,
            string phase = "queued",
            string? metadataJson = null,
            string? sourceRevision = null)
        {
            var owner = RequireOwner(kind);
            return EditorOperationRegistry.Create(
                owner.Kind,
                phase,
                metadataJson,
                owner.OwnerId,
                ToWire(owner.ReloadBehavior),
                sourceRevision);
        }

        public static EditorOperationContext BeginExecution(
            string operationId,
            string phase,
            CancellationToken cancellationToken = default)
            => BeginExecutionAsync(operationId, phase, cancellationToken)
                .GetAwaiter().GetResult();

        public static async Task<EditorOperationContext> BeginExecutionAsync(
            string operationId,
            string phase,
            CancellationToken cancellationToken = default)
        {
            var context = Start(operationId, cancellationToken);
            try
            {
                await context.AcquireExecutionAsync();
                context.Start(phase);
                context.Owner.Start(context);
                return context;
            }
            catch (OperationCanceledException)
            {
                var current = context.Current;
                if (current != null && !current.IsTerminal)
                    context.Cancel("cancelled-before-scheduler-admission");
                throw;
            }
            catch
            {
                var current = context.Current;
                if (current != null && !current.IsTerminal)
                {
                    context.Fail(
                        "operation_owner_start_failed",
                        "The registered operation owner could not start the operation.",
                        "owner-start-failed");
                }
                else
                {
                    Release(operationId, context);
                }
                throw;
            }
        }

        public static EditorOperationContext Start(
            string operationId,
            CancellationToken cancellationToken = default)
        {
            var operation = EditorOperationRegistry.Get(operationId)
                ?? throw new ToolCallControlException(
                    ToolCallErrorCodes.OperationNotFound,
                    "The requested Editor operation was not found.");
            if (operation.IsTerminal)
                throw new InvalidOperationException(
                    $"Editor operation '{operationId}' is already terminal ({operation.Status}).");
            var owner = RequireOwner(operation.Kind);
            lock (s_gate)
            {
                if (s_active.TryGetValue(operationId, out var existing)) return existing;
                if (!string.Equals(operation.OwnerId, owner.OwnerId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operationId}' belongs to owner '{operation.OwnerId}', not '{owner.OwnerId}'.");
                }
                var context = new EditorOperationContext(owner, operation, cancellationToken);
                s_active.Add(operationId, context);
                return context;
            }
        }

        public static void ScheduleExecution(
            string operationId,
            string phase,
            Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            // Reserve FIFO position immediately while the initiating call still owns its
            // lease. The record remains queued until the admitted next-Editor-tick callback.
            _ = ReserveScheduledExecution(operationId, phase, action);
        }

        static async Task ReserveScheduledExecution(
            string operationId,
            string phase,
            Action action)
        {
            EditorOperationContext? context = null;
            try
            {
                context = Start(operationId);
                await context.AcquireExecutionAsync();
                var admitted = context;
                EditorOperationRegistry.Schedule(
                    () => RunAdmittedExecution(admitted, phase, action));
            }
            catch (OperationCanceledException)
            {
                var current = EditorOperationRegistry.Get(operationId);
                if (current != null && !current.IsTerminal)
                    EditorOperationRegistry.CancelRunning(operationId, "cancelled-before-execution");
            }
            catch
            {
                var current = EditorOperationRegistry.Get(operationId);
                if (current != null && !current.IsTerminal)
                {
                    EditorOperationRegistry.Fail(
                        operationId,
                        "operation_execution_failed",
                        "The owned operation failed before its runner completed.",
                        "execution-failed");
                }
                else if (context != null)
                {
                    Release(operationId, context);
                }
            }
        }

        static void RunAdmittedExecution(
            EditorOperationContext context,
            string phase,
            Action action)
        {
            try
            {
                var current = context.Current;
                if (current == null || current.IsTerminal)
                {
                    Release(context.OperationId, context);
                    return;
                }
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Start(phase);
                context.Owner.Start(context);
                action();
            }
            catch (OperationCanceledException)
            {
                var current = context.Current;
                if (current != null && !current.IsTerminal)
                    context.Cancel("cancelled-before-execution");
            }
            catch
            {
                var current = context.Current;
                if (current != null && !current.IsTerminal)
                {
                    context.Fail(
                        "operation_execution_failed",
                        "The owned operation failed before its runner completed.",
                        "execution-failed");
                }
            }
        }

        public static EditorOperationInfo RequestCancellation(
            string operationId,
            string reason = "explicit-operation-cancel")
        {
            var operation = EditorOperationRegistry.Get(operationId)
                ?? throw new ToolCallControlException(
                    ToolCallErrorCodes.OperationNotFound,
                    "The requested Editor operation was not found.");
            if (operation.IsTerminal) return operation;
            var owner = RequireOwner(operation.Kind);
            var requested = EditorOperationRegistry.RequestCancellation(operationId);
            if (requested.IsTerminal)
            {
                EditorOperationContext? queuedContext;
                lock (s_gate)
                    s_active.TryGetValue(operationId, out queuedContext);
                queuedContext?.CancelToken();
                Release(operationId);
                return requested;
            }

            var context = Start(operationId);
            context.CancelToken();
            owner.RequestCancellation(context, BoundReason(reason));
            return EditorOperationRegistry.Get(operationId)!;
        }

        internal static void ResetReconciliationEpoch()
        {
            lock (s_gate)
                s_reconciled = false;
        }

        public static void ReconcilePriorGeneration()
        {
            lock (s_gate)
            {
                if (!s_registrationComplete || s_reconciled) return;
                s_reconciled = true;
            }

            foreach (var operation in EditorOperationRegistry.ListAllForReconciliation()
                         .Where(operation => !operation.IsTerminal
                             && (operation.EditorPid != EditorOperationRegistry.EditorPid
                                 || operation.DomainGeneration != EditorOperationRegistry.DomainGeneration)))
            {
                Reconcile(operation);
            }
        }

        static void Reconcile(EditorOperationInfo operation)
        {
            IEditorOperationOwner? owner;
            lock (s_gate)
                s_owners.TryGetValue(operation.Kind, out owner);
            if (owner == null)
            {
                EditorOperationRegistry.Interrupt(
                    operation.OperationId,
                    "No operation owner is registered for this kind.",
                    "operation-owner-missing",
                    "operation_owner_missing");
                return;
            }
            if (!string.Equals(operation.OwnerId, owner.OwnerId, StringComparison.Ordinal))
            {
                EditorOperationRegistry.Interrupt(
                    operation.OperationId,
                    "The registered operation owner does not match the persisted owner.",
                    "operation-owner-mismatch");
                return;
            }
            if (owner.ReloadBehavior == EditorOperationReloadBehavior.Interrupt
                || !string.Equals(operation.ReloadBehavior, ToWire(owner.ReloadBehavior), StringComparison.Ordinal))
            {
                EditorOperationRegistry.Interrupt(
                    operation.OperationId,
                    "The operation cannot be resumed safely after reload.",
                    "reload-interrupted");
                return;
            }

            EditorOperationRecoveryResult recovery;
            EditorOperationContext context;
            try
            {
                context = Start(operation.OperationId);
                recovery = owner.Recover(context)
                    ?? EditorOperationRecoveryResult.Interrupt();
            }
            catch
            {
                EditorOperationRegistry.Interrupt(
                    operation.OperationId,
                    "The operation owner failed to prove safe recovery.",
                    "recovery-failed");
                Release(operation.OperationId);
                return;
            }

            switch (recovery.Disposition)
            {
                case EditorOperationRecoveryDisposition.Resumed:
                    EditorOperationRegistry.Claim(operation.OperationId, recovery.Phase);
                    _ = ResumeRecoveredExecution(context);
                    break;
                case EditorOperationRecoveryDisposition.Completed:
                    context.Succeed(recovery.ResultJson);
                    break;
                case EditorOperationRecoveryDisposition.Failed:
                    context.Fail(
                        recovery.ErrorCode ?? "operation_recovery_failed",
                        recovery.Message ?? "Operation recovery failed.",
                        recovery.Phase,
                        recovery.ResultJson);
                    break;
                default:
                    context.Interrupt(
                        recovery.Message ?? "The operation could not be recovered safely.",
                        recovery.Phase);
                    break;
            }
        }

        static async Task ResumeRecoveredExecution(EditorOperationContext context)
        {
            try
            {
                await context.AcquireExecutionAsync();
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Owner.Start(context);
            }
            catch (OperationCanceledException)
            {
                var current = context.Current;
                if (current != null && !current.IsTerminal)
                    context.Cancel("cancelled-before-recovery-admission");
            }
            catch
            {
                var current = context.Current;
                if (current != null && !current.IsTerminal)
                {
                    context.Interrupt(
                        "The recovered operation could not reacquire execution ownership.",
                        "recovery-admission-failed");
                }
            }
        }

        public static void InvalidateGeneration()
        {
            EditorOperationContext[] contexts;
            lock (s_gate)
            {
                contexts = s_active.Values.ToArray();
                s_active.Clear();
                s_reconciled = false;
            }
            foreach (var context in contexts)
            {
                context.CancelToken();
                context.Dispose();
            }
        }

        internal static void Release(string operationId, EditorOperationContext? expected = null)
        {
            EditorOperationContext? context = null;
            lock (s_gate)
            {
                if (!s_active.TryGetValue(operationId, out var found)) return;
                if (expected != null && !ReferenceEquals(found, expected)) return;
                s_active.Remove(operationId);
                context = found;
            }
            context.Dispose();
        }

        internal static void TryReleaseExecution(string operationId)
        {
            lock (s_gate)
            {
                if (s_active.TryGetValue(operationId, out var context))
                    context.ReleaseExecution();
            }
        }

        static IEditorOperationOwner RequireOwner(string kind)
        {
            var normalized = Normalize(kind, nameof(kind));
            lock (s_gate)
            {
                if (!s_registrationComplete)
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.EditorSettling,
                        "Operation-owner registration is not complete.",
                        retryable: true);
                if (s_owners.TryGetValue(normalized, out var owner)) return owner;
            }
            throw new ToolCallControlException(
                ToolCallErrorCodes.OperationOwnerMissing,
                "No operation owner is registered for this operation kind.");
        }

        static string Normalize(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Value must be non-empty.", parameterName);
            var trimmed = value.Trim();
            return trimmed.Length <= 160 ? trimmed : trimmed.Substring(0, 160);
        }

        static string ToWire(EditorOperationReloadBehavior behavior)
            => behavior == EditorOperationReloadBehavior.Resume
                ? "resume"
                : behavior == EditorOperationReloadBehavior.CompleteAfterReload
                    ? "complete-after-reload"
                    : "interrupt";

        static string BoundReason(string? reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "explicit-operation-cancel";
            var singleLine = reason.Replace('\r', ' ').Replace('\n', ' ');
            return singleLine.Length <= 160 ? singleLine : singleLine.Substring(0, 160);
        }
    }
}
