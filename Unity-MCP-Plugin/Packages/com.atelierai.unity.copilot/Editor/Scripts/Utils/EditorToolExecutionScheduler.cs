/*
┌──────────────────────────────────────────────────────────────────┐
│  Per-Editor bounded execution admission and readiness snapshot.  │
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
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Editor.Utils
{
    public sealed class EditorReadinessSnapshot
    {
        public string State { get; set; } = "settling";
        public bool Ready { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public string Generation { get; set; } = string.Empty;
        public string[] Blockers { get; set; } = Array.Empty<string>();
        public int RetryAfterMs { get; set; } = 250;
        public int QueuedSideEffects { get; set; }
        public int RunningSideEffects { get; set; }
        public int QueuedReads { get; set; }
        public int RunningReads { get; set; }
        public int ActiveOperations { get; set; }
    }

    public sealed class EditorToolExecutionScheduler : IToolExecutionScheduler, IDisposable
    {
        public const int MaxQueuedPerInstance = 64;
        public const int MaxParallelReadsPerInstance = 4;
        public static EditorToolExecutionScheduler Shared { get; }
            = new EditorToolExecutionScheduler();

        readonly object _gate = new();
        readonly Dictionary<string, InstanceLane> _lanes = new(StringComparer.Ordinal);
        readonly Func<string> _instanceId;
        long _sequence;
        int _disposed;

        public EditorToolExecutionScheduler(Func<string>? instanceId = null)
            => _instanceId = instanceId ?? (() => EditorOperationRegistry.InstanceId);

        public Task<IToolExecutionLease> AcquireAsync(
            ToolExecutionSchedulingRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var instanceId = BoundInstanceId(_instanceId());
            InstanceLane lane;
            lock (_gate)
            {
                if (!_lanes.TryGetValue(instanceId, out lane!))
                {
                    lane = new InstanceLane(instanceId, RemoveEmptyLane);
                    _lanes.Add(instanceId, lane);
                }
            }
            var sequence = Interlocked.Increment(ref _sequence);
            if (request.IsCancellationControl)
            {
                return Task.FromResult<IToolExecutionLease>(
                    new ControlLease(instanceId, sequence));
            }
            // Console attribution (COCli-07): only serialized-lane (main
            // thread) executions publish their call identity; background read
            // leases never attribute main-thread log emission.
            return request.UsesSerializedLane
                ? lane.AcquireSerializedAsync(sequence, cancellationToken, new LogCallScope.Frame
                {
                    CallId = request.Context.CallId,
                    CorrelationId = request.Context.CorrelationId,
                })
                : lane.AcquireReadAsync(sequence, cancellationToken);
        }

        public EditorReadinessSnapshot Snapshot(
            bool connected = true,
            bool ignoreCurrentSerializedCall = false)
        {
            var instanceId = BoundInstanceId(_instanceId());
            LaneSnapshot lane;
            lock (_gate)
                lane = _lanes.TryGetValue(instanceId, out var value)
                    ? value.Snapshot()
                    : new LaneSnapshot();
            if (ignoreCurrentSerializedCall && lane.RunningSerialized == 1)
                lane.RunningSerialized = 0;

            var activeOperations = EditorOperationRegistry.List(
                includeTerminal: false,
                limit: EditorOperationRegistry.MaxListResults).Length;
            var blockers = new List<string>();
            string state;
            if (!connected)
            {
                state = "disconnected";
                blockers.Add("transport");
            }
            else if (!EditorOperationOwnerRegistry.RegistrationComplete)
            {
                state = "settling";
                blockers.Add("operation-owner-registration");
            }
            else if (EditorApplication.isCompiling)
            {
                state = "compiling";
                blockers.Add("compilation");
            }
            else if (EditorApplication.isUpdating)
            {
                state = "importing";
                blockers.Add("asset-import");
            }
            else if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
            {
                state = "playmode-transition";
                blockers.Add("playmode-transition");
            }
            else if (BuildPipeline.isBuildingPlayer)
            {
                state = "building";
                blockers.Add("player-build");
            }
            else if (lane.RunningSerialized > 0 || lane.QueuedSerialized > 0)
            {
                state = "busy";
                blockers.Add(lane.RunningSerialized > 0
                    ? "serialized-lane-running"
                    : "serialized-lane-queued");
            }
            else if (activeOperations > 0)
            {
                state = "busy";
                blockers.Add("active-operation");
            }
            else
            {
                state = "ready";
            }

            return new EditorReadinessSnapshot
            {
                State = state,
                Ready = state == "ready",
                InstanceId = instanceId,
                Generation = EditorOperationRegistry.DomainGeneration,
                Blockers = blockers.Take(8).ToArray(),
                RetryAfterMs = RetryAfter(state, lane),
                QueuedSideEffects = lane.QueuedSerialized,
                RunningSideEffects = lane.RunningSerialized,
                QueuedReads = lane.QueuedReads,
                RunningReads = lane.RunningReads,
                ActiveOperations = activeOperations
            };
        }

        public void InvalidateInstance()
        {
            InstanceLane[] lanes;
            lock (_gate)
            {
                lanes = _lanes.Values.ToArray();
                _lanes.Clear();
            }
            foreach (var lane in lanes)
                lane.Invalidate();
        }

        void RemoveEmptyLane(string instanceId, InstanceLane lane)
        {
            lock (_gate)
            {
                if (_lanes.TryGetValue(instanceId, out var current)
                    && ReferenceEquals(current, lane)
                    && lane.IsEmpty)
                {
                    _lanes.Remove(instanceId);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            InvalidateInstance();
        }

        void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(EditorToolExecutionScheduler));
        }

        static int RetryAfter(string state, LaneSnapshot lane)
            => state == "ready" ? 50
                : state == "compiling" || state == "importing" ? 500
                : state == "building" ? 1000
                : Math.Max(50, Math.Min(5000,
                    100 + 50 * (lane.QueuedSerialized + lane.QueuedReads)));

        static string BoundInstanceId(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown-editor";
            var trimmed = value.Trim();
            return trimmed.Length <= 160 ? trimmed : trimmed.Substring(0, 160);
        }

        sealed class ControlLease : IToolExecutionLease
        {
            public ControlLease(string instanceId, long sequence)
            {
                InstanceId = instanceId;
                Sequence = sequence;
            }

            public string InstanceId { get; }
            public long Sequence { get; }
            public bool Serialized => false;
            public bool Waited => false;
            public void Dispose() { }
        }

        sealed class InstanceLane
        {
            readonly object _gate = new();
            readonly LinkedList<Waiter> _serialized = new();
            readonly LinkedList<Waiter> _reads = new();
            readonly string _instanceId;
            readonly Action<string, InstanceLane> _onEmpty;
            bool _serializedRunning;
            int _readsRunning;
            bool _invalidated;

            public InstanceLane(string instanceId, Action<string, InstanceLane> onEmpty)
            {
                _instanceId = instanceId;
                _onEmpty = onEmpty;
            }

            public string InstanceId => _instanceId;

            public bool IsEmpty
            {
                get
                {
                    lock (_gate)
                        return !_serializedRunning && _readsRunning == 0
                            && _serialized.Count == 0 && _reads.Count == 0;
                }
            }

            public Task<IToolExecutionLease> AcquireSerializedAsync(
                long sequence,
                CancellationToken cancellationToken,
                LogCallScope.Frame? scopeFrame)
            {
                lock (_gate)
                {
                    ThrowIfInvalidated();
                    EnforceCapacity();
                    if (!_serializedRunning && _serialized.Count == 0)
                    {
                        _serializedRunning = true;
                        return Task.FromResult<IToolExecutionLease>(
                            new Lease(this, sequence, serialized: true, waited: false, scopeFrame));
                    }
                    var waiter = new Waiter(sequence, serialized: true) { ScopeFrame = scopeFrame };
                    waiter.Node = _serialized.AddLast(waiter);
                    waiter.Register(cancellationToken, CancelWaiter);
                    return waiter.Completion.Task;
                }
            }

            public Task<IToolExecutionLease> AcquireReadAsync(
                long sequence,
                CancellationToken cancellationToken)
            {
                lock (_gate)
                {
                    ThrowIfInvalidated();
                    EnforceCapacity();
                    if (_readsRunning < MaxParallelReadsPerInstance && _reads.Count == 0)
                    {
                        _readsRunning++;
                        return Task.FromResult<IToolExecutionLease>(
                            new Lease(this, sequence, serialized: false, waited: false, null));
                    }
                    var waiter = new Waiter(sequence, serialized: false);
                    waiter.Node = _reads.AddLast(waiter);
                    waiter.Register(cancellationToken, CancelWaiter);
                    return waiter.Completion.Task;
                }
            }

            void EnforceCapacity()
            {
                if (_serialized.Count + _reads.Count >= MaxQueuedPerInstance)
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.OperationCapacityExceeded,
                        "Editor execution admission capacity is full.",
                        retryable: true);
                }
            }

            void CancelWaiter(Waiter waiter)
            {
                var removed = false;
                lock (_gate)
                {
                    if (waiter.Node?.List != null)
                    {
                        waiter.Node.List.Remove(waiter.Node);
                        removed = true;
                    }
                    waiter.Node = null;
                }
                if (removed)
                    waiter.Completion.TrySetCanceled(waiter.CancellationToken);
                RemoveIfEmpty();
            }

            public void Release(bool serialized)
            {
                List<(Waiter Waiter, IToolExecutionLease Lease)> admitted = new();
                lock (_gate)
                {
                    if (serialized)
                        _serializedRunning = false;
                    else
                        _readsRunning = Math.Max(0, _readsRunning - 1);

                    if (!_serializedRunning)
                    {
                        var serializedWaiter = Dequeue(_serialized);
                        if (serializedWaiter != null)
                        {
                            _serializedRunning = true;
                            admitted.Add((serializedWaiter,
                                new Lease(this, serializedWaiter.Sequence, serialized: true, waited: true, serializedWaiter.ScopeFrame)));
                        }
                    }

                    while (_readsRunning < MaxParallelReadsPerInstance)
                    {
                        var waiter = Dequeue(_reads);
                        if (waiter == null) break;
                        _readsRunning++;
                        admitted.Add((waiter,
                            new Lease(this, waiter.Sequence, serialized: false, waited: true, null)));
                    }
                }
                foreach (var item in admitted)
                {
                    item.Waiter.DisposeRegistration();
                    item.Waiter.Completion.TrySetResult(item.Lease);
                }
                RemoveIfEmpty();
            }

            static Waiter? Dequeue(LinkedList<Waiter> queue)
            {
                while (queue.First != null)
                {
                    var waiter = queue.First.Value;
                    queue.RemoveFirst();
                    waiter.Node = null;
                    if (!waiter.CancellationToken.IsCancellationRequested)
                        return waiter;
                    waiter.DisposeRegistration();
                    waiter.Completion.TrySetCanceled(waiter.CancellationToken);
                }
                return null;
            }

            public LaneSnapshot Snapshot()
            {
                lock (_gate)
                {
                    return new LaneSnapshot
                    {
                        QueuedSerialized = _serialized.Count,
                        RunningSerialized = _serializedRunning ? 1 : 0,
                        QueuedReads = _reads.Count,
                        RunningReads = _readsRunning
                    };
                }
            }

            public void Invalidate()
            {
                Waiter[] waiters;
                lock (_gate)
                {
                    if (_invalidated) return;
                    _invalidated = true;
                    waiters = _serialized.Concat(_reads).ToArray();
                    _serialized.Clear();
                    _reads.Clear();
                }
                foreach (var waiter in waiters)
                {
                    waiter.Node = null;
                    waiter.DisposeRegistration();
                    waiter.Completion.TrySetException(
                        new ToolCallControlException(
                            ToolCallErrorCodes.EditorSettling,
                            "Editor generation changed while the call was queued.",
                            retryable: true));
                }
            }

            void ThrowIfInvalidated()
            {
                if (_invalidated)
                {
                    throw new ToolCallControlException(
                        ToolCallErrorCodes.EditorSettling,
                        "Editor generation is settling.",
                        retryable: true);
                }
            }

            void RemoveIfEmpty()
            {
                if (IsEmpty) _onEmpty(_instanceId, this);
            }
        }

        sealed class Waiter
        {
            CancellationTokenRegistration _registration;

            public Waiter(long sequence, bool serialized)
            {
                Sequence = sequence;
                Serialized = serialized;
            }

            public long Sequence { get; }
            public bool Serialized { get; }
            public LogCallScope.Frame? ScopeFrame { get; set; }
            public TaskCompletionSource<IToolExecutionLease> Completion { get; }
                = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public LinkedListNode<Waiter>? Node { get; set; }
            public CancellationToken CancellationToken { get; private set; }

            public void Register(CancellationToken token, Action<Waiter> cancel)
            {
                CancellationToken = token;
                if (token.CanBeCanceled)
                    _registration = token.Register(() => cancel(this));
            }

            public void DisposeRegistration() => _registration.Dispose();
        }

        sealed class Lease : IToolExecutionLease
        {
            InstanceLane? _lane;
            readonly IDisposable? _scope;

            public Lease(
                InstanceLane lane,
                long sequence,
                bool serialized,
                bool waited,
                LogCallScope.Frame? scopeFrame)
            {
                _lane = lane;
                InstanceId = lane.InstanceId;
                Sequence = sequence;
                Serialized = serialized;
                Waited = waited;
                // Serialized leases publish their call identity for the whole
                // execution window; it is withdrawn before the lane is released.
                _scope = serialized && scopeFrame != null ? LogCallScope.Push(scopeFrame) : null;
            }

            public string InstanceId { get; }
            public long Sequence { get; }
            public bool Serialized { get; }
            public bool Waited { get; }

            public void Dispose()
            {
                _scope?.Dispose();
                if (Serialized)
                    LogCallScope.SetOperationOverlay(null);
                Interlocked.Exchange(ref _lane, null)?.Release(Serialized);
            }
        }

        sealed class LaneSnapshot
        {
            public int QueuedSerialized;
            public int RunningSerialized;
            public int QueuedReads;
            public int RunningReads;
        }
    }
}
