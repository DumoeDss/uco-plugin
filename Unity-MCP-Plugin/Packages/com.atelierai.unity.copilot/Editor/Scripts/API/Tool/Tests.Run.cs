/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Editor.API.TestRunner;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    public static partial class Tool_Tests
    {
        public const string TestsRunToolId = "tests-run";
        internal const int DefaultExecutionTimeoutSeconds = 1800;
        internal const int DefaultWaitTimeoutSeconds = 10;
        internal const int TimeoutCancellationGraceSeconds = 5;

        [Serializable]
        internal sealed class TestOperationMetadata
        {
            public string Filters = string.Empty;
            public int ExecutionTimeoutSeconds = DefaultExecutionTimeoutSeconds;
            public int WaitTimeoutSeconds = DefaultWaitTimeoutSeconds;
            public bool SandboxScene;
        }
        [UcoTool
        (
            TestsRunToolId,
            Title = "Tests / Run",
            Enabled = true,
            DurableOperationStart = true
        )]
        [UcoSkillDescription("Execute Unity tests (`EditMode` or `PlayMode`) and return per-test results. " +
            "Supports filtering by test assembly, namespace, class, and method. " +
            "Refreshes the AssetDatabase first; defers execution across domain reloads if scripts changed. " +
            "Precondition: every open scene must be saved — dirty scenes abort the run.")]
        [UcoSkillBody("Execute Unity tests and return detailed results. " +
            "Supports filtering by test mode, assembly, namespace, class, and method. " +
            "Recommended to use '" + nameof(TestMode.EditMode) + "' for faster iteration during development. " +
            "Precondition: every open scene MUST be saved (no unsaved changes). If any open scene is dirty, " +
            "this tool throws an InvalidOperationException listing the dirty scenes; save them and retry.\n\n" +
            "## Filters\n\n" +
            "- `testMode` (default `EditMode`) — `EditMode` or `PlayMode`. EditMode is faster; prefer it during iteration.\n" +
            "- `testAssembly` / `testNamespace` / `testClass` / `testMethod` — optional, layered filters. Namespace and " +
            "class filters become regex `groupNames` so the validation count and Unity's execution stay in sync. " +
            "`testMethod` must be fully qualified (`Namespace.FixtureName.TestName`).\n\n" +
            "## Response toggles\n\n" +
            "- `includePassingTests` (default `false`) — include details for passing tests; otherwise only failing test details are returned.\n" +
            "- `includeMessages` (default `true`) — include per-test result messages.\n" +
            "- `includeStacktrace` (default `false`) — include stack traces for failing tests.\n" +
            "- `includeLogs` (default `false`) — include console logs captured during the run.\n" +
            "- `logType` (default `Warning`) — minimum log severity to include.\n" +
            "- `includeLogsStacktrace` (default `false`) — include log stack traces (large payload; use sparingly).\n\n" +
            "## Domain reloads\n\n" +
            "If the AssetDatabase refresh triggers compilation, the tool persists the run parameters to `SessionState`, " +
            "returns `Processing`, and resumes the run automatically after the reload completes. Pre-existing " +
            "compilation errors short-circuit the run and return the error details so the caller can fix the project first.")]
        [Description("Execute Unity tests and return detailed results. " +
            "Supports filtering by test mode, assembly, namespace, class, and method. " +
            "Recommended to use '" + nameof(TestMode.EditMode) + "' for faster iteration during development. " +
            "Precondition: every open scene MUST be saved (no unsaved changes). If any open scene is dirty, " +
            "this tool throws an InvalidOperationException listing the dirty scenes; save them and retry.")]
        public static async Task<EditorOperationInfo> Run
        (
            [Description("Test mode to run. Options: '" + nameof(TestMode.EditMode) + "', '" + nameof(TestMode.PlayMode) + "'. Default: '" + nameof(TestMode.EditMode) + "'")]
            TestMode testMode = TestMode.EditMode,
            [Description("Specific test assembly name to run (optional). Example: 'Assembly-CSharp-Editor-testable'")]
            string? testAssembly = null,
            [Description("Specific test namespace to run (optional). Example: 'MyTestNamespace'")]
            string? testNamespace = null,
            [Description("Specific test class name to run (optional). Example: 'MyTestClass'")]
            string? testClass = null,
            [Description("Specific fully qualified test method to run (optional). Example: 'MyTestNamespace.FixtureName.TestName'")]
            string? testMethod = null,

            [Description("Include details for all tests, both passing and failing (default: false). If you just need details for failing tests, set to false.")]
            bool includePassingTests = false,
            [Description("Include test result messages in the test results (default: true). If you just need pass/fail status, set to false.")]
            bool includeMessages = true,
            [Description("Include stack traces in the test results (default: false).")]
            bool includeStacktrace = false,

            [Description("Include console logs in the test results (default: false).")]
            bool includeLogs = false,
            [Description("Log type filter for console logs. Options: '" + nameof(LogType.Log) + "', '" + nameof(LogType.Warning) + "', '" + nameof(LogType.Assert) + "', '" + nameof(LogType.Error) + "', '" + nameof(LogType.Exception) + "'. (default: '" + nameof(LogType.Warning) + "')")]
            LogType logType = LogType.Warning,
            [Description("Include stack traces for console logs in the test results (default: false). This is huge amount of data, use only if really needed.")]
            bool includeLogsStacktrace = false,

            [Description("Maximum total test operation duration in seconds. Default 1800; valid range 1..86400.")]
            int executionTimeoutSeconds = DefaultExecutionTimeoutSeconds,
            [Description("Maximum discovery/initial wait in seconds before returning a failed operation. Default 10; valid range 1..300.")]
            int waitTimeoutSeconds = DefaultWaitTimeoutSeconds,
            [Description("Run the tests inside a disposable sandbox scene: a new untitled scene is opened for the run " +
                "and the previously active scene setup, selection, and dirty state are restored afterwards. " +
                "User scenes that were dirty before the call still block the run exactly as without sandbox mode. Default false.")]
            bool sandboxScene = false,

            [RequestID]
            string? requestId = null
        )
        {
            return await MainThread.Instance.RunAsync(() =>
            {
                // Validation happens before the durable record and before all owned side effects.
                ThrowIfAnyOpenSceneIsDirty();
                if (executionTimeoutSeconds < 1 || executionTimeoutSeconds > 86400)
                    throw new ArgumentOutOfRangeException(nameof(executionTimeoutSeconds),
                        "executionTimeoutSeconds must be between 1 and 86400.");
                if (waitTimeoutSeconds < 1 || waitTimeoutSeconds > 300)
                    throw new ArgumentOutOfRangeException(nameof(waitTimeoutSeconds),
                        "waitTimeoutSeconds must be between 1 and 300.");

                var existingOperation = EditorOperationRegistry.List("tests-run", includeTerminal: false)
                    .FirstOrDefault();
                if (existingOperation != null)
                    throw new InvalidOperationException(
                        $"Test operation '{existingOperation.OperationId}' is already " +
                        $"{existingOperation.Status} ({existingOperation.Phase}).");

                var filterParams = new TestFilterParameters(
                    testAssembly, testNamespace, testClass, testMethod);
                var metadata = JsonUtility.ToJson(new TestOperationMetadata
                {
                    Filters = filterParams.ToString(),
                    ExecutionTimeoutSeconds = executionTimeoutSeconds,
                    WaitTimeoutSeconds = waitTimeoutSeconds,
                    SandboxScene = sandboxScene
                });
                var operation = EditorOperationOwnerRegistry.Create(
                    "tests-run", "queued", metadata,
                    sourceRevision: EditorOperationRegistry.CaptureSourceRevision());
                // Console attribution (COCli-07): log entries emitted for the
                // rest of this execution window carry the operation id.
                LogCallScope.SetOperationOverlay(operation.OperationId);

                // Reserve FIFO position while the initiating request still holds its
                // own scheduler lease. The returned handle remains queued; PlayerPrefs,
                // refresh, discovery, and TestRunner execution are owner-side effects.
                EditorOperationOwnerRegistry.ScheduleExecution(
                    operation.OperationId,
                    "preparing",
                    () => _ = PrepareTestOperationAsync(
                        operation.OperationId,
                        requestId ?? string.Empty,
                        testMode,
                        testAssembly,
                        testNamespace,
                        testClass,
                        testMethod,
                        filterParams,
                        includePassingTests,
                        includeMessages,
                        includeStacktrace,
                        includeLogs,
                        logType,
                        includeLogsStacktrace,
                        waitTimeoutSeconds,
                        sandboxScene));
                return operation;
            });
        }

        static async Task PrepareTestOperationAsync(
            string operationId,
            string requestId,
            TestMode testMode,
            string? testAssembly,
            string? testNamespace,
            string? testClass,
            string? testMethod,
            TestFilterParameters filterParams,
            bool includePassingTests,
            bool includeMessages,
            bool includeStacktrace,
            bool includeLogs,
            LogType logType,
            bool includeLogsStacktrace,
            int waitTimeoutSeconds,
            bool sandboxScene)
        {
            try
            {
                // RunAdmittedExecution invokes this method on Unity's main thread.
                TestResultCollector.TestOperationId.Value = operationId;
                TestResultCollector.TestCallRequestID.Value = requestId;
                TestResultCollector.IncludePassingTests.Value = includePassingTests;
                TestResultCollector.IncludeMessage.Value = includeMessages;
                TestResultCollector.IncludeMessageStacktrace.Value = includeStacktrace;
                TestResultCollector.IncludeLogs.Value = includeLogs;
                TestResultCollector.IncludeLogsMinLevel.Value = (int)logType;
                TestResultCollector.IncludeLogsStacktrace.Value = includeLogsStacktrace;

                if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Info))
                    Debug.Log($"[TestRunner] Refreshing AssetDatabase before running {testMode} tests...");
                AssetDatabase.Refresh();

                if (EditorApplication.isCompiling)
                {
                    SavePendingTestRun(operationId, testMode,
                        testAssembly, testNamespace, testClass, testMethod);
                    EditorOperationRegistry.Update(operationId, "waiting-for-compilation");
                    EditorApplication.update -= ResumePendingTestRunOnce;
                    EditorApplication.update += ResumePendingTestRunOnce;
                    return;
                }

                if (EditorUtility.scriptCompilationFailed)
                {
                    var compileMessage = "Cannot run tests because the Unity project has compilation errors.";
                    ClearTestRunOwnership(operationId);
                    EditorOperationRegistry.Fail(operationId,
                        "test-compilation-failed",
                        compileMessage,
                        "compilation-failed");
                    NotifyTestOperationFailed(operationId, requestId, compileMessage);
                    return;
                }

                var discovery = await DiscoverMatchingTests(
                    TestRunnerApi,
                    testMode,
                    filterParams,
                    TimeSpan.FromSeconds(waitTimeoutSeconds));

                // RetrieveTestList callbacks are not guaranteed to resume on Unity's main
                // thread. Marshal every Unity/PlayerPrefs/operation continuation explicitly.
                await MainThread.Instance.RunAsync(() =>
                {
                    var current = EditorOperationRegistry.Get(operationId);
                    if (current == null || current.IsTerminal)
                    {
                        ClearTestRunOwnership(operationId);
                        return;
                    }
                    if (current.CancellationRequested)
                    {
                        EditorOperationRegistry.CancelRunning(
                            operationId, "cancelled-after-discovery");
                        ClearTestRunOwnership(operationId);
                        NotifyTestOperationFailed(operationId, requestId, "Test run was cancelled before execution.");
                        return;
                    }
                    if (discovery.MatchedNames.Length == 0)
                    {
                        var message = Error.NoTestsFound(filterParams);
                        EditorOperationRegistry.Fail(operationId,
                            "tests-no-match", message, "filter-validation");
                        ClearTestRunOwnership(operationId);
                        NotifyTestOperationFailed(operationId, requestId, message);
                        return;
                    }

                    StartDiscoveredTests(operationId, testMode, filterParams, discovery, sandboxScene);
                });
            }
            catch (Exception ex)
            {
                await MainThread.Instance.RunAsync(() =>
                {
                    if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Error))
                        Debug.LogError($"[TestRunner] Failed to prepare {testMode} operation.");
                    var failureMessage = Error.TestExecutionFailed(ex.GetBaseException().Message);
                    var current = EditorOperationRegistry.Get(operationId);
                    if (current != null && !current.IsTerminal)
                    {
                        EditorOperationRegistry.Fail(operationId,
                            "test-execution-failed",
                            failureMessage,
                            "start-failed");
                    }
                    ClearTestRunOwnership(operationId);
                    NotifyTestOperationFailed(operationId, requestId, failureMessage);
                });
            }
        }

        sealed class TestDiscovery
        {
            public int DiscoveredCount;
            public string[] MatchedNames = Array.Empty<string>();
        }

        internal sealed class DiscoveredTest
        {
            public string Assembly = string.Empty;
            public string Namespace = string.Empty;
            public string ClassFullName = string.Empty;
            public string ClassSimpleName = string.Empty;
            public string MethodName = string.Empty;
            public string FullName = string.Empty;
        }

        static Filter CreateTestFilter(
            TestMode testMode,
            TestFilterParameters filterParams,
            string[] matchedNames)
        {
            var filter = new Filter
            {
                testMode = testMode,
                testNames = matchedNames
            };
            if (!string.IsNullOrEmpty(filterParams.TestAssembly))
                filter.assemblyNames = new[] { NormalizeAssembly(filterParams.TestAssembly!) };
            return filter;
        }

        static async Task<TestDiscovery> DiscoverMatchingTests(
            TestRunnerApi testRunnerApi,
            TestMode testMode,
            TestFilterParameters filterParams,
            TimeSpan? waitTimeout = null)
        {
            var tcs = new TaskCompletionSource<TestDiscovery>();
            testRunnerApi.RetrieveTestList(testMode, testRoot =>
            {
                try
                {
                    var discovered = new List<DiscoveredTest>();
                    if (testRoot != null)
                        CollectDiscoveredTests(testRoot, string.Empty, discovered);
                    var matched = discovered
                        .Where(test => MatchesAll(test, filterParams))
                        .Select(test => test.FullName)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    tcs.TrySetResult(new TestDiscovery
                    {
                        DiscoveredCount = discovered.Count,
                        MatchedNames = matched
                    });
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            var effectiveWait = waitTimeout ?? TimeSpan.FromSeconds(DefaultWaitTimeoutSeconds);
            var timeoutTask = Task.Delay(effectiveWait);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completed == timeoutTask)
                throw new TimeoutException(
                    $"Test discovery timed out after {effectiveWait.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds.");

            var result = await tcs.Task;
            if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Info))
                Debug.Log($"[TestRunner] discovered {result.DiscoveredCount}, matched {result.MatchedNames.Length} " +
                    $"{testMode} tests for {filterParams}");
            return result;
        }

        static void StartDiscoveredTests(
            string operationId,
            TestMode testMode,
            TestFilterParameters filterParams,
            TestDiscovery discovery,
            bool sandboxScene)
        {
            TestResultCollector.ExpectedDiscoveredTests.Value = discovery.DiscoveredCount;
            TestResultCollector.ExpectedMatchedTests.Value = discovery.MatchedNames.Length;
            var filter = CreateTestFilter(testMode, filterParams, discovery.MatchedNames);
            var settings = new ExecutionSettings(filter);

            EditorOperationOwnerRegistry.ScheduleExecution(
                operationId, "scheduled",
                () => ExecuteWhenPreviousRunnerSettles(operationId, settings, sandboxScene));
        }

        static void ExecuteWhenPreviousRunnerSettles(
            string operationId,
            ExecutionSettings settings,
            bool sandboxScene)
        {
            var operation = EditorOperationRegistry.Get(operationId);
            if (operation == null || operation.IsTerminal)
            {
                ClearTestRunOwnership(operationId);
                return;
            }

            if (operation.CancellationRequested)
            {
                EditorOperationRegistry.CancelRunning(operationId, "cancelled-before-execution");
                ClearTestRunOwnership(operationId);
                return;
            }

            if (TestResultCollector.HasUnsettledRun)
            {
                if (!string.Equals(operation.Phase, "waiting-for-previous-test-run",
                        StringComparison.Ordinal))
                {
                    EditorOperationRegistry.Update(operationId,
                        "waiting-for-previous-test-run", cancellationPending: false);
                }
                EditorOperationRegistry.Schedule(() =>
                    ExecuteWhenPreviousRunnerSettles(operationId, settings, sandboxScene));
                return;
            }

            try
            {
                // Reassert the queued operation immediately before Execute. A timed-out
                // predecessor may have cleared its own persisted ownership while this job
                // was waiting, but its late callbacks are bound to its captured context.
                TestResultCollector.TestOperationId.Value = operationId;

                // Scene hygiene (COCli-06): the mutation baseline is always
                // captured (persisted so it survives reload-triggered runs);
                // a sandboxed run additionally captures the restorable setup
                // and opens the disposable scene the tests will execute in.
                // The existing dirty-scene blocker already ran at the call,
                // so only the sandbox scene is ever exempt — user scenes that
                // were dirty still blocked the run before this point.
                var baseline = EditorSceneSandbox.CaptureMutationBaseline();
                TestRunSceneSandbox.PersistForOperation(
                    operationId,
                    sandboxScene ? EditorSceneSandbox.CaptureState() : null,
                    baseline);
                if (sandboxScene)
                    _ = EditorSceneSandbox.OpenSandboxScene();

                EditorOperationRegistry.Update(operationId, "executing", 0);
                TestRunnerApi.Execute(settings);
            }
            catch (Exception ex)
            {
                var failureMessage = Error.TestExecutionFailed(ex.GetBaseException().Message);
                // Capture before ClearTestRunOwnership wipes the persisted ids.
                var requestId = TestResultCollector.TestCallRequestID.Value;
                EditorOperationRegistry.Fail(operationId, "test-execution-failed",
                    failureMessage, "start-failed");
                ClearTestRunOwnership(operationId);
                NotifyTestOperationFailed(operationId, requestId, failureMessage);
            }
        }

        public static void RequestCancellation(string operationId)
            => RequestCancellation(operationId, TestRunnerApi);

        internal static string? RequestCancellation(string operationId, object testRunnerApi)
        {
            var operation = EditorOperationRegistry.Get(operationId);
            if (operation == null || operation.Kind != "tests-run" || operation.IsTerminal)
                return null;

            try
            {
                var cancel = testRunnerApi.GetType().GetMethod("CancelTestRun",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public,
                    null, Type.EmptyTypes, null);
                if (cancel == null)
                {
                    EditorOperationRegistry.Update(operationId, "cancellation-pending", cancellationPending: true);
                    return "Unity Test Runner does not expose CancelTestRun.";
                }
                cancel.Invoke(testRunnerApi, null);
                EditorOperationRegistry.Update(operationId, "cancelling", cancellationPending: true);
                return null;
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;
                EditorOperationRegistry.Update(operationId,
                    "cancellation-pending: " + message,
                    cancellationPending: true);
                return "CancelTestRun failed: " + message;
            }
        }

        static void CollectDiscoveredTests(
            ITestAdaptor test,
            string inheritedAssembly,
            List<DiscoveredTest> output)
        {
            var assembly = InferAssemblyName(test, inheritedAssembly);
            if (!test.IsSuite)
            {
                // Use Unity Test Runner's NUnit metadata instead of parsing the
                // display name. Parameterized/default-namespace tests can contain
                // dots and brackets that are not class/method separators.
                var typeInfo = test.TypeInfo ?? test.Method?.TypeInfo;
                var classNode = test.Parent;
                var namespaceNode = classNode?.Parent;
                var exactAssembly = typeInfo?.Assembly?.GetName().Name;
                output.Add(new DiscoveredTest
                {
                    Assembly = string.IsNullOrEmpty(exactAssembly) ? assembly : exactAssembly,
                    Namespace = typeInfo?.Namespace
                        ?? (namespaceNode != null && !namespaceNode.IsTestAssembly
                            ? namespaceNode.FullName ?? string.Empty
                            : string.Empty),
                    ClassFullName = typeInfo?.FullName ?? classNode?.FullName ?? string.Empty,
                    ClassSimpleName = typeInfo?.Name ?? classNode?.Name ?? string.Empty,
                    MethodName = test.Method?.Name ?? test.Name ?? string.Empty,
                    FullName = test.FullName ?? string.Empty
                });
            }

            if (!test.HasChildren || test.Children == null) return;
            foreach (var child in test.Children)
                CollectDiscoveredTests(child, assembly, output);
        }

        static string InferAssemblyName(ITestAdaptor test, string inherited)
        {
            if (test.IsTestAssembly && !string.IsNullOrEmpty(test.Name))
                return System.IO.Path.GetFileNameWithoutExtension(test.Name);
            return inherited;
        }

        internal static bool MatchesAll(DiscoveredTest test, TestFilterParameters filters)
        {
            if (!string.IsNullOrEmpty(filters.TestAssembly)
                && !string.Equals(test.Assembly, NormalizeAssembly(filters.TestAssembly!), StringComparison.OrdinalIgnoreCase))
                return false;
            if (!string.IsNullOrEmpty(filters.TestNamespace)
                && !string.Equals(test.Namespace, filters.TestNamespace, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrEmpty(filters.TestClass)
                && !string.Equals(test.ClassFullName, filters.TestClass, StringComparison.Ordinal)
                && !string.Equals(test.ClassSimpleName, filters.TestClass, StringComparison.Ordinal))
                return false;
            if (!string.IsNullOrEmpty(filters.TestMethod)
                && !string.Equals(test.FullName, filters.TestMethod, StringComparison.Ordinal)
                && !string.Equals(test.MethodName, filters.TestMethod, StringComparison.Ordinal))
                return false;
            return true;
        }

        static string NormalizeAssembly(string assembly)
            => assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? assembly.Substring(0, assembly.Length - 4)
                : assembly;
    }

    [InitializeOnLoad]
    internal static class TestOperationTimeoutMonitor
    {
        internal const string TimeoutCancellationPendingPhase = "execution-timeout-cancellation-pending";
        static double s_nextPoll;

        static TestOperationTimeoutMonitor()
            => EditorApplication.update += Poll;

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < s_nextPoll) return;
            s_nextPoll = EditorApplication.timeSinceStartup + 1d;

            foreach (var operation in EditorOperationRegistry.List("tests-run", includeTerminal: false))
                ProcessOperation(operation, DateTime.UtcNow,
                    operationId => Tool_Tests.RequestCancellation(operationId, Tool_Tests.TestRunnerApi));
        }

        internal static void ProcessOperation(
            EditorOperationInfo operation,
            DateTime utcNow,
            Func<string, string?> requestRunnerCancellation)
        {
            if (operation == null || operation.IsTerminal)
                return;

            Tool_Tests.TestOperationMetadata? metadata = null;
            try
            {
                if (!string.IsNullOrEmpty(operation.MetadataJson))
                    metadata = JsonUtility.FromJson<Tool_Tests.TestOperationMetadata>(operation.MetadataJson);
            }
            catch { }
            var timeoutSeconds = metadata?.ExecutionTimeoutSeconds
                ?? Tool_Tests.DefaultExecutionTimeoutSeconds;
            if (!DateTime.TryParse(operation.CreatedAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var createdAt))
                return;
            if (utcNow - createdAt.ToUniversalTime() < TimeSpan.FromSeconds(timeoutSeconds))
                return;

            var current = EditorOperationRegistry.Get(operation.OperationId);
            if (current == null || current.IsTerminal)
                return;

            if (current.CancellationRequested
                && DateTime.TryParse(current.CancellationRequestedAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var cancellationRequestedAt))
            {
                var graceElapsed = utcNow - cancellationRequestedAt.ToUniversalTime()
                    >= TimeSpan.FromSeconds(Tool_Tests.TimeoutCancellationGraceSeconds);
                if (!graceElapsed)
                {
                    if (!current.Phase.StartsWith(TimeoutCancellationPendingPhase, StringComparison.Ordinal))
                        EditorOperationRegistry.Update(operation.OperationId,
                            TimeoutCancellationPendingPhase, cancellationPending: true);
                    return;
                }

                var diagnostic = current.Phase.Length > TimeoutCancellationPendingPhase.Length
                    && current.Phase.StartsWith(TimeoutCancellationPendingPhase, StringComparison.Ordinal)
                    ? current.Phase.Substring(TimeoutCancellationPendingPhase.Length).TrimStart(':', ' ')
                    : string.Empty;
                var message = $"Test execution exceeded its {timeoutSeconds}-second timeout and did not " +
                    $"terminate within the {Tool_Tests.TimeoutCancellationGraceSeconds}-second cancellation grace period.";
                if (!string.IsNullOrEmpty(diagnostic))
                    message += " Cancellation request diagnostic: " + diagnostic;
                EditorOperationRegistry.Fail(operation.OperationId, "tests-execution-timeout",
                    message, "execution-timeout");
                // Capture before ClearTestRunOwnership wipes the persisted ids.
                var timeoutRequestId = TestResultCollector.TestCallRequestID.Value;
                Tool_Tests.ClearTestRunOwnership(operation.OperationId);
                Tool_Tests.NotifyTestOperationFailed(operation.OperationId, timeoutRequestId, message);
                return;
            }

            var cancellation = EditorOperationRegistry.RequestCancellation(operation.OperationId);
            if (cancellation.IsTerminal)
            {
                Tool_Tests.ClearTestRunOwnership(operation.OperationId);
                return;
            }

            string? cancellationDiagnostic;
            try
            {
                cancellationDiagnostic = requestRunnerCancellation(operation.OperationId);
            }
            catch (Exception ex)
            {
                cancellationDiagnostic = "CancelTestRun failed: " + ex.GetBaseException().Message;
            }

            current = EditorOperationRegistry.Get(operation.OperationId);
            if (current == null || current.IsTerminal)
            {
                Tool_Tests.ClearTestRunOwnership(operation.OperationId);
                return;
            }

            var phase = TimeoutCancellationPendingPhase;
            if (!string.IsNullOrWhiteSpace(cancellationDiagnostic))
                phase += ": " + cancellationDiagnostic;
            EditorOperationRegistry.Update(operation.OperationId, phase, cancellationPending: true);
        }
    }
}
