#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using NUnit.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    public sealed class EditorOperationSchedulingTests
    {
        string _temporaryDirectory = string.Empty;
        string _operationStore = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _temporaryDirectory = Path.Combine(Path.GetTempPath(),
                "unity-copilot-g006-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            _operationStore = Path.Combine(_temporaryDirectory, "operations.json");
            EditorOperationRegistry.ConfigureStoreForTests(_operationStore);
            EditorOperationOwners.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            EditorOperationOwnerRegistry.InvalidateGeneration();
            EditorOperationRegistry.ConfigureStoreForTests(null);
            EditorOperationOwners.RegisterAndReconcile();
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, true);
        }

        [Test]
        public void Registry_QuarantinesCorruptStateWithoutFalseLiveOperations()
        {
            File.WriteAllText(_operationStore, "{not-json", new System.Text.UTF8Encoding(false));
            EditorOperationRegistry.ConfigureStoreForTests(_operationStore);

            Assert.That(EditorOperationRegistry.List(), Is.Empty);
            Assert.That(File.Exists(_operationStore), Is.False);
            Assert.That(Directory.GetFiles(
                _temporaryDirectory, "operations.json.corrupt-*"), Has.Length.EqualTo(1));
        }

        [Test]
        public void OwnerRegistry_RejectsDuplicateKindsAndRoutesCancellation()
        {
            var first = new FakeOwner("fixture", EditorOperationReloadBehavior.Interrupt);
            var duplicate = new FakeOwner("fixture", EditorOperationReloadBehavior.Resume);
            EditorOperationOwnerRegistry.Register(first);
            Assert.Throws<InvalidOperationException>(() =>
                EditorOperationOwnerRegistry.Register(duplicate));
            EditorOperationOwnerRegistry.CompleteRegistrationAndReconcile();

            var queued = EditorOperationOwnerRegistry.Create("fixture");
            var queuedCancelled = EditorOperationOwnerRegistry.RequestCancellation(queued.OperationId);
            Assert.That(queuedCancelled.Status, Is.EqualTo("cancelled"));
            Assert.That(first.CancellationCalls, Is.Zero);

            var running = EditorOperationOwnerRegistry.Create("fixture");
            var context = EditorOperationOwnerRegistry.BeginExecution(running.OperationId, "running");
            Assert.That(context.OperationId, Is.EqualTo(running.OperationId));
            var cancellation = EditorOperationOwnerRegistry.RequestCancellation(running.OperationId);
            Assert.That(cancellation.CancellationRequested, Is.True);
            Assert.That(cancellation.CancellationPending, Is.True);
            Assert.That(first.CancellationCalls, Is.EqualTo(1));
            Assert.That(context.CancellationToken.IsCancellationRequested, Is.True);
            context.Cancel();
        }

        [Test]
        public void OwnerRegistry_ReconcilesInterruptResumeCompleteAndMissingOwnerWithoutReplay()
        {
            var resume = new FakeOwner("resume", EditorOperationReloadBehavior.Resume)
            {
                Recovery = EditorOperationRecoveryResult.Resume("resumed")
            };
            var complete = new FakeOwner("complete", EditorOperationReloadBehavior.CompleteAfterReload)
            {
                Recovery = EditorOperationRecoveryResult.Complete("{\"done\":true}")
            };
            var interrupt = new FakeOwner("interrupt", EditorOperationReloadBehavior.Interrupt);
            EditorOperationOwnerRegistry.Register(resume);
            EditorOperationOwnerRegistry.Register(complete);
            EditorOperationOwnerRegistry.Register(interrupt);
            EditorOperationOwnerRegistry.CompleteRegistrationAndReconcile();

            WritePriorGenerationOperations(
                Prior("resume", "resume"),
                Prior("complete", "complete-after-reload"),
                Prior("interrupt", "interrupt"),
                Prior("missing", "resume"));
            EditorOperationRegistry.ConfigureStoreForTests(_operationStore);
            EditorOperationRegistry.ReconcileActiveOperations();

            Assert.That(EditorOperationRegistry.Get("prior-resume")!.Status, Is.EqualTo("running"));
            Assert.That(EditorOperationRegistry.Get("prior-resume")!.Phase, Is.EqualTo("resumed"));
            Assert.That(EditorOperationRegistry.Get("prior-complete")!.Status, Is.EqualTo("succeeded"));
            Assert.That(EditorOperationRegistry.Get("prior-interrupt")!.Status, Is.EqualTo("interrupted"));
            Assert.That(EditorOperationRegistry.Get("prior-missing")!.Status, Is.EqualTo("interrupted"));
            Assert.That(resume.StartCalls, Is.EqualTo(1),
                "resume starts once only after validated recovery and scheduler admission");
            Assert.That(complete.StartCalls + interrupt.StartCalls, Is.Zero,
                "completion and interruption must never replay owner side effects");
        }

        [Test]
        public void Registry_BoundsAndStructurallyRedactsPayloadsAndProtectsActiveCapacity()
        {
            EditorOperationOwnerRegistry.Register(
                new FakeOwner("fixture", EditorOperationReloadBehavior.Interrupt));
            EditorOperationOwnerRegistry.CompleteRegistrationAndReconcile();
            var secret = "synthetic-secret-value";
            var absolutePath = "C:/private/fixture.txt";
            var large = new string('x', EditorOperationRegistry.MaxPayloadBytes + 1000);
            var operation = EditorOperationOwnerRegistry.Create(
                "fixture",
                metadataJson: "{\"password\":\"" + secret +
                    "\",\"outputPath\":\"" + absolutePath +
                    "\",\"safe\":\"ok\",\"large\":\"" + large + "\"}");
            var stored = EditorOperationRegistry.Get(operation.OperationId)!;
            Assert.That(stored.MetadataJson, Does.Not.Contain(secret));
            Assert.That(stored.MetadataJson, Does.Not.Contain(absolutePath));
            Assert.That(System.Text.Encoding.UTF8.GetByteCount(stored.MetadataJson!),
                Is.LessThanOrEqualTo(EditorOperationRegistry.MaxPayloadBytes));

            for (var index = 1; index < EditorOperationRegistry.MaxActiveOperationsPerInstance; index++)
                EditorOperationOwnerRegistry.Create("fixture");
            var capacity = Assert.Throws<EditorOperationCapacityException>(() =>
                EditorOperationOwnerRegistry.Create("fixture"));
            Assert.That(capacity!.Code, Is.EqualTo(ToolCallErrorCodes.OperationCapacityExceeded));
            Assert.That(capacity.Retryable, Is.True);
        }

        [Test]
        public void Scheduler_SerializesFIFOAndRemovesCancelledWaiters()
        {
            using var scheduler = new EditorToolExecutionScheduler(() => "editor-a");
            var request = Request("write", threadSafeRead: false);
            var first = scheduler.AcquireAsync(request).GetAwaiter().GetResult();
            var secondTask = scheduler.AcquireAsync(request);
            using var cancellation = new CancellationTokenSource();
            var cancelledTask = scheduler.AcquireAsync(request, cancellation.Token);
            var thirdTask = scheduler.AcquireAsync(request);
            Assert.That(secondTask.IsCompleted, Is.False);
            Assert.That(thirdTask.IsCompleted, Is.False);
            cancellation.Cancel();
            Assert.Catch<OperationCanceledException>(() =>
                cancelledTask.GetAwaiter().GetResult());

            first.Dispose();
            var second = secondTask.GetAwaiter().GetResult();
            Assert.That(thirdTask.IsCompleted, Is.False);
            second.Dispose();
            var third = thirdTask.GetAwaiter().GetResult();
            Assert.That(second.Sequence, Is.LessThan(third.Sequence));
            third.Dispose();
        }

        [Test]
        public void Scheduler_AllowsExplicitReadsAcrossInstancesAndReportsReadiness()
        {
            using var schedulerA = new EditorToolExecutionScheduler(() => "editor-a");
            using var schedulerB = new EditorToolExecutionScheduler(() => "editor-b");
            var read = Request("read", threadSafeRead: true);
            var a1 = schedulerA.AcquireAsync(read).GetAwaiter().GetResult();
            var a2 = schedulerA.AcquireAsync(read).GetAwaiter().GetResult();
            var b1 = schedulerB.AcquireAsync(Request("write", false)).GetAwaiter().GetResult();

            Assert.That(a1.Serialized, Is.False);
            Assert.That(a2.Serialized, Is.False);
            Assert.That(b1.InstanceId, Is.EqualTo("editor-b"));
            Assert.That(schedulerA.Snapshot().RunningReads, Is.EqualTo(2));
            Assert.That(schedulerB.Snapshot().RunningSideEffects, Is.EqualTo(1));

            a1.Dispose();
            a2.Dispose();
            b1.Dispose();
        }

        [Test]
        public void Scheduler_FailsClosedForContradictoryReadMetadata()
        {
            var runner = new FakeRunner
            {
                ReadOnlyHint = false,
                ExecutionScheduling = new ToolExecutionSchedulingMetadata
                {
                    ExecutionAffinity = ToolExecutionAffinity.Background,
                    ThreadSafeRead = true
                }
            };
            Assert.Throws<ToolCallControlException>(() => new ToolExecutionSchedulingRequest(
                Context("contradictory"), runner.Name, runner,
                AuthoringRiskLevel.Mutating, AuthoringUndoLevel.None));
        }

        static ToolExecutionSchedulingRequest Request(string name, bool threadSafeRead)
        {
            var runner = new FakeRunner
            {
                Name = name,
                ReadOnlyHint = threadSafeRead,
                ExecutionScheduling = threadSafeRead
                    ? new ToolExecutionSchedulingMetadata
                    {
                        ExecutionAffinity = ToolExecutionAffinity.Background,
                        ThreadSafeRead = true
                    }
                    : null
            };
            return new ToolExecutionSchedulingRequest(
                Context(name), name, runner,
                threadSafeRead ? AuthoringRiskLevel.Read : AuthoringRiskLevel.Mutating,
                AuthoringUndoLevel.None);
        }

        static ToolCallContext Context(string id) => new()
        {
            RequestID = id,
            CallId = id,
            CorrelationId = id,
            DryRun = "none",
            Legacy = false
        };

        static EditorOperationInfo Prior(string kind, string reloadBehavior)
            => new()
            {
                OperationId = "prior-" + kind,
                Kind = kind,
                OwnerId = kind,
                ReloadBehavior = reloadBehavior,
                InstanceId = "old-editor",
                Status = "running",
                Phase = "prior-work",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O"),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O"),
                EditorPid = -1,
                DomainGeneration = "old-domain"
            };

        void WritePriorGenerationOperations(params EditorOperationInfo[] operations)
        {
            File.WriteAllText(_operationStore, UnityEngine.JsonUtility.ToJson(
                new EditorOperationStore
                {
                    SchemaVersion = EditorOperationRegistry.SchemaVersion,
                    Operations = operations.ToList()
                }, true));
        }

        sealed class FakeOwner : IEditorOperationOwner
        {
            public FakeOwner(string kind, EditorOperationReloadBehavior reloadBehavior)
            {
                Kind = kind;
                ReloadBehavior = reloadBehavior;
            }

            public string Kind { get; }
            public string OwnerId => Kind;
            public EditorOperationReloadBehavior ReloadBehavior { get; }
            public int StartCalls { get; private set; }
            public int CancellationCalls { get; private set; }
            public EditorOperationRecoveryResult Recovery { get; set; }
                = EditorOperationRecoveryResult.Interrupt();

            public void Start(EditorOperationContext operation) => StartCalls++;

            public void RequestCancellation(EditorOperationContext operation, string reason)
            {
                CancellationCalls++;
                operation.Update("cancellation-pending", cancellationPending: true);
            }

            public EditorOperationRecoveryResult Recover(EditorOperationContext operation)
                => Recovery;
        }

        sealed class FakeRunner : IRunTool
        {
            public string Name { get; set; } = "fixture";
            public bool Enabled { get; set; } = true;
            public string? Title => Name;
            public string? Description => null;
            public System.Reflection.MethodInfo? Method => null;
            public string? SkillDescription => null;
            public string? SkillBody => null;
            public System.Text.Json.Nodes.JsonNode? InputSchema => null;
            public System.Text.Json.Nodes.JsonNode? OutputSchema => null;
            public bool? ReadOnlyHint { get; set; }
            public bool? DestructiveHint => false;
            public bool? IdempotentHint => true;
            public bool? OpenWorldHint => false;
            public ToolExecutionSchedulingMetadata? ExecutionScheduling { get; set; }
            public int TokenCount => 0;
            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, System.Text.Json.JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
                => Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
        }
    }
}
