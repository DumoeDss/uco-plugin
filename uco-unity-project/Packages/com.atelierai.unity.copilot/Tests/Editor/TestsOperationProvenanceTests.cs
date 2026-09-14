#nullable enable
using System;
using System.IO;
using System.Linq;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using NUnit.Framework;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// COCli-03 contract tests: test operations are always fresh executions
    /// with auditable provenance, blocked causes are distinguishable from
    /// failures, and same-filter reruns never alias prior terminal results.
    /// </summary>
    public sealed class TestsOperationProvenanceTests
    {
        string _temporaryDirectory = string.Empty;
        string _operationStore = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _temporaryDirectory = Path.Combine(Path.GetTempPath(),
                "unity-copilot-cocli03-" + Guid.NewGuid().ToString("N"));
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

        static string TestMetadata(string filters = "EditMode;TestNamespace.Fixture")
            => $"{{\"filters\":\"{filters}\",\"executionTimeoutSeconds\":1800,\"waitTimeoutSeconds\":10}}";

        [Test]
        public void SameFilterReruns_ExecuteFreshOperationsAndNeverAliasPriorTerminalResults()
        {
            // First run for the filter completes terminally with its own result.
            var first = EditorOperationRegistry.Create(
                "tests-run", "queued", TestMetadata(),
                sourceRevision: EditorOperationRegistry.CaptureSourceRevision());
            EditorOperationRegistry.Start(first.OperationId, "executing");
            EditorOperationRegistry.Succeed(first.OperationId,
                "{\"summary\":{\"total\":2,\"passed\":2,\"failed\":0}}");
            var firstTerminal = EditorOperationRegistry.Get(first.OperationId)!;
            Assert.That(firstTerminal.IsTerminal, Is.True);

            // An identical-filter rerun creates a distinct operation that
            // starts queued and owns no result until its own execution ends.
            var second = EditorOperationRegistry.Create(
                "tests-run", "queued", TestMetadata(),
                sourceRevision: EditorOperationRegistry.CaptureSourceRevision());
            Assert.That(second.OperationId, Is.Not.EqualTo(first.OperationId));
            Assert.That(second.Status, Is.EqualTo("queued"));
            Assert.That(second.IsTerminal, Is.False);
            Assert.That(second.ResultJson, Is.Null);

            // The prior terminal result is not aliased into the new record…
            var secondCurrent = EditorOperationRegistry.Get(second.OperationId)!;
            Assert.That(secondCurrent.ResultJson, Is.Null);
            // …and the earlier record still returns its own result.
            Assert.That(EditorOperationRegistry.Get(first.OperationId)!.ResultJson,
                Does.Contain("\"passed\":2"));

            // Completing the rerun produces its own distinct provenance.
            EditorOperationRegistry.Start(second.OperationId, "executing");
            EditorOperationRegistry.Succeed(second.OperationId,
                "{\"summary\":{\"total\":2,\"passed\":1,\"failed\":1}}");
            var secondTerminal = EditorOperationRegistry.Get(second.OperationId)!;
            Assert.That(secondTerminal.ResultJson, Does.Contain("\"failed\":1"));
            Assert.That(firstTerminal.ResultJson, Does.Contain("\"failed\":0"));
        }

        [Test]
        public void TestOperations_ExposeRunProvenanceAndFreshExecutionGuarantee()
        {
            var operation = EditorOperationRegistry.Create(
                "tests-run", "queued", TestMetadata(),
                sourceRevision: EditorOperationRegistry.CaptureSourceRevision());

            // The envelope states the no-reuse guarantee explicitly.
            Assert.That(operation.Execution, Is.EqualTo("fresh"));
            Assert.That(operation.SourceRevision, Is.Not.Null.And.StartsWith("compile:"));

            EditorOperationRegistry.Start(operation.OperationId, "executing");
            EditorOperationRegistry.Succeed(operation.OperationId, "{\"summary\":{}}");

            var terminal = EditorOperationRegistry.Get(operation.OperationId)!;
            Assert.That(terminal.StartedAtUtc, Is.Not.Null.And.Not.Empty);
            Assert.That(terminal.CompletedAtUtc, Is.Not.Null.And.Not.Empty);
            Assert.That(terminal.CreatedAtUtc, Is.Not.Empty);
            // Start/finish ordering is auditable.
            Assert.That(String.CompareOrdinal(terminal.CreatedAtUtc, terminal.StartedAtUtc) <= 0, Is.True);
            Assert.That(String.CompareOrdinal(terminal.StartedAtUtc, terminal.CompletedAtUtc) <= 0, Is.True);
        }

        [Test]
        public void SourceRevision_ChangesWhenTheCompilationEpochAdvances()
        {
            var before = EditorOperationRegistry.CaptureSourceRevision();
            EditorOperationRegistry.NoteCompilationFinished();
            var after = EditorOperationRegistry.CaptureSourceRevision();
            Assert.That(after, Is.Not.EqualTo(before));
            Assert.That(after, Does.StartWith(before.Substring(0, before.LastIndexOf(':'))));
        }

        [Test]
        public void BlockedCause_IsStructuredAndDistinguishableFromFailures()
        {
            var compiling = EditorOperationRegistry.Create(
                "tests-run", "waiting-for-compilation", TestMetadata());
            var compilingBlocked = compiling.Blocked;
            Assert.That(compilingBlocked, Is.Not.Null);
            Assert.That(compilingBlocked!.Cause, Is.EqualTo("compilation"));
            Assert.That(compilingBlocked.RetryAfterMs, Is.GreaterThan(0));
            Assert.That(compiling.Status, Is.Not.EqualTo("failed"));

            var settling = EditorOperationRegistry.Create(
                "tests-run", "waiting-for-previous-test-run", TestMetadata());
            Assert.That(settling.Blocked!.Cause, Is.EqualTo("previous-run-settling"));

            var queued = EditorOperationRegistry.Create(
                "tests-run", "queued", TestMetadata());
            Assert.That(queued.Blocked!.Cause, Is.EqualTo("capacity-admission"));

            var cancelling = EditorOperationRegistry.Create(
                "tests-run", "cancellation-pending", TestMetadata());
            Assert.That(cancelling.Blocked!.Cause, Is.EqualTo("cancellation-pending"));

            // Freely executing operations are not blocked…
            var executing = EditorOperationRegistry.Create(
                "tests-run", "executing", TestMetadata());
            Assert.That(executing.Blocked, Is.Null);
            // …and terminal records never report a blocked cause.
            EditorOperationRegistry.Fail(executing.OperationId,
                "test-execution-failed", "boom", "failed");
            Assert.That(EditorOperationRegistry.Get(executing.OperationId)!.Blocked, Is.Null);
        }

        [Test]
        public void Provenance_SurvivesStoreRoundTrips()
        {
            var operation = EditorOperationRegistry.Create(
                "tests-run", "queued", TestMetadata(),
                sourceRevision: "compile:abc123:7");
            EditorOperationRegistry.Start(operation.OperationId, "executing");
            EditorOperationRegistry.Succeed(operation.OperationId, "{\"summary\":{}}");

            // Reload from disk (as a domain reload would).
            EditorOperationRegistry.ConfigureStoreForTests(_operationStore);
            var reloaded = EditorOperationRegistry.Get(operation.OperationId)!;
            Assert.That(reloaded.SourceRevision, Is.EqualTo("compile:abc123:7"));
            Assert.That(reloaded.StartedAtUtc, Is.Not.Null.And.Not.Empty);
            Assert.That(reloaded.Execution, Is.EqualTo("fresh"));
        }
    }
}
