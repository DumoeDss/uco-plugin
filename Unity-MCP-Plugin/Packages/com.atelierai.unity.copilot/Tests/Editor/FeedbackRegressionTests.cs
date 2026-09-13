#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using com.AtelierAI.Unity.Copilot.Editor.API;
using com.AtelierAI.Unity.Copilot.Editor.API.TestRunner;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.IvanMurzak.McpPlugin.Common.Model;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools;
using TestMode = UnityEditor.TestTools.TestRunner.Api.TestMode;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    public sealed class FeedbackRegressionTests
    {
        string _temporaryDirectory = string.Empty;
        string _operationStore = string.Empty;
        string? _toolGroupPrefs;

        [SetUp]
        public void SetUp()
        {
            _temporaryDirectory = Path.Combine(Path.GetTempPath(),
                "unity-copilot-feedback-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
            _operationStore = Path.Combine(_temporaryDirectory, "operations.json");
            EditorOperationRegistry.ConfigureStoreForTests(_operationStore);
            _toolGroupPrefs = EditorPrefs.HasKey(ToolGroupRegistry.EditorPrefsKey)
                ? EditorPrefs.GetString(ToolGroupRegistry.EditorPrefsKey)
                : null;
        }

        [TearDown]
        public void TearDown()
        {
            EditorOperationRegistry.ConfigureStoreForTests(null);
            if (_toolGroupPrefs == null)
                EditorPrefs.DeleteKey(ToolGroupRegistry.EditorPrefsKey);
            else
                EditorPrefs.SetString(ToolGroupRegistry.EditorPrefsKey, _toolGroupPrefs);
            ToolGroupRegistry.Rebuild();
            if (Directory.Exists(_temporaryDirectory))
                Directory.Delete(_temporaryDirectory, true);
        }

        [Test]
        public void ProcessGuard_RejectsAssetImportWorkersAndAllowsTheMainEditor()
        {
            Assert.That(UnityProcessGuard.ShouldInitialize(isAssetImportWorkerProcess: false), Is.True);
            Assert.That(UnityProcessGuard.ShouldInitialize(isAssetImportWorkerProcess: true), Is.False);
        }

        [Test]
        public void OperationRegistry_PersistsMonotonicTerminalStateAndCancellation()
        {
            var operation = EditorOperationRegistry.Create("fixture", "queued", "{\"safe\":true}");
            EditorOperationRegistry.Start(operation.OperationId, "running");
            EditorOperationRegistry.Update(operation.OperationId, "halfway", 0.5);
            EditorOperationRegistry.Succeed(operation.OperationId, "{\"done\":true}");

            EditorOperationRegistry.ConfigureStoreForTests(_operationStore);
            var restored = EditorOperationRegistry.Get(operation.OperationId);
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored!.Status, Is.EqualTo("succeeded"));
            Assert.That(restored.Progress, Is.EqualTo(1));
            Assert.That(restored.CompletedAtUtc, Is.Not.Null.And.Not.Empty);
            Assert.Throws<InvalidOperationException>(() =>
                EditorOperationRegistry.Fail(operation.OperationId, "late", "late failure"));

            var queued = EditorOperationRegistry.Create("fixture", "queued");
            var cancelled = EditorOperationRegistry.RequestCancellation(queued.OperationId);
            Assert.That(cancelled.Status, Is.EqualTo("cancelled"));
            Assert.That(cancelled.Phase, Is.EqualTo("cancelled-before-start"));
            Assert.That(Directory.GetFiles(_temporaryDirectory, "*.tmp-*"), Is.Empty,
                "Atomic writes must not leave temporary operation stores behind.");
        }

        [Test]
        public void OperationRegistry_ReconcilesLostOwnerAndBoundsTerminalRetention()
        {
            var old = new EditorOperationInfo
            {
                OperationId = "old-owner",
                Kind = "fixture",
                Status = "running",
                Phase = "work",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O"),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1).ToString("O"),
                EditorPid = -1,
                DomainGeneration = "obsolete"
            };
            var records = new List<EditorOperationInfo> { old };
            for (var i = 0; i < EditorOperationRegistry.MaxRetainedOperations + 5; i++)
            {
                records.Add(new EditorOperationInfo
                {
                    OperationId = "terminal-" + i,
                    Kind = "fixture",
                    Status = "succeeded",
                    Phase = "completed",
                    CreatedAtUtc = DateTime.UtcNow.AddSeconds(-1000 + i).ToString("O"),
                    UpdatedAtUtc = DateTime.UtcNow.AddSeconds(-1000 + i).ToString("O"),
                    CompletedAtUtc = DateTime.UtcNow.AddSeconds(-1000 + i).ToString("O")
                });
            }
            File.WriteAllText(_operationStore, JsonUtility.ToJson(new EditorOperationStore
            {
                SchemaVersion = EditorOperationRegistry.SchemaVersion,
                Operations = records
            }, true));

            EditorOperationRegistry.ConfigureStoreForTests(_operationStore);
            EditorOperationRegistry.ReconcileActiveOperations();
            EditorOperationRegistry.Create("fixture", "queued");

            var reconciled = EditorOperationRegistry.Get("old-owner");
            Assert.That(reconciled!.Status, Is.EqualTo("interrupted"));
            Assert.That(reconciled.ErrorCode, Is.EqualTo("operation_owner_missing"));
            Assert.That(EditorOperationRegistry.List().Length,
                Is.LessThanOrEqualTo(EditorOperationRegistry.MaxRetainedOperations));
        }

        [Test]
        public void BuildJobCompatibility_ProjectsSuccessFailureAndPendingCancellation()
        {
            var succeeded = Tool_Build.BuildJobRegistry.Create("StandaloneWindows64", "Build/fixture.exe");
            Tool_Build.BuildJobRegistry.Start(succeeded.JobId);
            Tool_Build.BuildJobRegistry.Complete(succeeded.JobId, 1.25, 42, 0, 1);
            var succeededResult = Tool_Build.BuildJobRegistry.Get(succeeded.JobId)!;
            Assert.That(succeededResult.Status, Is.EqualTo("succeeded"));
            Assert.That(succeededResult.TotalSizeBytes, Is.EqualTo(42));

            var failed = Tool_Build.BuildJobRegistry.Create("StandaloneWindows64", "Build/fail.exe");
            Tool_Build.BuildJobRegistry.Start(failed.JobId);
            Tool_Build.BuildJobRegistry.Fail(failed.JobId, "fixture failure");
            Assert.That(Tool_Build.BuildJobRegistry.Get(failed.JobId)!.Status, Is.EqualTo("failed"));

            var cancelled = Tool_Build.BuildJobRegistry.Create("StandaloneWindows64", "Build/cancel.exe");
            Tool_Build.BuildJobRegistry.Start(cancelled.JobId);
            var pending = EditorOperationRegistry.RequestCancellation(cancelled.JobId);
            Assert.That(pending.CancellationPending, Is.True);
            Tool_Build.BuildJobRegistry.CancelAfterBuildReturns(cancelled.JobId, 0.5, 0, 0, 0);
            Assert.That(Tool_Build.BuildJobRegistry.Get(cancelled.JobId)!.Status, Is.EqualTo("cancelled"));
        }

        [Test]
        public void TestJobAliases_QueryAndCancelTheSharedOperation()
        {
            var operation = EditorOperationRegistry.Create("tests-run", "queued");
            Assert.That(Tool_Tests.GetJob(operation.OperationId).OperationId,
                Is.EqualTo(operation.OperationId));
            Assert.That(Tool_Tests.ListJobs().Any(item => item.OperationId == operation.OperationId),
                Is.True);

            var cancelled = Tool_Tests.CancelJob(operation.OperationId);
            Assert.That(cancelled.Status, Is.EqualTo("cancelled"));
            Assert.That(cancelled.CancellationRequested, Is.True);
        }

        [TestCase("missing-api")]
        [TestCase("cancel-throws")]
        [TestCase("no-callback")]
        public void TestExecutionTimeout_TerminalizesAfterBoundedCancellationGrace(string cancellationMode)
        {
            object runner = cancellationMode switch
            {
                "missing-api" => new MissingCancelApi(),
                "cancel-throws" => new ThrowingCancelApi(),
                _ => new SilentCancelApi()
            };
            var operation = CreateExpiredRunningTestOperation();
            var afterTimeout = ParseUtc(operation.CreatedAtUtc).AddSeconds(2);

            TestOperationTimeoutMonitor.ProcessOperation(operation, afterTimeout,
                operationId => Tool_Tests.RequestCancellation(operationId, runner));

            var pending = EditorOperationRegistry.Get(operation.OperationId)!;
            Assert.That(pending.Status, Is.EqualTo("running"));
            Assert.That(pending.CancellationRequested, Is.True);
            Assert.That(pending.CancellationRequestedAtUtc, Is.Not.Null.And.Not.Empty);
            Assert.That(pending.CancellationPending, Is.True);
            Assert.That(pending.Phase, Does.StartWith(
                TestOperationTimeoutMonitor.TimeoutCancellationPendingPhase));
            if (cancellationMode == "missing-api")
                Assert.That(pending.Phase, Does.Contain("does not expose CancelTestRun"));
            if (cancellationMode == "cancel-throws")
                Assert.That(pending.Phase, Does.Contain("fixture cancellation failure"));
            if (runner is SilentCancelApi silent)
                Assert.That(silent.CallCount, Is.EqualTo(1));

            if (runner is SilentCancelApi)
            {
                EditorOperationRegistry.Update(operation.OperationId, "executing", 0.5);
                pending = EditorOperationRegistry.Get(operation.OperationId)!;
                Assert.That(pending.Phase, Is.EqualTo("executing"),
                    "Simulate a late progress callback overwriting the timeout phase.");
            }

            var afterGrace = ParseUtc(pending.CancellationRequestedAtUtc!)
                .AddSeconds(Tool_Tests.TimeoutCancellationGraceSeconds + 1);
            TestOperationTimeoutMonitor.ProcessOperation(pending, afterGrace,
                operationId => Tool_Tests.RequestCancellation(operationId, runner));

            var terminal = EditorOperationRegistry.Get(operation.OperationId)!;
            Assert.That(terminal.Status, Is.EqualTo("failed"));
            Assert.That(terminal.Phase, Is.EqualTo("execution-timeout"));
            Assert.That(terminal.ErrorCode, Is.EqualTo("tests-execution-timeout"));
            if (runner is SilentCancelApi completedSilent)
                Assert.That(completedSilent.CallCount, Is.EqualTo(1),
                    "The first timeout request must be durable and not replayed on each poll.");
        }

        [Test]
        public void TestExecutionTimeout_ClearsOwnedSessionAndIgnoresLateCompletionCallback()
        {
            var operation = CreateExpiredRunningTestOperation();
            var originalOperationId = Tool_Tests.CurrentTestOperationId;
            var originalRequestId = Tool_Tests.CurrentTestRequestId;
            var runner = new SilentCancelApi();
            try
            {
                Tool_Tests.CurrentTestOperationId = operation.OperationId;
                Tool_Tests.CurrentTestRequestId = "timeout-fixture-request";
                TestOperationTimeoutMonitor.ProcessOperation(operation,
                    ParseUtc(operation.CreatedAtUtc).AddSeconds(2),
                    operationId => Tool_Tests.RequestCancellation(operationId, runner));
                var pending = EditorOperationRegistry.Get(operation.OperationId)!;
                TestOperationTimeoutMonitor.ProcessOperation(pending,
                    ParseUtc(pending.CancellationRequestedAtUtc!)
                        .AddSeconds(Tool_Tests.TimeoutCancellationGraceSeconds + 1),
                    operationId => Tool_Tests.RequestCancellation(operationId, runner));

                var timedOut = EditorOperationRegistry.Get(operation.OperationId)!;
                Assert.That(Tool_Tests.CurrentTestOperationId, Is.Empty);
                Assert.That(Tool_Tests.CurrentTestRequestId, Is.Empty);
                TestResultCollector.CompleteOperationFromCallback(operation.OperationId,
                    failedTests: 0, executedTests: 1, resultJson: "{\"late\":true}");
                var afterLateCallback = EditorOperationRegistry.Get(operation.OperationId)!;
                Assert.That(afterLateCallback.Status, Is.EqualTo("failed"));
                Assert.That(afterLateCallback.ErrorCode, Is.EqualTo("tests-execution-timeout"));
                Assert.That(afterLateCallback.CompletedAtUtc, Is.EqualTo(timedOut.CompletedAtUtc));
            }
            finally
            {
                Tool_Tests.CurrentTestOperationId = originalOperationId;
                Tool_Tests.CurrentTestRequestId = originalRequestId;
            }
        }

        [Test]
        public void TestCollectorCallbacks_TimedOutRunALateCallbacksCannotMutateStartedRunB()
        {
            var operationA = CreateExpiredRunningTestOperation();
            var originalOperationId = Tool_Tests.CurrentTestOperationId;
            var originalRequestId = Tool_Tests.CurrentTestRequestId;
            var originalDiscoveredTests = TestResultCollector.ExpectedDiscoveredTestCount;
            var originalMatchedTests = TestResultCollector.ExpectedMatchedTestCount;
            var collector = TestResultCollector.CreateIsolatedForTests();
            var testA = new FakeTestAdaptor("Fixture.OperationA.Test", isSuite: false);
            var runA = new FakeTestAdaptor("Fixture.OperationA", isSuite: true, testA);
            var finishedTestA = new FakeTestResultAdaptor(testA, TestStatus.Passed);
            var finishedRunA = new FakeTestResultAdaptor(runA, TestStatus.Passed);
            var runner = new SilentCancelApi();
            var callbackRunStarted = false;
            var callbackRunFinished = false;
            try
            {
                Tool_Tests.CurrentTestOperationId = operationA.OperationId;
                Tool_Tests.CurrentTestRequestId = "operation-a-request";
                TestResultCollector.ExpectedDiscoveredTestCount = 1;
                TestResultCollector.ExpectedMatchedTestCount = 1;
                collector.RunStarted(runA);
                callbackRunStarted = true;

                TestOperationTimeoutMonitor.ProcessOperation(operationA,
                    ParseUtc(operationA.CreatedAtUtc).AddSeconds(2),
                    operationId => Tool_Tests.RequestCancellation(operationId, runner));
                var pendingA = EditorOperationRegistry.Get(operationA.OperationId)!;
                TestOperationTimeoutMonitor.ProcessOperation(pendingA,
                    ParseUtc(pendingA.CancellationRequestedAtUtc!)
                        .AddSeconds(Tool_Tests.TimeoutCancellationGraceSeconds + 1),
                    operationId => Tool_Tests.RequestCancellation(operationId, runner));
                var terminalA = EditorOperationRegistry.Get(operationA.OperationId)!;
                Assert.That(terminalA.Status, Is.EqualTo("failed"));
                Assert.That(terminalA.ErrorCode, Is.EqualTo("tests-execution-timeout"));

                var operationB = EditorOperationRegistry.Create("tests-run", "scheduled");
                EditorOperationRegistry.Start(operationB.OperationId, "executing-b");
                Tool_Tests.CurrentTestOperationId = operationB.OperationId;
                Tool_Tests.CurrentTestRequestId = "operation-b-request";
                TestResultCollector.ExpectedDiscoveredTestCount = 7;
                TestResultCollector.ExpectedMatchedTestCount = 9;
                var beforeB = EditorOperationRegistry.Get(operationB.OperationId)!;

                collector.TestFinished(finishedTestA);
                collector.RunFinished(finishedRunA);
                callbackRunFinished = true;

                Assert.That(collector.GetResults(), Has.Count.EqualTo(1),
                    "The real TestFinished callback must have processed A's late result.");
                var afterA = EditorOperationRegistry.Get(operationA.OperationId)!;
                Assert.That(afterA.Status, Is.EqualTo("failed"));
                Assert.That(afterA.ErrorCode, Is.EqualTo("tests-execution-timeout"));
                Assert.That(afterA.CompletedAtUtc, Is.EqualTo(terminalA.CompletedAtUtc));

                var afterB = EditorOperationRegistry.Get(operationB.OperationId)!;
                Assert.That(afterB.Status, Is.EqualTo("running"));
                Assert.That(afterB.Phase, Is.EqualTo(beforeB.Phase));
                Assert.That(afterB.Progress, Is.EqualTo(beforeB.Progress));
                Assert.That(afterB.ResultJson, Is.Null);
                Assert.That(Tool_Tests.CurrentTestOperationId, Is.EqualTo(operationB.OperationId));
                Assert.That(Tool_Tests.CurrentTestRequestId, Is.EqualTo("operation-b-request"));
                Assert.That(TestResultCollector.ExpectedDiscoveredTestCount, Is.EqualTo(7));
                Assert.That(TestResultCollector.ExpectedMatchedTestCount, Is.EqualTo(9));
            }
            finally
            {
                if (callbackRunStarted && !callbackRunFinished)
                    collector.RunFinished(finishedRunA);
                Tool_Tests.CurrentTestOperationId = originalOperationId;
                Tool_Tests.CurrentTestRequestId = originalRequestId;
                TestResultCollector.ExpectedDiscoveredTestCount = originalDiscoveredTests;
                TestResultCollector.ExpectedMatchedTestCount = originalMatchedTests;
            }
        }

        [Test]
        public void PendingTestRun_CancelledWhileQueuedShortCircuitsReloadResume()
        {
            var operation = EditorOperationRegistry.Create("tests-run", "waiting-for-compilation");
            var originalOperationId = Tool_Tests.CurrentTestOperationId;
            var originalRequestId = Tool_Tests.CurrentTestRequestId;
            try
            {
                Tool_Tests.CurrentTestOperationId = operation.OperationId;
                Tool_Tests.CurrentTestRequestId = "reload-cancel-fixture";
                Tool_Tests.SavePendingTestRun(operation.OperationId, TestMode.EditMode,
                    null, null, null, null);
                EditorOperationRegistry.RequestCancellation(operation.OperationId);

                Assert.That(Tool_Tests.TryClaimPendingTestRun(out var resumedOperationId), Is.False);
                Assert.That(resumedOperationId, Is.EqualTo(operation.OperationId));
                Assert.That(Tool_Tests.HasPendingTestRun(), Is.False);
                Assert.That(EditorOperationRegistry.Get(operation.OperationId)!.Status,
                    Is.EqualTo("cancelled"));
                Assert.That(Tool_Tests.CurrentTestOperationId, Is.Empty);
                Assert.That(Tool_Tests.CurrentTestRequestId, Is.Empty);
            }
            finally
            {
                Tool_Tests.ClearPendingTestRun();
                Tool_Tests.CurrentTestOperationId = originalOperationId;
                Tool_Tests.CurrentTestRequestId = originalRequestId;
            }
        }

        [Test]
        public void RefreshAndMenuOperations_ReachDeterministicFailureOrCancellationWithoutReplay()
        {
            var refresh = EditorOperationRegistry.Create("assets-refresh", "scheduled");
            EditorOperationRegistry.RequestCancellation(refresh.OperationId);
            Tool_Assets.RunRefresh(refresh.OperationId, ImportAssetOptions.Default);
            Assert.That(EditorOperationRegistry.Get(refresh.OperationId)!.Status, Is.EqualTo("cancelled"));

            var missingMenu = EditorOperationRegistry.Create("editor-execute-menu-item", "scheduled");
            LogAssert.Expect(LogType.Error,
                "ExecuteMenuItem failed because there is no menu named 'Window/UnityCopilot/Definitely Missing Feedback Fixture'");
            Tool_Editor.RunMenuItem(missingMenu.OperationId,
                "Window/UnityCopilot/Definitely Missing Feedback Fixture");
            Assert.That(EditorOperationRegistry.Get(missingMenu.OperationId)!.Status, Is.EqualTo("failed"));

            var cancelledMenu = EditorOperationRegistry.Create("editor-execute-menu-item", "scheduled");
            EditorOperationRegistry.RequestCancellation(cancelledMenu.OperationId);
            Tool_Editor.RunMenuItem(cancelledMenu.OperationId, "File/Save");
            Assert.That(EditorOperationRegistry.Get(cancelledMenu.OperationId)!.Status, Is.EqualTo("cancelled"));
        }

        [Test]
        public void MenuOperation_CancellationDuringHandlerWinsAndRetainsHandlerResult()
        {
            var operation = EditorOperationRegistry.Create("editor-execute-menu-item", "scheduled");
            EditorOperationRegistry.Start(operation.OperationId,
                "executing-non-interruptible-menu-handler");
            Tool_Editor.RunMenuItem(operation.OperationId, "Fixture/Long Handler", _ =>
            {
                EditorOperationRegistry.RequestCancellation(operation.OperationId);
                return true;
            });

            var cancelled = EditorOperationRegistry.Get(operation.OperationId)!;
            Assert.That(cancelled.Status, Is.EqualTo("cancelled"));
            Assert.That(cancelled.Phase,
                Is.EqualTo("cancelled-after-non-interruptible-menu-handler"));
            Assert.That(cancelled.ResultJson, Does.Contain("\"Ok\":true"));
            Assert.That(cancelled.ResultJson, Does.Contain("Fixture/Long Handler"));
        }

        [Test]
        public void ToolGroups_ResolveEveryAliasAndReportRequestedVersusEffectiveState()
        {
            EditorPrefs.DeleteKey(ToolGroupRegistry.EditorPrefsKey);
            ToolGroupRegistry.Rebuild();
            var groups = ToolGroupRegistry.AllGroups();
            Assert.That(groups.Count, Is.GreaterThan(10));
            Assert.That(groups.Sum(group => ToolGroupRegistry.ToolsInGroup(group).Count), Is.GreaterThan(150));

            foreach (var canonical in groups)
            {
                Assert.That(ToolGroupRegistry.ResolveCanonicalGroup(canonical), Is.EqualTo(canonical));
                foreach (var alias in ToolGroupRegistry.AliasesForGroup(canonical))
                    Assert.That(ToolGroupRegistry.ResolveCanonicalGroup(alias), Is.EqualTo(canonical));
            }

            ToolGroupRegistry.SetGroupEnabled("application", false);
            var editor = new Tool_ToolGroups().ListGroups().Single(group => group.Name == "editor");
            Assert.That(editor.RequestedEnabled, Is.False);
            Assert.That(editor.EffectiveEnabled, Is.True,
                "Soft group preferences must not be misreported as request-boundary enforcement.");
            Assert.That(editor.ToolCount, Is.EqualTo(editor.Tools.Length));
            CollectionAssert.Contains(editor.Aliases, "application");
        }

        [Test]
        public void TestFilters_UseExactMetadataAndLogicalAndIncludingDefaultNamespace()
        {
            var defaultNamespace = new Tool_Tests.DiscoveredTest
            {
                Assembly = "Feedback.Tests",
                Namespace = string.Empty,
                ClassFullName = "DefaultFixture",
                ClassSimpleName = "DefaultFixture",
                MethodName = "Works",
                FullName = "DefaultFixture.Works"
            };
            Assert.That(Tool_Tests.MatchesAll(defaultNamespace, new TestFilterParameters(
                "Feedback.Tests.dll", string.Empty, "DefaultFixture", "Works")), Is.True);

            var namespaced = new Tool_Tests.DiscoveredTest
            {
                Assembly = "Feedback.Tests",
                Namespace = "Feedback.Namespace",
                ClassFullName = "Feedback.Namespace.FilterFixture",
                ClassSimpleName = "FilterFixture",
                MethodName = "Matches",
                FullName = "Feedback.Namespace.FilterFixture.Matches"
            };
            Assert.That(Tool_Tests.MatchesAll(namespaced, new TestFilterParameters(
                "Feedback.Tests", "Feedback.Namespace", "FilterFixture", "Matches")), Is.True);
            Assert.That(Tool_Tests.MatchesAll(namespaced, new TestFilterParameters(
                "Feedback.Tests", "Wrong.Namespace", "FilterFixture", "Matches")), Is.False);
        }

        [Test]
        public void ScreenshotMetadata_WritesBoundedFileIdentityAndRejectsEscapingPaths()
        {
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var relative = "Temp/feedback-screenshot.png";
            var absolute = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(UnityEngine.Application.dataPath)!, relative));
            try
            {
                var inline = Tool_Screenshot.BuildScreenshotResponse(bytes, 2, 3,
                    "fixture", null, false);
                Assert.That(inline.Status, Is.Not.EqualTo(ResponseStatus.Error));

                var metadataOnly = Tool_Screenshot.BuildScreenshotResponse(bytes, 2, 3,
                    "fixture", null, true);
                Assert.That(metadataOnly.Status, Is.Not.EqualTo(ResponseStatus.Error));
                Assert.That(metadataOnly.StructuredContent!.ToJsonString().Length, Is.LessThan(2048));

                var response = Tool_Screenshot.BuildScreenshotResponse(bytes, 2, 3, "fixture", relative, false);
                if (Path.DirectorySeparatorChar == '\\')
                {
                    Assert.That(response.Status, Is.Not.EqualTo(ResponseStatus.Error));
                    var json = response.StructuredContent!.ToJsonString();
                    Assert.That(json.Length, Is.LessThan(2048));
                    Assert.That(json, Does.Not.Contain("base64"));
                    Assert.That(File.ReadAllBytes(absolute), Is.EqualTo(bytes));
                    Assert.That(json, Does.Contain("74f81fe167d99b4cb41d6d0ccda82278caee9f3e2f25d5e5a3936ff3dcec60d0"));
                }
                else
                {
                    Assert.That(response.Status, Is.EqualTo(ResponseStatus.Error));
                    Assert.That(File.Exists(absolute), Is.False);
                }

                var escaped = Tool_Screenshot.BuildScreenshotResponse(bytes, 2, 3, "fixture", "../escape.png", false);
                Assert.That(escaped.Status, Is.EqualTo(ResponseStatus.Error));
            }
            finally
            {
                if (File.Exists(absolute)) File.Delete(absolute);
            }
        }

        [Test]
        public void ScreenshotContainment_RejectsDifferentVolumeSiblingTraversalAndReparsePointsOnWindows()
        {
            if (Path.DirectorySeparatorChar != '\\')
                Assert.Ignore("Windows path-containment regression.");

            const string projectRoot = @"C:\Fixture\Project";
            Assert.That(Tool_Screenshot.TryResolveContainedOutputPath(projectRoot,
                @"D:\outside.png", out _, out _), Is.False, "different volume");
            Assert.That(Tool_Screenshot.TryResolveContainedOutputPath(projectRoot,
                @"C:\Fixture\ProjectSibling\outside.png", out _, out _), Is.False,
                "separator-bounded sibling prefix");
            Assert.That(Tool_Screenshot.TryResolveContainedOutputPath(projectRoot,
                @"..\outside.png", out _, out _), Is.False, "parent traversal");

            var junction = Path.Combine(projectRoot, "junction");
            Assert.That(Tool_Screenshot.TryResolveContainedOutputPath(projectRoot,
                @"junction\outside.png",
                path => string.Equals(path, junction, StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Directory | FileAttributes.ReparsePoint
                    : (FileAttributes?)null,
                out _, out var reparseError), Is.False, "junction/reparse escape");
            Assert.That(reparseError, Does.Contain("reparse point"));
        }

        [Test]
        public void ScreenshotContainment_DoesNotCreateDirectoriesBeforeValidation()
        {
            var outsideDirectory = Path.Combine(_temporaryDirectory, "must-not-create");
            var response = Tool_Screenshot.BuildScreenshotResponse(new byte[] { 1, 2, 3 },
                1, 1, "fixture", Path.Combine(outsideDirectory, "escape.png"), false);

            Assert.That(response.Status, Is.EqualTo(ResponseStatus.Error));
            Assert.That(Directory.Exists(outsideDirectory), Is.False);
        }

        [Test]
        public void ScreenshotContainment_BlocksAdversarialJunctionSwapDuringWriteOnWindows()
        {
            if (Path.DirectorySeparatorChar != '\\')
                Assert.Ignore("Windows junction-swap regression.");

            var projectRoot = Path.Combine(_temporaryDirectory, "project");
            var outputDirectory = Path.Combine(projectRoot, "captures");
            var outsideDirectory = Path.Combine(_temporaryDirectory, "outside");
            var stagedJunction = Path.Combine(_temporaryDirectory, "staged-junction");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(outsideDirectory);
            Assert.That(TryCreateDirectoryJunction(stagedJunction, outsideDirectory,
                    out var junctionDiagnostic), Is.True, junctionDiagnostic);

            var deleteBlocked = false;
            var replacementMoved = false;
            try
            {
                var wrote = Tool_Screenshot.TryWriteContainedOutputFile(projectRoot,
                    Path.Combine("captures", "swap.png"), new byte[] { 7, 8, 9 }, () =>
                    {
                        try
                        {
                            Directory.Delete(outputDirectory);
                        }
                        catch (IOException)
                        {
                            deleteBlocked = true;
                            return;
                        }
                        catch (UnauthorizedAccessException)
                        {
                            deleteBlocked = true;
                            return;
                        }

                        Directory.Move(stagedJunction, outputDirectory);
                        replacementMoved = true;
                    }, out var resolvedPath, out var error);

                Assert.That(deleteBlocked || replacementMoved, Is.True,
                    "The adversarial swap hook must either be blocked or install its real junction.");
                if (replacementMoved)
                {
                    Assert.That(wrote, Is.False,
                        "A successful directory-name swap must make the handle-relative write fail closed.");
                    Assert.That(error, Does.Contain("securely written"));
                }
                else
                {
                    Assert.That(wrote, Is.True, error);
                    Assert.That(File.ReadAllBytes(resolvedPath), Is.EqualTo(new byte[] { 7, 8, 9 }));
                }
                Assert.That(File.Exists(Path.Combine(outsideDirectory, "swap.png")), Is.False,
                    "The adversarial junction target must remain untouched.");
                var junctionFixture = replacementMoved ? outputDirectory : stagedJunction;
                Assert.That((File.GetAttributes(junctionFixture) & FileAttributes.ReparsePoint) != 0,
                    Is.True, "The swap fixture must be a real Windows reparse point.");
            }
            finally
            {
                if (Directory.Exists(stagedJunction))
                    Directory.Delete(stagedJunction);
                if (Directory.Exists(outputDirectory)
                    && (File.GetAttributes(outputDirectory) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(outputDirectory);
            }
        }

        [Test]
        public void ScreenshotContainment_FailsClosedDuringDirectorySymlinkSwapOnUnix()
        {
            if (Path.DirectorySeparatorChar == '\\')
                Assert.Ignore("Unix symbolic-link swap regression.");

            var projectRoot = Path.Combine(_temporaryDirectory, "project-unix");
            var outputDirectory = Path.Combine(projectRoot, "captures");
            var outsideDirectory = Path.Combine(_temporaryDirectory, "outside-unix");
            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(outsideDirectory);

            var hookRan = false;
            var symlinkCreated = false;
            try
            {
                var wrote = Tool_Screenshot.TryWriteContainedOutputFile(projectRoot,
                    Path.Combine("captures", "swap.png"), new byte[] { 7, 8, 9 }, () =>
                    {
                        hookRan = true;
                        Directory.Delete(outputDirectory);
                        symlinkCreated = TryCreateDirectorySymbolicLink(outputDirectory,
                            outsideDirectory, out _);
                    }, out _, out var error);

                Assert.That(hookRan, Is.True,
                    "The fixture must swap the checked directory at the pre-write seam.");
                Assert.That(symlinkCreated, Is.True,
                    "The fixture must install a real directory symbolic link.");
                Assert.That((File.GetAttributes(outputDirectory) & FileAttributes.ReparsePoint) != 0,
                    Is.True, "The swap fixture must be a real Unix symbolic link.");
                Assert.That(wrote, Is.False);
                Assert.That(error, Does.Contain("unavailable on this platform"));
                Assert.That(File.Exists(Path.Combine(outsideDirectory, "swap.png")), Is.False,
                    "Fail-closed output must leave the external symlink target untouched.");
            }
            finally
            {
                if (symlinkCreated && Directory.Exists(outputDirectory))
                    Directory.Delete(outputDirectory);
            }
        }

        [Test]
        public void ScreenshotQueue_RecordsSuccessFailureAndPreStartCancellation()
        {
            var success = Tool_Screenshot.QueueScreenshot("screenshot-camera",
                () => ResponseCallTool.Success("captured"));
            var failure = Tool_Screenshot.QueueScreenshot("screenshot-camera",
                () => ResponseCallTool.Error("failed"));
            var cancelled = Tool_Screenshot.QueueScreenshot("screenshot-camera",
                () => ResponseCallTool.Success("must not run"));

            var successInfo = JsonUtility.FromJson<EditorOperationInfo>(success.StructuredContent!["result"]!.ToJsonString());
            var failureInfo = JsonUtility.FromJson<EditorOperationInfo>(failure.StructuredContent!["result"]!.ToJsonString());
            var cancelledInfo = JsonUtility.FromJson<EditorOperationInfo>(cancelled.StructuredContent!["result"]!.ToJsonString());
            Assert.That(successInfo.OperationId, Is.Not.Empty, "Queued screenshot must return a durable handle.");
            EditorOperationRegistry.RequestCancellation(cancelledInfo.OperationId);

            Tool_Screenshot.RunQueuedScreenshot(successInfo.OperationId,
                () => ResponseCallTool.Success("captured"));
            Tool_Screenshot.RunQueuedScreenshot(failureInfo.OperationId,
                () => ResponseCallTool.Error("failed"));
            Tool_Screenshot.RunQueuedScreenshot(cancelledInfo.OperationId,
                () => ResponseCallTool.Success("must not run"));

            Assert.That(EditorOperationRegistry.Get(successInfo.OperationId)!.Status, Is.EqualTo("succeeded"));
            Assert.That(EditorOperationRegistry.Get(failureInfo.OperationId)!.Status, Is.EqualTo("failed"));
            Assert.That(EditorOperationRegistry.Get(cancelledInfo.OperationId)!.Status, Is.EqualTo("cancelled"));
        }

        [Test]
        public void ConsoleStorage_ClearSucceedsWhileAnotherWriterKeepsTheFileOpen()
        {
            var storage = new FileLogStorage(directoryPath: _temporaryDirectory,
                requestedFileName: "concurrent.log");
            var path = Path.Combine(_temporaryDirectory, "concurrent.log");
            try
            {
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    writer.WriteByte(42);
                    writer.Flush();
                    var result = storage.Clear();
                    Assert.That(result.Ok, Is.True, result.Error);
                    CollectionAssert.Contains(new[]
                    {
                        "truncate-and-reopen", "rotate-and-reopen", "switch-to-new-file"
                    }, result.Strategy);
                }
            }
            finally
            {
                storage.Dispose();
            }
        }

        [Test]
        public void EditorClose_RefusesDirtySceneWithBoundedBlockers()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.MarkSceneDirty(scene);
            try
            {
                var result = new Tool_Editor().RequestApplicationClose();
                Assert.That(result.Ok, Is.False);
                Assert.That(result.Accepted, Is.False);
                Assert.That(result.Blockers, Is.Not.Empty);
                Assert.That(result.Blockers.Length, Is.LessThan(32));
            }
            finally
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        [Test]
        public void ScriptClassSelection_PrefersExactThenUniqueAndRejectsMissingOrAmbiguousNames()
        {
            var assembly = typeof(FeedbackRegressionTests).Assembly;
            var exact = Tool_Script.ResolveCompiledType(assembly,
                typeof(global::FeedbackClassNameA.DuplicateClassNameFixture).FullName!, out var exactError);
            Assert.That(exact, Is.EqualTo(typeof(global::FeedbackClassNameA.DuplicateClassNameFixture)));
            Assert.That(exactError, Is.Null);

            var unique = Tool_Script.ResolveCompiledType(assembly,
                nameof(FeedbackUniqueClassNameFixture), out var uniqueError);
            Assert.That(unique, Is.EqualTo(typeof(FeedbackUniqueClassNameFixture)));
            Assert.That(uniqueError, Is.Null);

            var missing = Tool_Script.ResolveCompiledType(assembly,
                "DefinitelyMissingFeedbackClass", out var missingError);
            Assert.That(missing, Is.Null);
            Assert.That(missingError, Does.Contain("not found"));

            var ambiguous = Tool_Script.ResolveCompiledType(assembly,
                "DuplicateClassNameFixture", out var ambiguousError);
            Assert.That(ambiguous, Is.Null);
            Assert.That(ambiguousError, Does.Contain("ambiguous"));
            Assert.That(ambiguousError, Does.Contain(
                typeof(global::FeedbackClassNameA.DuplicateClassNameFixture).FullName!));
            Assert.That(ambiguousError, Does.Contain(
                typeof(global::FeedbackClassNameB.DuplicateClassNameFixture).FullName!));
        }


        EditorOperationInfo CreateExpiredRunningTestOperation()
        {
            var metadata = JsonUtility.ToJson(new Tool_Tests.TestOperationMetadata
            {
                ExecutionTimeoutSeconds = 1,
                WaitTimeoutSeconds = 1
            });
            var operation = EditorOperationRegistry.Create("tests-run", "scheduled", metadata);
            return EditorOperationRegistry.Start(operation.OperationId, "executing");
        }

        static DateTime ParseUtc(string value)
            => DateTime.Parse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind).ToUniversalTime();

        static bool TryCreateDirectoryJunction(string junctionPath, string targetPath, out string diagnostic)
        {
            var command = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                Arguments = $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                if (process == null)
                {
                    diagnostic = "Could not start mklink junction fixture.";
                    return false;
                }
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    diagnostic = "mklink junction fixture timed out.";
                    return false;
                }
                diagnostic = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return process.ExitCode == 0
                    && Directory.Exists(junctionPath)
                    && (File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0;
            }
        }

        static bool TryCreateDirectorySymbolicLink(
            string linkPath,
            string targetPath,
            out string diagnostic)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/ln",
                Arguments = $"-s \"{targetPath}\" \"{linkPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = System.Diagnostics.Process.Start(startInfo))
            {
                if (process == null)
                {
                    diagnostic = "Could not start the ln symbolic-link fixture.";
                    return false;
                }
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(10000))
                {
                    process.Kill();
                    diagnostic = "The ln symbolic-link fixture timed out.";
                    return false;
                }
                diagnostic = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                return process.ExitCode == 0 && Directory.Exists(linkPath);
            }
        }

        [Test]
        public void TestSummary_TotalTestsPrefersRunStartedCountOverTheWholeResultTree()
        {
            // COCli-12 regression: when ExpectedMatchedTests is stale/zero at
            // RunFinished, the summary previously fell back to counting the
            // whole Unity result tree (the "total=1319 while executed=487"
            // symptom). It must prefer the RunStarted filtered count.
            var originalOperationId = Tool_Tests.CurrentTestOperationId;
            var originalRequestId = Tool_Tests.CurrentTestRequestId;
            var originalDiscovered = TestResultCollector.ExpectedDiscoveredTestCount;
            var originalMatched = TestResultCollector.ExpectedMatchedTestCount;
            var collector = TestResultCollector.CreateIsolatedForTests();
            var leaf1 = new FakeTestAdaptor("Ns.Fixture.RunScoped.Test1", isSuite: false);
            var leaf2 = new FakeTestAdaptor("Ns.Fixture.RunScoped.Test2", isSuite: false);
            var runRoot = new FakeTestAdaptor("Ns.Fixture.RunScoped", isSuite: true, leaf1, leaf2);
            var otherLeaf1 = new FakeTestAdaptor("Ns.Fixture.Unscoped.Test1", isSuite: false);
            var otherLeaf2 = new FakeTestAdaptor("Ns.Fixture.Unscoped.Test2", isSuite: false);
            var otherLeaf3 = new FakeTestAdaptor("Ns.Fixture.Unscoped.Test3", isSuite: false);
            var wholeTree = new FakeTestResultAdaptor(
                new FakeTestAdaptor("Ns.Fixture", isSuite: true, runRoot, otherLeaf1, otherLeaf2, otherLeaf3),
                TestStatus.Passed);
            try
            {
                Tool_Tests.CurrentTestOperationId = string.Empty;
                Tool_Tests.CurrentTestRequestId = string.Empty;
                TestResultCollector.ExpectedDiscoveredTestCount = 5;
                TestResultCollector.ExpectedMatchedTestCount = 0; // stale / bypassed discovery

                collector.RunStarted(runRoot);
                collector.RunFinished(wholeTree);

                var summary = collector.GetSummary();
                Assert.That(summary.TotalTests, Is.EqualTo(2), "RunStarted filtered count must win over the whole-tree fallback");
                Assert.That(summary.DiscoveredTests, Is.EqualTo(5));
            }
            finally
            {
                Tool_Tests.CurrentTestOperationId = originalOperationId;
                Tool_Tests.CurrentTestRequestId = originalRequestId;
                TestResultCollector.ExpectedDiscoveredTestCount = originalDiscovered;
                TestResultCollector.ExpectedMatchedTestCount = originalMatched;
            }
        }

        [Test]
        public void EnvironmentUtils_BatchModeDetectionAcceptsFlagAndEditorState()
        {
            var empty = new Dictionary<string, string>();
            var batchArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["batchmode"] = string.Empty };

            Assert.That(EnvironmentUtils.IsBatchMode(false, empty), Is.False);
            Assert.That(EnvironmentUtils.IsBatchMode(true, empty), Is.True, "Application.isBatchMode alone must count");
            Assert.That(EnvironmentUtils.IsBatchMode(false, batchArgs), Is.True, "the -batchmode command-line flag must count");
        }

        [Test]
        public void BuildJobInfo_IsADurableOperationHandleExposingOperationId()
        {
            // COCli-11: build-player must return a durable handle so `--wait`
            // and generic operation polling work instead of failing with
            // "did not return a durable operation handle".
            var job = new Tool_Build.BuildJobInfo { JobId = "job-1" };
            Assert.That(job, Is.InstanceOf<IDurableOperationHandle>());
            Assert.That(job.OperationId, Is.EqualTo("job-1"));
        }

        sealed class FakeTestAdaptor : ITestAdaptor
        {
            readonly ITestAdaptor[] _children;

            internal FakeTestAdaptor(string fullName, bool isSuite, params ITestAdaptor[] children)
            {
                FullName = fullName;
                Name = fullName.Split('.').Last();
                IsSuite = isSuite;
                _children = children ?? Array.Empty<ITestAdaptor>();
            }

            public string Id => FullName;
            public string Name { get; }
            public string FullName { get; }
            public int TestCaseCount => IsSuite
                ? _children.Sum(child => child.TestCaseCount)
                : 1;
            public bool HasChildren => _children.Length > 0;
            public bool IsSuite { get; }
            public IEnumerable<ITestAdaptor> Children => _children;
            public ITestAdaptor Parent => null!;
            public int TestCaseTimeout => 0;
            public NUnit.Framework.Interfaces.ITypeInfo TypeInfo => null!;
            public NUnit.Framework.Interfaces.IMethodInfo Method => null!;
            public object[] Arguments => Array.Empty<object>();
            public string[] Categories => Array.Empty<string>();
            public bool IsTestAssembly => false;
            public RunState RunState => RunState.Runnable;
            public string Description => string.Empty;
            public string SkipReason => string.Empty;
            public string ParentId => string.Empty;
            public string ParentFullName => string.Empty;
            public string UniqueName => FullName;
            public string ParentUniqueName => string.Empty;
            public int ChildIndex => 0;
            public TestMode TestMode => TestMode.EditMode;
        }

        sealed class FakeTestResultAdaptor : ITestResultAdaptor
        {
            readonly TestStatus _status;

            internal FakeTestResultAdaptor(ITestAdaptor test, TestStatus status)
            {
                Test = test;
                _status = status;
            }

            public ITestAdaptor Test { get; }
            public string Name => Test.Name;
            public string FullName => Test.FullName;
            public string ResultState => _status.ToString();
            public TestStatus TestStatus => _status;
            public double Duration => 0.01;
            public DateTime StartTime => DateTime.UtcNow;
            public DateTime EndTime => DateTime.UtcNow;
            public string Message => string.Empty;
            public string StackTrace => string.Empty;
            public int AssertCount => 0;
            public int FailCount => _status == TestStatus.Failed ? 1 : 0;
            public int PassCount => _status == TestStatus.Passed ? 1 : 0;
            public int SkipCount => _status == TestStatus.Skipped ? 1 : 0;
            public int InconclusiveCount => _status == TestStatus.Inconclusive ? 1 : 0;
            public bool HasChildren => false;
            public IEnumerable<ITestResultAdaptor> Children
                => Array.Empty<ITestResultAdaptor>();
            public string Output => string.Empty;
            public NUnit.Framework.Interfaces.TNode ToXml() => null!;
        }

        sealed class MissingCancelApi
        {
        }

        sealed class ThrowingCancelApi
        {
            public void CancelTestRun()
                => throw new InvalidOperationException("fixture cancellation failure");
        }

        sealed class SilentCancelApi
        {
            public int CallCount { get; private set; }

            public void CancelTestRun()
                => CallCount++;
        }
    }

    internal sealed class FeedbackUniqueClassNameFixture
    {
    }
}

namespace FeedbackClassNameA
{
    internal sealed class DuplicateClassNameFixture
    {
    }
}

namespace FeedbackClassNameB
{
    internal sealed class DuplicateClassNameFixture
    {
    }
}
