/*
┌────────────────────────────────────────────────────────────────────────┐
│  Optional scheduling contract for one guarded tool execution pipeline. │
└────────────────────────────────────────────────────────────────────────┘
*/
#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;

namespace com.IvanMurzak.McpPlugin
{
    public enum ToolExecutionAffinity
    {
        MainThread,
        Background,
        Either
    }

    public interface IToolExecutionSchedulingMetadata
    {
        ToolExecutionAffinity ExecutionAffinity { get; }
        bool ThreadSafeRead { get; }
    }

    public sealed class ToolExecutionSchedulingMetadata : IToolExecutionSchedulingMetadata
    {
        public ToolExecutionAffinity ExecutionAffinity { get; set; }
            = ToolExecutionAffinity.MainThread;
        public bool ThreadSafeRead { get; set; }

        public ToolExecutionSchedulingMetadata Clone()
            => new ToolExecutionSchedulingMetadata
            {
                ExecutionAffinity = ExecutionAffinity,
                ThreadSafeRead = ThreadSafeRead
            };
    }

    public sealed class ToolExecutionSchedulingRequest
    {
        public ToolCallContext Context { get; }
        public string ToolName { get; }
        public IRunTool Runner { get; }
        public AuthoringRiskLevel Risk { get; }
        public AuthoringUndoLevel Undo { get; }
        public ToolExecutionSchedulingMetadata Metadata { get; }
        public bool UsesSerializedLane { get; }
        public bool IsCancellationControl { get; }

        public ToolExecutionSchedulingRequest(
            ToolCallContext context,
            string toolName,
            IRunTool runner,
            AuthoringRiskLevel risk,
            AuthoringUndoLevel undo)
        {
            Context = context ?? throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(toolName))
                throw new ArgumentException("Tool name must be non-empty.", nameof(toolName));
            ToolName = toolName;
            Runner = runner ?? throw new ArgumentNullException(nameof(runner));
            Risk = risk;
            Undo = undo;
            Metadata = runner.ExecutionScheduling?.Clone()
                ?? new ToolExecutionSchedulingMetadata();

            var descriptor = runner.AuthoringCapability;
            var hasWriteBinding = descriptor?.PathBindings != null
                && System.Linq.Enumerable.Any(
                    descriptor.PathBindings,
                    binding => binding != null
                        && binding.Intent != AuthoringPathAccessIntent.Read);
            var contradictory = Metadata.ThreadSafeRead
                && (risk != AuthoringRiskLevel.Read
                    || Metadata.ExecutionAffinity == ToolExecutionAffinity.MainThread
                    || descriptor?.TransactionFactory != null
                    || hasWriteBinding);
            if (contradictory)
            {
                throw new ToolCallControlException(
                    ToolCallErrorCodes.SchedulingMetadataInvalid,
                    "Tool execution scheduling metadata contradicts its authoring contract.",
                    callId: context.CallId,
                    correlationId: context.CorrelationId);
            }

            UsesSerializedLane = !Metadata.ThreadSafeRead
                || Metadata.ExecutionAffinity == ToolExecutionAffinity.MainThread;
            // Calls exempt from lane admission. Cancellation-control tools must
            // reach a target that currently holds the serialized lane for a
            // blocking phase; a new cancel tool must be matched by this pattern
            // (or routed so its own serialized lane cannot be held by its
            // target), otherwise it would queue behind the very operation it is
            // meant to cancel. Approved batch children execute while their root's
            // lease is already held, so admitting them would queue each child
            // behind its own parent and wedge the lane forever; only batch-child
            // derivation sets ParentCallId (bound to the admitted root before
            // dispatch), dry-run children return before admission, and nested
            // batches are rejected, so an ancestor lease always exists.
            IsCancellationControl = toolName == "editor-operation-cancel"
                || toolName.EndsWith("-job-cancel", StringComparison.Ordinal)
                || context.ParentCallId != null;
        }
    }

    public interface IToolExecutionLease : IDisposable
    {
        string InstanceId { get; }
        long Sequence { get; }
        bool Serialized { get; }
        bool Waited => false;
    }

    public interface IToolExecutionScheduler
    {
        Task<IToolExecutionLease> AcquireAsync(
            ToolExecutionSchedulingRequest request,
            CancellationToken cancellationToken = default);
    }

    public sealed class InlineToolExecutionScheduler : IToolExecutionScheduler
    {
        static readonly IToolExecutionLease Lease = new InlineLease();

        public Task<IToolExecutionLease> AcquireAsync(
            ToolExecutionSchedulingRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Lease);
        }

        sealed class InlineLease : IToolExecutionLease
        {
            public string InstanceId => "inline";
            public long Sequence => 0;
            public bool Serialized => true;
            public void Dispose() { }
        }
    }

    public static class ToolExecutionAffinityExtensions
    {
        public static string ToWireValue(this ToolExecutionAffinity affinity)
            => affinity == ToolExecutionAffinity.Background
                ? "background"
                : affinity == ToolExecutionAffinity.Either
                    ? "either"
                    : "main-thread";
    }
}
