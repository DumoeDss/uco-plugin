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
using System.Threading;
using System.Text.Json.Nodes;

namespace com.AtelierAI.Uco.Framework
{
    public enum AuthoringRollbackStatus
    {
        None,
        Complete,
        Partial,
    }

    /// <summary>Bounded, non-sensitive information about one affected value.</summary>
    public sealed class AuthoringAffectedObjectSummary
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? RelativePath { get; set; }

        public AuthoringAffectedObjectSummary CloneSafe()
            => new AuthoringAffectedObjectSummary
            {
                Kind = Bound(Kind),
                Name = Bound(Name),
                RelativePath = SafeRelative(RelativePath),
            };

        private static string? Bound(string? value)
            => value == null ? null : value.Length <= 160 ? value : value.Substring(0, 160);

        private static string? SafeRelative(string? value)
        {
            var bounded = Bound(value);
            if (bounded == null
                || bounded.StartsWith("/", StringComparison.Ordinal)
                || bounded.StartsWith("\\", StringComparison.Ordinal)
                || (bounded.Length > 1 && bounded[1] == ':'))
                return null;
            return bounded.Replace('\\', '/');
        }
    }

    /// <summary>
    /// Immediate report for one top-level authoring transaction.  The report
    /// is intentionally process-local and does not represent durable
    /// operation state or a reload-recovery record.
    /// </summary>
    public sealed class AuthoringTransactionReport
    {
        public AuthoringUndoLevel UndoLevel { get; internal set; }
        public string Undo => UndoLevel.ToWireValue();
        public bool Mutated { get; internal set; }
        public int? GroupId { get; internal set; }
        public string? GroupLabel { get; internal set; }
        public AuthoringRollbackStatus Rollback { get; internal set; }
        public bool Completed { get; internal set; }
        public bool Aborted { get; internal set; }
        public bool Shared { get; internal set; }
        public bool Pending { get; internal set; }
        public IReadOnlyList<AuthoringAffectedObjectSummary> AffectedObjects { get; internal set; }
            = Array.Empty<AuthoringAffectedObjectSummary>();

        public AuthoringTransactionReport CloneSafe()
            => new AuthoringTransactionReport
            {
                UndoLevel = UndoLevel,
                Mutated = Mutated,
                GroupId = GroupId,
                GroupLabel = GroupLabel == null
                    ? null
                    : GroupLabel.Length <= 160 ? GroupLabel : GroupLabel.Substring(0, 160),
                Rollback = Rollback,
                Completed = Completed,
                Aborted = Aborted,
                Shared = Shared,
                Pending = Pending,
                AffectedObjects = AffectedObjects == null
                    ? Array.Empty<AuthoringAffectedObjectSummary>()
                    : AffectedObjects.Where(value => value != null).Take(64)
                        .Select(value => value.CloneSafe()).ToArray(),
            };

        public JsonObject ToJson()
        {
            var safe = CloneSafe();
            var objects = new JsonArray();
            foreach (var value in safe.AffectedObjects)
            {
                objects.Add(new JsonObject
                {
                    ["kind"] = value.Kind,
                    ["name"] = value.Name,
                    ["relativePath"] = value.RelativePath,
                });
            }

            return new JsonObject
            {
                ["undo"] = safe.Undo,
                ["mutated"] = safe.Mutated,
                ["groupId"] = safe.GroupId,
                ["groupLabel"] = safe.GroupLabel,
                ["rollback"] = safe.Rollback.ToString().ToLowerInvariant(),
                ["completed"] = safe.Completed,
                ["aborted"] = safe.Aborted,
                ["shared"] = safe.Shared,
                ["pending"] = safe.Pending,
                ["affectedObjects"] = objects,
            };
        }
    }

    /// <summary>
    /// Opaque position inside a shared transaction. It permits one child to
    /// report only the records added during its own execution.
    /// </summary>
    public readonly struct AuthoringTransactionCheckpoint
    {
        internal int EventCount { get; }
        internal int MutationRevision { get; }
        internal int PartialRevision { get; }

        internal AuthoringTransactionCheckpoint(
            int eventCount,
            int mutationRevision,
            int partialRevision)
        {
            EventCount = eventCount;
            MutationRevision = mutationRevision;
            PartialRevision = partialRevision;
        }
    }

    /// <summary>Transaction seam implemented by the Unity Editor adapter.</summary>
    public interface IAuthoringTransaction : IDisposable
    {
        AuthoringTransactionReport Report { get; }
        void RecordCreated(object value);
        void RecordModified(object value, bool completeSnapshot);
        void RecordDeleted(object value);
        /// <summary>
        /// Tracks an object whose Undo record was already registered by the
        /// host API inside this transaction's group (for example Unity's
        /// <c>Undo.AddComponent</c> or pasteboard duplication). No second
        /// host record is created; the object still appears in the report.
        /// </summary>
        void RecordHostRegistered(object value);
        void MarkMutated();
        void Complete();
        void Abort();
    }

    /// <summary>
    /// Optional reporting seam for a transaction that can safely distinguish
    /// records contributed by individual children sharing its group.
    /// </summary>
    public interface IAuthoringSharedTransactionReporter
    {
        AuthoringTransactionCheckpoint CreateCheckpoint();
        AuthoringTransactionReport ReportSince(
            AuthoringTransactionCheckpoint checkpoint,
            AuthoringUndoLevel childUndoLevel);
    }

    /// <summary>Factory attached to one existing runner registration.</summary>
    public interface IAuthoringTransactionFactory
    {
        IAuthoringTransaction Begin(AuthoringInvocation invocation);
    }

    /// <summary>
    /// A small callback-driven transaction implementation useful for tests
    /// and hosts that do not reference Unity.  Unity supplies the callbacks
    /// that call its Undo API; this type owns lifecycle/reporting invariants.
    /// </summary>
    public class AuthoringTransaction : IAuthoringTransaction, IAuthoringSharedTransactionReporter
    {
        private readonly Action<object>? _recordCreated;
        private readonly Action<object, bool>? _recordModified;
        private readonly Action<object>? _recordDeleted;
        private readonly Action? _complete;
        private readonly Action? _abort;
        private readonly List<AuthoringAffectedObjectSummary> _affected =
            new List<AuthoringAffectedObjectSummary>();
        private readonly List<AuthoringAffectedObjectSummary> _affectedEvents =
            new List<AuthoringAffectedObjectSummary>();
        private readonly AuthoringUndoLevel _advertisedUndo;
        private int _state;
        private int _mutationRevision;
        private int _partialRevision;
        private int _recordRevision;
        private int _lastMarkedRecordRevision;
        private bool _sawPartial;

        public AuthoringTransaction(
            string groupLabel,
            AuthoringUndoLevel advertisedUndo,
            int? groupId = null,
            Action<object>? recordCreated = null,
            Action<object, bool>? recordModified = null,
            Action<object>? recordDeleted = null,
            Action? complete = null,
            Action? abort = null)
        {
            if (string.IsNullOrWhiteSpace(groupLabel))
                throw new ArgumentException("Transaction group label must be non-empty.", nameof(groupLabel));
            _advertisedUndo = advertisedUndo;
            _recordCreated = recordCreated;
            _recordModified = recordModified;
            _recordDeleted = recordDeleted;
            _complete = complete;
            _abort = abort;
            Report = new AuthoringTransactionReport
            {
                UndoLevel = advertisedUndo,
                GroupId = groupId,
                GroupLabel = groupLabel,
            };
        }

        public AuthoringTransactionReport Report { get; }

        public void RecordCreated(object value)
        {
            EnsureOpen();
            if (value == null) throw new ArgumentNullException(nameof(value));
            _recordCreated?.Invoke(value);
            _recordRevision++;
            AddAffected(value);
        }

        public void RecordModified(object value, bool completeSnapshot)
        {
            EnsureOpen();
            if (value == null) throw new ArgumentNullException(nameof(value));
            _recordModified?.Invoke(value, completeSnapshot);
            _recordRevision++;
            if (!completeSnapshot)
            {
                _sawPartial = true;
                _partialRevision++;
            }
            AddAffected(value);
        }

        public void RecordDeleted(object value)
        {
            EnsureOpen();
            if (value == null) throw new ArgumentNullException(nameof(value));
            _recordDeleted?.Invoke(value);
            _recordRevision++;
            AddAffected(value);
        }

        public void RecordHostRegistered(object value)
        {
            EnsureOpen();
            if (value == null) throw new ArgumentNullException(nameof(value));
            _recordRevision++;
            AddAffected(value);
        }

        public void MarkMutated()
        {
            EnsureOpen();
            _mutationRevision++;
            if (_recordRevision == _lastMarkedRecordRevision)
            {
                _sawPartial = true;
                _partialRevision++;
            }
            _lastMarkedRecordRevision = _recordRevision;
            Report.Mutated = true;
        }

        public AuthoringTransactionCheckpoint CreateCheckpoint()
        {
            EnsureOpen();
            return new AuthoringTransactionCheckpoint(
                _affectedEvents.Count,
                _mutationRevision,
                _partialRevision);
        }

        public AuthoringTransactionReport ReportSince(
            AuthoringTransactionCheckpoint checkpoint,
            AuthoringUndoLevel childUndoLevel)
        {
            EnsureOpen();
            var start = Math.Max(0, Math.Min(checkpoint.EventCount, _affectedEvents.Count));
            var childMutated = _mutationRevision > checkpoint.MutationRevision;
            var childPartial = _partialRevision > checkpoint.PartialRevision;
            var effectiveUndo = childUndoLevel;
            if (childMutated && (childPartial || (childUndoLevel == AuthoringUndoLevel.Full && _affectedEvents.Count == start)))
                effectiveUndo = AuthoringUndoLevel.Partial;

            return new AuthoringTransactionReport
            {
                UndoLevel = effectiveUndo,
                Mutated = childMutated,
                GroupId = Report.GroupId,
                GroupLabel = Report.GroupLabel,
                Rollback = AuthoringRollbackStatus.None,
                Completed = false,
                Aborted = false,
                Shared = true,
                Pending = true,
                AffectedObjects = _affectedEvents.Skip(start).Take(64)
                    .Select(value => value.CloneSafe()).ToArray(),
            };
        }

        public void Complete()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;

            try
            {
                _complete?.Invoke();
                Report.Completed = true;
                Report.Aborted = false;
                Report.Pending = false;
                Report.Rollback = AuthoringRollbackStatus.None;
                if (_sawPartial && Report.UndoLevel == AuthoringUndoLevel.Full)
                    Report.UndoLevel = AuthoringUndoLevel.Partial;
                if (Report.UndoLevel == AuthoringUndoLevel.Full && _affected.Count == 0)
                    Report.UndoLevel = _advertisedUndo == AuthoringUndoLevel.None
                        ? AuthoringUndoLevel.None
                        : AuthoringUndoLevel.Partial;
                Report.AffectedObjects = _affected.Take(64).Select(value => value.CloneSafe()).ToArray();
            }
            catch
            {
                // A host callback (for example Undo.CollapseUndoOperations)
                // can fail after the transaction has been marked closed. Put
                // the lifecycle back into the open state long enough for the
                // normal abort path to attempt this transaction's own group.
                // Never leave a completed-looking report after a failed
                // collapse.
                Volatile.Write(ref _state, 0);
                Abort();
                throw;
            }
        }

        public void Abort()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0)
                return;

            try
            {
                _abort?.Invoke();
                // Rollback is only "complete" when the host revert ran and
                // every recorded change was a complete snapshot of a
                // capability that advertises full Undo. A partial snapshot
                // or a partial/none capability cannot prove restoration.
                if (_abort == null || _advertisedUndo == AuthoringUndoLevel.None)
                    Report.Rollback = AuthoringRollbackStatus.None;
                else if (_sawPartial || _advertisedUndo == AuthoringUndoLevel.Partial)
                    Report.Rollback = AuthoringRollbackStatus.Partial;
                else
                    Report.Rollback = AuthoringRollbackStatus.Complete;
            }
            catch
            {
                Report.Rollback = _abort == null || _advertisedUndo == AuthoringUndoLevel.None
                    ? AuthoringRollbackStatus.None
                    : AuthoringRollbackStatus.Partial;
            }

            Report.Completed = false;
            Report.Aborted = true;
            Report.Pending = false;
            if (Report.UndoLevel == AuthoringUndoLevel.Full && Report.Rollback != AuthoringRollbackStatus.Complete)
                Report.UndoLevel = AuthoringUndoLevel.Partial;
            Report.AffectedObjects = _affected.Take(64).Select(value => value.CloneSafe()).ToArray();
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _state) == 0)
                Abort();
        }

        protected virtual void AddAffected(object value)
        {
            // The Unity adapter can replace this report entry with a richer
            // safe summary; a generic host still gets the runtime type name.
            AddAffectedSummary(new AuthoringAffectedObjectSummary
            {
                Kind = value.GetType().Name,
                Name = value.ToString(),
            });
        }

        /// <summary>Appends one bounded affected-object summary to the report.</summary>
        protected void AddAffectedSummary(AuthoringAffectedObjectSummary summary)
        {
            AddAffectedEvent(summary);
            _affected.Add(summary.CloneSafe());
        }

        protected void AddAffectedEvent(AuthoringAffectedObjectSummary summary)
        {
            if (summary == null) throw new ArgumentNullException(nameof(summary));
            _affectedEvents.Add(summary.CloneSafe());
        }

        private void EnsureOpen()
        {
            if (Volatile.Read(ref _state) != 0)
                throw new InvalidOperationException("The authoring transaction is already closed.");
        }
    }

    /// <summary>Ambient transaction visible only to trusted pilot adapters.</summary>
    public static class AuthoringTransactionScope
    {
        private static readonly AsyncLocal<IAuthoringTransaction?> CurrentValue = new AsyncLocal<IAuthoringTransaction?>();

        public static IAuthoringTransaction? Current => CurrentValue.Value;

        public static IDisposable Push(IAuthoringTransaction transaction)
        {
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            var previous = CurrentValue.Value;
            CurrentValue.Value = transaction;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly IAuthoringTransaction? _previous;
            private int _disposed;

            public Scope(IAuthoringTransaction? previous) => _previous = previous;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    CurrentValue.Value = _previous;
            }
        }
    }
}
