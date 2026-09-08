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
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.AtelierAI.Unity.Copilot.Editor.API.TestRunner;
using com.AtelierAI.Unity.Copilot.Editor.Utils;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace com.AtelierAI.Unity.Copilot.Editor.API
{
    [McpPluginToolType]
    [InitializeOnLoad]
    public static partial class Tool_Tests
    {
        static readonly object _lock = new();
        static volatile TestRunnerApi? _testRunnerApi = null!;
        static volatile TestResultCollector? _resultCollector = null!;
        static volatile bool _callbacksRegistered = false;

        // SessionState keys for persisting pending test run across domain reload
        const string PendingTestRunKey = "MCP_PendingTestRun";
        const string PendingTestModeKey = "MCP_PendingTestRun_TestMode";
        const string PendingTestAssemblyKey = "MCP_PendingTestRun_TestAssembly";
        const string PendingTestNamespaceKey = "MCP_PendingTestRun_TestNamespace";
        const string PendingTestClassKey = "MCP_PendingTestRun_TestClass";
        const string PendingTestMethodKey = "MCP_PendingTestRun_TestMethod";
        const string PendingTestOperationIdKey = "MCP_PendingTestRun_OperationId";

        static Tool_Tests()
        {
            _testRunnerApi ??= CreateInstance();
            // g-006 owner registration performs every reload claim and only schedules
            // resume after persisted state validates and the scheduler lease is reacquired.
        }

        public static TestRunnerApi TestRunnerApi
        {
            get
            {
                lock (_lock)
                {
                    if (_testRunnerApi == null)
                        _testRunnerApi = CreateInstance();
                    return _testRunnerApi;
                }
            }
        }
        public static TestRunnerApi CreateInstance()
        {
            if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Trace))
                Debug.Log($"[{nameof(TestRunnerApi)}] Creating new instance. Existing API: {_testRunnerApi != null}, Existing Collector: {_resultCollector != null}, Callbacks Registered: {_callbacksRegistered}");

            _resultCollector ??= new TestResultCollector();
            var testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

            // Only register callbacks once globally to prevent accumulation
            // Unity's TestRunnerApi maintains a static callback list, so multiple RegisterCallbacks calls add duplicates
            if (!_callbacksRegistered)
            {
                if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Trace))
                    Debug.Log($"[{nameof(TestRunnerApi)}] Registering callbacks for the first (and only) time.");

                testRunnerApi.RegisterCallbacks(_resultCollector);
                _callbacksRegistered = true;
            }
            else
            {
                if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Trace))
                    Debug.LogWarning($"[{nameof(TestRunnerApi)}] Callbacks already registered globally - skipping registration.");
            }

            return testRunnerApi;
        }

        public static void Init()
        {
            // none
        }

        internal static string CurrentTestOperationId
        {
            get => TestResultCollector.TestOperationId.Value;
            set => TestResultCollector.TestOperationId.Value = value;
        }

        internal static string CurrentTestRequestId
        {
            get => TestResultCollector.TestCallRequestID.Value;
            set => TestResultCollector.TestCallRequestID.Value = value;
        }

        internal static bool HasPendingTestRun()
            => !string.IsNullOrEmpty(SessionState.GetString(PendingTestRunKey, string.Empty));

        internal static void SavePendingTestRun(string operationId, TestMode testMode, string? testAssembly, string? testNamespace, string? testClass, string? testMethod)
        {
            SessionState.SetString(PendingTestRunKey, "pending");
            SessionState.SetString(PendingTestOperationIdKey, operationId);
            SessionState.SetInt(PendingTestModeKey, (int)testMode);
            SessionState.SetString(PendingTestAssemblyKey, testAssembly ?? string.Empty);
            SessionState.SetString(PendingTestNamespaceKey, testNamespace ?? string.Empty);
            SessionState.SetString(PendingTestClassKey, testClass ?? string.Empty);
            SessionState.SetString(PendingTestMethodKey, testMethod ?? string.Empty);
        }

        internal static void ClearPendingTestRun()
        {
            SessionState.EraseString(PendingTestRunKey);
            SessionState.EraseInt(PendingTestModeKey);
            SessionState.EraseString(PendingTestAssemblyKey);
            SessionState.EraseString(PendingTestNamespaceKey);
            SessionState.EraseString(PendingTestClassKey);
            SessionState.EraseString(PendingTestMethodKey);
            SessionState.EraseString(PendingTestOperationIdKey);
        }

        internal static bool TryClaimPendingTestRun(out string operationId)
        {
            operationId = SessionState.GetString(PendingTestOperationIdKey, string.Empty);
            var operation = EditorOperationRegistry.Get(operationId);
            if (string.IsNullOrEmpty(operationId) || operation == null || operation.IsTerminal)
            {
                ClearTestRunOwnership(operationId);
                return false;
            }

            EditorOperationRegistry.Claim(operationId, "waiting-for-compilation");
            return true;
        }

        internal static void ClearTestRunOwnership(string operationId)
        {
            if (string.IsNullOrEmpty(operationId))
            {
                ClearPendingTestRun();
                return;
            }

            // Safety net for abnormal terminations (timeout, interrupted reload)
            // whose completion callback never runs: reclaim any persisted
            // sandbox context so the Editor does not keep a sandbox scene open.
            // Idempotent — the normal RunFinished path already consumed it.
            _ = TestRunSceneSandbox.RestoreForOperation(operationId);

            if (string.Equals(SessionState.GetString(PendingTestOperationIdKey, string.Empty),
                    operationId, StringComparison.Ordinal))
            {
                ClearPendingTestRun();
                EditorApplication.update -= ResumePendingTestRunOnce;
            }

            if (!string.Equals(TestResultCollector.TestOperationId.Value,
                    operationId, StringComparison.Ordinal))
                return;

            TestResultCollector.TestOperationId.Value = string.Empty;
            TestResultCollector.TestCallRequestID.Value = string.Empty;
            TestResultCollector.ExpectedDiscoveredTests.Value = 0;
            TestResultCollector.ExpectedMatchedTests.Value = 0;
        }

        internal static void ResumePendingTestRunOnce()
        {
            // If still compiling, wait for next update tick
            if (EditorApplication.isCompiling)
                return;

            // Compilation finished (or was never happening), unsubscribe
            EditorApplication.update -= ResumePendingTestRunOnce;

            if (!TryClaimPendingTestRun(out var operationId))
                return;

            var requestId = TestResultCollector.TestCallRequestID.Value;

            // Check for compilation failure
            if (EditorUtility.scriptCompilationFailed)
            {
                ClearPendingTestRun();
                TestResultCollector.TestCallRequestID.Value = string.Empty;
                TestResultCollector.TestOperationId.Value = string.Empty;

                var errorDetails = ScriptUtils.GetCompilationErrorDetails();
                if (!string.IsNullOrEmpty(operationId))
                    EditorOperationRegistry.Fail(operationId, "test-compilation-failed", errorDetails, "compilation-failed");
                var response = ResponseCallValueTool<TestRunResponse>
                    .Error($"Cannot run tests: compilation errors after script recompilation.\n\n{errorDetails}")
                    .SetRequestID(requestId);

                _ = UnityCopilotPluginEditor.NotifyToolRequestCompleted(new RequestToolCompletedData
                {
                    RequestId = requestId,
                    OperationId = operationId,
                    Result = response
                });
                return;
            }

            // Compilation succeeded — load filter params from SessionState and run tests
            var testMode = (TestMode)SessionState.GetInt(PendingTestModeKey, (int)TestMode.EditMode);
            var testAssembly = SessionState.GetString(PendingTestAssemblyKey, string.Empty);
            var testNamespace = SessionState.GetString(PendingTestNamespaceKey, string.Empty);
            var testClass = SessionState.GetString(PendingTestClassKey, string.Empty);
            var testMethod = SessionState.GetString(PendingTestMethodKey, string.Empty);

            ClearPendingTestRun();

            // Normalize empty strings to null
            if (string.IsNullOrEmpty(testAssembly)) testAssembly = null;
            if (string.IsNullOrEmpty(testNamespace)) testNamespace = null;
            if (string.IsNullOrEmpty(testClass)) testClass = null;
            if (string.IsNullOrEmpty(testMethod)) testMethod = null;

            var filterParams = new TestFilterParameters(testAssembly, testNamespace, testClass, testMethod);

            if (UnityCopilotPlugin.IsLogEnabled(LogLevel.Info))
                Debug.Log($"[TestRunner] Resuming test run after recompilation. Mode: {testMode}, Filters: {filterParams}");

            _ = ResumePendingTestRunAsync(operationId, requestId, testMode, filterParams);
        }

        static async Task ResumePendingTestRunAsync(
            string operationId,
            string requestId,
            TestMode testMode,
            TestFilterParameters filterParams)
        {
            try
            {
                var operation = EditorOperationRegistry.Get(operationId);
                if (operation == null || operation.IsTerminal)
                {
                    ClearTestRunOwnership(operationId);
                    return;
                }
                TestOperationMetadata? metadata = null;
                try
                {
                    if (!string.IsNullOrEmpty(operation?.MetadataJson))
                        metadata = JsonUtility.FromJson<TestOperationMetadata>(operation.MetadataJson);
                }
                catch { }
                var wait = TimeSpan.FromSeconds(metadata?.WaitTimeoutSeconds
                    ?? DefaultWaitTimeoutSeconds);
                var discovery = await DiscoverMatchingTests(TestRunnerApi, testMode, filterParams, wait);
                operation = EditorOperationRegistry.Get(operationId);
                if (operation == null || operation.IsTerminal)
                {
                    ClearTestRunOwnership(operationId);
                    return;
                }
                if (discovery.MatchedNames.Length == 0)
                {
                    var message = Error.NoTestsFound(filterParams);
                    EditorOperationRegistry.Fail(operationId, "tests-no-match", message, "filter-validation");
                    TestResultCollector.TestOperationId.Value = string.Empty;
                    TestResultCollector.TestCallRequestID.Value = string.Empty;
                    return;
                }

                StartDiscoveredTests(operationId, testMode, filterParams, discovery,
                    metadata?.SandboxScene ?? false);
            }
            catch (Exception ex)
            {
                var operation = EditorOperationRegistry.Get(operationId);
                if (operation != null && !operation.IsTerminal)
                    EditorOperationRegistry.Fail(operationId, "tests-resume-failed", ex.Message, "resume-failed");
                ClearTestRunOwnership(operationId);
            }
        }

        /// <summary>
        /// Throws <see cref="InvalidOperationException"/> if any currently open scene has unsaved
        /// changes (<see cref="Scene.isDirty"/>). The exception message lists every dirty scene's
        /// name and path so the caller (LLM agent or human) can save them and retry.
        ///
        /// Running tests while a scene is dirty is unsafe: Unity may reload the scene when
        /// entering play mode, silently discarding the unsaved edits and producing a test run
        /// against a scene state that does not match either the in-memory scene or the asset
        /// on disk. This check aborts before any state is mutated (no PlayerPrefs, no
        /// AssetDatabase.Refresh, no domain reload is triggered).
        ///
        /// MUST be called on the Unity main thread.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown if one or more open scenes have unsaved changes.
        /// </exception>
        internal static void ThrowIfAnyOpenSceneIsDirty()
        {
            var dirtyScenes = GetDirtyOpenScenes();
            if (dirtyScenes.Count == 0)
                return;

            throw new InvalidOperationException(FormatDirtyScenesMessage(dirtyScenes));
        }

        /// <summary>
        /// Returns every open scene whose <see cref="Scene.isDirty"/> is true.
        /// MUST be called on the Unity main thread.
        /// </summary>
        internal static List<Scene> GetDirtyOpenScenes()
        {
            var result = new List<Scene>(capacity: EditorSceneManager.sceneCount);
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isDirty)
                    result.Add(scene);
            }
            return result;
        }

        internal static string FormatDirtyScenesMessage(IReadOnlyList<Scene> dirtyScenes)
        {
            var details = string.Join(", ", dirtyScenes.Select(FormatSceneForMessage));
            return $"Cannot run tests: {dirtyScenes.Count} open scene(s) have unsaved changes: {details}. " +
                "Save the scene(s) and try again.";
        }

        static string FormatSceneForMessage(Scene scene)
        {
            var name = string.IsNullOrEmpty(scene.name) ? "(untitled)" : scene.name;
            var path = string.IsNullOrEmpty(scene.path) ? "(unsaved)" : scene.path;
            return $"'{name}' ({path})";
        }

        private static class Error
        {
            public static string InvalidTestMode(string testMode)
                => $"Invalid test mode '{testMode}'. Valid modes: EditMode, PlayMode, All";

            public static string TestExecutionFailed(string reason)
                => $"Test execution failed: {reason}";

            public static string TestTimeout(int timeoutMs)
                => $"Test execution timed out after {timeoutMs} ms";

            public static string NoTestsFound(TestFilterParameters filterParams)
            {
                var filters = new List<string>();

                if (!string.IsNullOrEmpty(filterParams.TestAssembly)) filters.Add($"assembly '{filterParams.TestAssembly}'");
                if (!string.IsNullOrEmpty(filterParams.TestNamespace)) filters.Add($"namespace '{filterParams.TestNamespace}'");
                if (!string.IsNullOrEmpty(filterParams.TestClass)) filters.Add($"class '{filterParams.TestClass}'");
                if (!string.IsNullOrEmpty(filterParams.TestMethod)) filters.Add($"method '{filterParams.TestMethod}'");

                var filterText = filters.Count > 0
                    ? $" matching {string.Join(", ", filters)}"
                    : string.Empty;

                return $"No tests found{filterText}. Please check that the specified assembly, namespace, class, and method names are correct and that your Unity project contains tests.";
            }
        }
    }
}
