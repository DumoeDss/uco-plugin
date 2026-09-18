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
using System.Linq;
using System.Text.Json;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.API.TestRunner
{
    internal sealed class TestRunCallbackOwnership
    {
        internal sealed class Context
        {
            public string OperationId = string.Empty;
            public string RequestId = string.Empty;
            public bool IncludePassingTests;
            public bool IncludeMessage;
            public bool IncludeMessageStacktrace;
            public bool IncludeLogs;
            public int IncludeLogsMinLevel;
            public bool IncludeLogsStacktrace;
            /// <summary>
            /// Filtered leaf count reported by RunStarted for the owning run.
            /// RunFinished prefers it over counting the whole Unity result
            /// tree when the persisted ExpectedMatchedTests is stale/zero
            /// (COCli-12: "total=1319 while executed=487").
            /// </summary>
            public int RunStartedTestCount;
        }

        readonly object _gate = new();
        Context? _active;

        internal bool HasUnsettledRun
        {
            get
            {
                lock (_gate)
                    return _active != null;
            }
        }

        internal string ActiveOperationId
        {
            get
            {
                lock (_gate)
                    return _active?.OperationId ?? string.Empty;
            }
        }

        internal bool CaptureCurrentRun()
        {
            var context = new Context
            {
                OperationId = TestResultCollector.TestOperationId.Value,
                RequestId = TestResultCollector.TestCallRequestID.Value,
                IncludePassingTests = TestResultCollector.IncludePassingTests.Value,
                IncludeMessage = TestResultCollector.IncludeMessage.Value,
                IncludeMessageStacktrace = TestResultCollector.IncludeMessageStacktrace.Value,
                IncludeLogs = TestResultCollector.IncludeLogs.Value,
                IncludeLogsMinLevel = TestResultCollector.IncludeLogsMinLevel.Value,
                IncludeLogsStacktrace = TestResultCollector.IncludeLogsStacktrace.Value
            };

            lock (_gate)
            {
                if (_active != null)
                    return false;
                _active = context;
                return true;
            }
        }

        internal void SetRunStartedTestCount(int count)
        {
            lock (_gate)
            {
                if (_active != null)
                    _active.RunStartedTestCount = count;
            }
        }

        internal void UpdateFromTestFinished(int executedTests, int matchedTests)
        {
            string operationId;
            lock (_gate)
                operationId = _active?.OperationId ?? string.Empty;
            if (string.IsNullOrEmpty(operationId))
                return;

            var operation = EditorOperationRegistry.Get(operationId);
            if (operation == null || operation.IsTerminal)
                return;
            var total = Math.Max(1, matchedTests);
            EditorOperationRegistry.Update(operationId, "executing",
                Math.Min(1d, (double)executedTests / total));
        }

        internal Context? ReleaseForRunFinished()
        {
            lock (_gate)
            {
                var released = _active;
                _active = null;
                return released;
            }
        }

        internal bool CompleteFromRunFinished(
            Context? context,
            int failedTests,
            int executedTests,
            string resultJson)
        {
            if (context == null)
                return false;

            var completed = TestResultCollector.CompleteOperationFromCallback(
                context.OperationId, failedTests, executedTests, resultJson);
            ClearPersistedOwnershipIfMatching(context);
            return completed;
        }

        static void ClearPersistedOwnershipIfMatching(Context context)
        {
            if (!string.IsNullOrEmpty(context.OperationId)
                && string.Equals(TestResultCollector.TestOperationId.Value,
                    context.OperationId, StringComparison.Ordinal))
            {
                TestResultCollector.TestOperationId.Value = string.Empty;
                TestResultCollector.ExpectedDiscoveredTests.Value = 0;
                TestResultCollector.ExpectedMatchedTests.Value = 0;
            }

            if (!string.IsNullOrEmpty(context.RequestId)
                && string.Equals(TestResultCollector.TestCallRequestID.Value,
                    context.RequestId, StringComparison.Ordinal))
            {
                TestResultCollector.TestCallRequestID.Value = string.Empty;
            }
        }
    }

    public class TestResultCollector : ICallbacks
    {
        static volatile int counter = 0;
        static readonly TestRunCallbackOwnership s_callbackOwnership = new();

        readonly TestRunCallbackOwnership _callbackOwnership;
        readonly object _logsMutex = new();
        readonly List<TestResultData> _results = new();
        readonly TestSummaryData _summary = new();
        readonly List<TestLogEntry> _logs = new();
        readonly TestMode _testMode;

        DateTime startTime;

        public List<TestResultData> GetResults() => _results;
        public TestSummaryData GetSummary() => _summary;
        public List<TestLogEntry> GetLogs()
        {
            lock (_logsMutex)
            {
                return _logs.ToList();
            }
        }
        public TestMode GetTestMode() => _testMode;

        public string TestModeAsString => _testMode switch
        {
            TestMode.EditMode => "EditMode",
            TestMode.PlayMode => "PlayMode",
            _ => "Unknown"
        };

        public static PlayerPrefsString TestCallRequestID = new PlayerPrefsString("Unity_COPILOT_TestRunner_TestCallRequestID");
        public static PlayerPrefsString TestOperationId = new PlayerPrefsString("Unity_COPILOT_TestRunner_TestOperationId");
        public static PlayerPrefsInt ExpectedDiscoveredTests = new PlayerPrefsInt("Unity_COPILOT_TestRunner_DiscoveredTests");
        public static PlayerPrefsInt ExpectedMatchedTests = new PlayerPrefsInt("Unity_COPILOT_TestRunner_MatchedTests");

        internal static int ExpectedDiscoveredTestCount
        {
            get => ExpectedDiscoveredTests.Value;
            set => ExpectedDiscoveredTests.Value = value;
        }

        internal static int ExpectedMatchedTestCount
        {
            get => ExpectedMatchedTests.Value;
            set => ExpectedMatchedTests.Value = value;
        }

        public static PlayerPrefsBool IncludePassingTests = new PlayerPrefsBool("Unity_COPILOT_TestRunner_IncludePassingTests");
        public static PlayerPrefsBool IncludeMessage = new PlayerPrefsBool("Unity_COPILOT_TestRunner_IncludeMessage", true);
        public static PlayerPrefsBool IncludeMessageStacktrace = new PlayerPrefsBool("Unity_COPILOT_TestRunner_IncludeStacktrace");

        public static PlayerPrefsBool IncludeLogs = new PlayerPrefsBool("Unity_COPILOT_TestRunner_IncludeLogs");
        public static PlayerPrefsInt IncludeLogsMinLevel = new PlayerPrefsInt("Unity_COPILOT_TestRunner_IncludeLogsMinLevel", (int)LogType.Warning);
        public static PlayerPrefsBool IncludeLogsStacktrace = new PlayerPrefsBool("Unity_COPILOT_TestRunner_IncludeLogsStacktrace");

        internal static bool HasUnsettledRun => s_callbackOwnership.HasUnsettledRun;
        internal static string ActiveOperationId => s_callbackOwnership.ActiveOperationId;

        public TestResultCollector() : this(s_callbackOwnership, enforceSingleton: true)
        {
        }

        TestResultCollector(
            TestRunCallbackOwnership callbackOwnership,
            bool enforceSingleton)
        {
            _callbackOwnership = callbackOwnership
                ?? throw new ArgumentNullException(nameof(callbackOwnership));
            if (!enforceSingleton)
                return;

            int newCount = System.Threading.Interlocked.Increment(ref counter);

            UnityCopilotPluginEditor.Instance.LogTrace("Ctor", typeof(TestResultCollector));

            if (newCount > 1)
                throw new InvalidOperationException($"Only one instance of {nameof(TestResultCollector)} is allowed. Current count: {newCount}");
        }

        internal static TestResultCollector CreateIsolatedForTests()
            => new(new TestRunCallbackOwnership(), enforceSingleton: false);

        public void RunStarted(ITestAdaptor testsToRun)
        {
            UnityCopilotPluginEditor.Instance.LogInfo("RunStarted", typeof(TestResultCollector));

            if (!_callbackOwnership.CaptureCurrentRun())
            {
                UnityCopilotPluginEditor.Instance.LogError(
                    "Ignoring overlapping test RunStarted callback while operation '{operationId}' still owns the runner.",
                    typeof(TestResultCollector), _callbackOwnership.ActiveOperationId);
                return;
            }

            startTime = DateTime.Now;
            var testCount = CountTests(testsToRun);
            _callbackOwnership.SetRunStartedTestCount(testCount);

            lock (_logsMutex)
            {
                _logs.Clear();
            }
            _results.Clear();
            _summary.Clear();
            _summary.TotalTests = testCount;
            _summary.DiscoveredTests = ExpectedDiscoveredTests.Value;
            _summary.MatchedTests = ExpectedMatchedTests.Value;

            // Subscribe to log messages (using threaded version to catch logs from all threads)
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            Application.logMessageReceivedThreaded += OnLogMessageReceived;

            UnityCopilotPluginEditor.Instance.LogInfo("Run {testMode} started: {testCount} tests.",
                typeof(TestResultCollector), TestModeAsString, testCount);
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            UnityCopilotPluginEditor.Instance.LogInfo("RunFinished", typeof(TestResultCollector));

            // Unsubscribe from log messages
            Application.logMessageReceivedThreaded -= OnLogMessageReceived;

            // Release the ownership context up front: its captured members
            // (including RunStartedTestCount) feed the summary fallback below
            // and the structured response afterwards.
            var ownership = _callbackOwnership.ReleaseForRunFinished();

            var duration = DateTime.Now - startTime;
            _summary.Duration = DateTime.Now - startTime;
            _summary.ExecutedTests = _results.Count;
            // COCli-12: TotalTests reports the filtered execution scope, never
            // the whole-project discovery count. Preference order: the
            // discovery-matched count, the RunStarted filtered count, then
            // (only for runs that bypassed discovery) the executed count
            // before falling back to counting the Unity result tree.
            _summary.TotalTests = _summary.MatchedTests > 0
                ? _summary.MatchedTests
                : ownership is { RunStartedTestCount: > 0 }
                    ? ownership.RunStartedTestCount
                    : Math.Max(_summary.ExecutedTests, CountTests(result.Test));
            if (_summary.FailedTests > 0)
            {
                _summary.Status = TestRunStatus.Failed;
            }
            else if (_summary.PassedTests > 0)
            {
                _summary.Status = TestRunStatus.Passed;
            }
            else
            {
                _summary.Status = TestRunStatus.Unknown;
            }

            UnityCopilotPluginEditor.Instance.LogInfo("Run {testMode} finished with {totalTests} test results. Result status: {status}",
                typeof(TestResultCollector), TestModeAsString, _summary.TotalTests, result.TestStatus);
            UnityCopilotPluginEditor.Instance.LogInfo("Final duration: {duration:mm\\:ss\\.fff}. Completed: {completed}/{total}",
                typeof(TestResultCollector), duration, _results.Count, _summary.TotalTests);

            UnityCopilotPluginEditor.Instance.BuildUcoPluginIfNeeded();

            if (!EnvironmentUtils.IsCi() && !EnvironmentUtils.IsBatchMode())
                UnityCopilotPluginEditor.ConnectIfNeeded();

            var structuredResponse = CreateStructuredResponse(
                includePassingTests: ownership?.IncludePassingTests ?? IncludePassingTests.Value,
                includeMessage: ownership?.IncludeMessage ?? IncludeMessage.Value,
                includeLogs: ownership?.IncludeLogs ?? IncludeLogs.Value,
                includeLogsMinLevel: ownership?.IncludeLogsMinLevel ?? IncludeLogsMinLevel.Value,
                includeMessageStacktrace: ownership?.IncludeMessageStacktrace ?? IncludeMessageStacktrace.Value,
                includeLogsStacktrace: ownership?.IncludeLogsStacktrace ?? IncludeLogsStacktrace.Value);

            // Scene hygiene (COCli-06): restore a sandboxed run and report the
            // observed mutation against the persisted pre-run baseline.
            var sceneOutcome = TestRunSceneSandbox.RestoreForOperation(ownership?.OperationId);
            structuredResponse.Mutated = sceneOutcome.Mutation.Mutated;
            structuredResponse.MutatedScenes = sceneOutcome.Mutation.MutatedScenes;
            structuredResponse.SandboxRestored = sceneOutcome.SandboxRestored;
            structuredResponse.SandboxRestoreCause = sceneOutcome.SandboxRestoreCause;

            var resultJson = JsonSerializer.Serialize(structuredResponse);
            var completedOwnedOperation = _callbackOwnership.CompleteFromRunFinished(
                ownership, _summary.FailedTests, _summary.ExecutedTests, resultJson);

            var requestId = ownership?.RequestId ?? string.Empty;
            if (completedOwnedOperation && string.IsNullOrEmpty(requestId) == false)
            {
                var ucoPlugin = UnityCopilotPluginEditor.Instance.UcoPluginInstance ?? throw new InvalidOperationException("Uco Plugin instance is not available.");

                var response = ResponseCallValueTool<TestRunResponse>
                    .SuccessStructured(ucoPlugin.UcoManager.Reflector.JsonSerializer.SerializeToNode(structuredResponse))
                    .SetRequestID(requestId);

                _ = UnityCopilotPluginEditor.NotifyToolRequestCompleted(new RequestToolCompletedData
                {
                    RequestId = requestId,
                    OperationId = ownership?.OperationId,
                    Result = response
                });
            }
        }

        internal static bool CompleteOperationFromCallback(
            string operationId,
            int failedTests,
            int executedTests,
            string resultJson)
        {
            var operation = EditorOperationRegistry.Get(operationId);
            if (operation == null || operation.IsTerminal)
                return false;

            if (operation.CancellationRequested)
                EditorOperationRegistry.CancelRunning(operationId, "test-run-cancelled", resultJson);
            else if (failedTests > 0)
                EditorOperationRegistry.Fail(operationId, "tests-failed",
                    $"{failedTests} test(s) failed.", "completed-with-failures", resultJson);
            else if (executedTests == 0)
                EditorOperationRegistry.Fail(operationId, "tests-no-results",
                    "Unity Test Runner completed without executing a matched test.", "completed-without-results", resultJson);
            else
                EditorOperationRegistry.Succeed(operationId, resultJson);
            return true;
        }

        public void TestStarted(ITestAdaptor test)
        {
            // Test started - could log this if needed
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            // Only count actual tests, not test suites
            if (!result.Test.IsSuite)
            {
                var testResult = new TestResultData
                {
                    Name = result.Test.FullName,
                    Status = ConvertTestStatus(result.TestStatus),
                    Duration = TimeSpan.FromSeconds(result.Duration),
                    Message = result.Message,
                    StackTrace = result.StackTrace
                };

                _results.Add(testResult);

                var statusEmoji = result.TestStatus switch
                {
                    TestStatus.Passed => "<color=green>✅</color>",
                    TestStatus.Failed => "<color=red>❌</color>",
                    TestStatus.Skipped => "<color=yellow>⚠️</color>",
                    _ => string.Empty
                };

                UnityCopilotPluginEditor.Instance.LogInfo("{emoji} Test finished ({counter}/{total}): {testName} - {testStatus}",
                    typeof(TestResultCollector), statusEmoji, _results.Count, _summary.TotalTests, result.Test.FullName, result.TestStatus);

                // Update summary counts
                switch (result.TestStatus)
                {
                    case TestStatus.Passed:
                        _summary.PassedTests++;
                        break;
                    case TestStatus.Failed:
                        _summary.FailedTests++;
                        break;
                    case TestStatus.Skipped:
                        _summary.SkippedTests++;
                        break;
                }

                // Update duration as tests complete
                _summary.Duration = DateTime.Now - startTime;
                _summary.ExecutedTests = _results.Count;

                _callbackOwnership.UpdateFromTestFinished(
                    _results.Count, _summary.MatchedTests);

                // Check if all tests are complete
                if (_results.Count >= _summary.TotalTests)
                {
                    UnityCopilotPluginEditor.Instance.LogInfo("All tests completed via TestFinished. Final duration: {duration:mm\\:ss\\.fff}",
                        typeof(TestResultCollector), _summary.Duration);
                }
            }
        }

        void OnLogMessageReceived(string condition, string stackTrace, LogType type)
        {
            var entry = new TestLogEntry(type, condition, stackTrace);
            lock (_logsMutex)
            {
                _logs.Add(entry);
            }
        }

        TestRunResponse CreateStructuredResponse(
            bool includePassingTests,
            bool includeMessage,
            bool includeMessageStacktrace,
            bool includeLogs,
            int includeLogsMinLevel,
            bool includeLogsStacktrace)
        {
            var results = GetResults();
            var summary = GetSummary();
            var logs = GetLogs();

            var response = new TestRunResponse
            {
                Summary = summary,
                Results = new List<TestResultData>()
            };

            // Filter test results based on includePassingTests, includeMessage and includeMessageStacktrace
            foreach (var result in results)
            {
                // Skip passing tests if includePassingTests is false
                if (!includePassingTests && result.Status == TestResultStatus.Passed)
                    continue;

                var filteredResult = new TestResultData
                {
                    Name = result.Name,
                    Status = result.Status,
                    Duration = result.Duration,
                    Message = includeMessage ? result.Message : null,
                    StackTrace = includeMessageStacktrace ? result.StackTrace : null
                };
                response.Results.Add(filteredResult);
            }

            // Include logs if requested
            if (includeLogs && logs.Any())
            {
                var minLogLevel = TestLogEntry.ToLogLevel((LogType)includeLogsMinLevel);
                response.Logs = logs
                    .Where(log => log.LogLevel >= minLogLevel)
                    .Select(log => includeLogsStacktrace
                        ? log
                        : new TestLogEntry(log.Type, log.Condition, null, log.Timestamp))
                    .ToList();
            }

            return response;
        }

        public static int CountTests(ITestAdaptor test)
        {
            try
            {
                if (test == null)
                    return 0;

                if (test.HasChildren && test.Children != null)
                    return test.Children.Sum(CountTests);

                return test.IsSuite ? 0 : 1;
            }
            catch
            {
                return 0;
            }
        }

        static TestResultStatus ConvertTestStatus(TestStatus testStatus)
        {
            return testStatus switch
            {
                TestStatus.Passed => TestResultStatus.Passed,
                TestStatus.Failed => TestResultStatus.Failed,
                TestStatus.Skipped => TestResultStatus.Skipped,
                _ => TestResultStatus.Skipped
            };
        }
    }
}
