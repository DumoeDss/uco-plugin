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
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using com.IvanMurzak.ReflectorNet.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    public class BaseTest
    {
        protected Microsoft.Extensions.Logging.ILogger _logger = null!;

        [UnitySetUp]
        public virtual IEnumerator SetUp()
        {
            Debug.Log($"[{GetType().GetTypeShortName()}] SetUp");

            UnityCopilotPluginEditor.InitSingletonIfNeeded();

            _logger = UnityLoggerFactory.LoggerFactory.CreateLogger("Tests");

            yield return null;
        }
        [UnityTearDown]
        public virtual IEnumerator TearDown()
        {
            Debug.Log($"[{GetType().GetTypeShortName()}] TearDown");

            DestroyAllGameObjectsInActiveScene();

            yield return null;
        }

        protected static void DestroyAllGameObjectsInActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            foreach (var go in scene.GetRootGameObjects())
                UnityEngine.Object.DestroyImmediate(go);
        }

        static RequestCallTool BuildRequest(string toolName, string json)
        {
            var parameters = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            return new RequestCallTool(toolName, parameters!);
        }

        static string RenderResult(ResponseData<ResponseCallTool> result)
            => result.ToJson(UnityCopilotPluginEditor.Instance.Reflector)!;

        static void AssertToolSucceeded(ResponseData<ResponseCallTool> result, string jsonResult)
        {
            Assert.IsFalse(result.Status == ResponseStatus.Error, $"Tool call failed with error status: {result.Message}");
            Assert.IsNotNull(result.Message, $"Tool call returned null message");
            Assert.IsFalse(result.Message!.Contains("[Error]"), $"Tool call failed with error: {result.Message}");
            Assert.IsNotNull(result.Value, $"Tool call returned null value");
            Assert.IsFalse(result.Value!.Status == ResponseStatus.Error, $"Tool call failed");
            Assert.IsFalse(jsonResult.Contains("[Error]"), $"Tool call failed with error in JSON: {jsonResult}");
            Assert.IsFalse(jsonResult.Contains("[Warning]"), $"Tool call contains warnings in JSON: {jsonResult}");
        }

        private (ResponseData<ResponseCallTool> result, string json) CallToolInternal(string toolName, string json)
        {
            Debug.Log($"{toolName} Started with JSON:\n{json}");
            AllowPolicyRejectionLogs();
            var result = RunToolThroughPolicyAsync(BuildRequest(toolName, json)).Result;
            var jsonResult = RenderResult(result);
            Debug.Log($"{toolName} Completed. Result:\n{jsonResult}");
            return (result, jsonResult);
        }

        /// <summary>
        /// Runs a tool request through the production manager. Tool-behaviour
        /// tests use the same two-step controlled flow as clients: the first
        /// call issues a confirmation record without invoking the runner, and
        /// the exact retry consumes that record once.
        /// </summary>
        public static async Task<ResponseData<ResponseCallTool>> RunToolThroughPolicyAsync(RequestCallTool request)
        {
            var tools = UnityCopilotPluginEditor.Instance.Tools!;
            var control = request.Control?.Clone() ?? new ToolCallControl
            {
                Version = ToolCallControl.CurrentVersion,
                CallId = request.RequestID + "-call",
                CorrelationId = request.RequestID + "-trace",
            };
            control.DryRun = null;
            control.Confirm = null;
            control.Confirmation = null;

            var controlled = new RequestCallTool(
                request.RequestID,
                request.Name,
                request.Arguments,
                control);
            var response = await tools.RunCallTool(controlled).ConfigureAwait(false);
            if (!IsConfirmationRequired(response))
                return response;

            var plan = ReadConfirmationPlan(response);
            if (plan == null)
                return response;

            var executeControl = control.Clone();
            executeControl.Confirm = true;
            executeControl.Confirmation = new ToolCallConfirmation
            {
                PlanId = plan["planId"]?.GetValue<string>(),
                PlanHash = plan["planHash"]?.GetValue<string>(),
                ExpiresAtUnixMs = plan["expiresAtUnixMs"]?.GetValue<long>(),
            };
            return await tools.RunCallTool(new RequestCallTool(
                request.RequestID,
                request.Name,
                request.Arguments,
                executeControl)).ConfigureAwait(false);
        }

        private static System.Text.Json.Nodes.JsonObject? ReadConfirmationPlan(
            ResponseData<ResponseCallTool> response)
        {
            var details = response.StructuredError?.Details
                ?? response.Value?.StructuredError?.Details;
            return details?["confirmationPlan"] as System.Text.Json.Nodes.JsonObject;
        }

        private static bool IsConfirmationRequired(ResponseData<ResponseCallTool>? response)
        {
            var code = response?.StructuredError?.Code ?? response?.Value?.StructuredError?.Code;
            return string.Equals(code, ToolCallErrorCodes.ConfirmationRequired, StringComparison.Ordinal);
        }

        /// <summary>
        /// The manager logs every policy rejection ("Error Response to AI")
        /// as an error, and the plan/confirm flow driven by these helpers
        /// starts with exactly such a rejection. Unity resets this flag
        /// between SetUp and the test body, so it is set immediately before
        /// each helper-driven tool call. Explicit <c>LogAssert.Expect</c>
        /// calls in a test keep working: only unexpected error logs are
        /// tolerated; response/state assertions are unchanged.
        /// </summary>
        public static void AllowPolicyRejectionLogs()
            => LogAssert.ignoreFailingMessages = true;

        /// <summary>
        /// g-005 no-bypass: pilot Scene/GameObject/Component tool bodies refuse
        /// to mutate outside an authoring transaction. Tests that call a tool
        /// method directly (to assert reflected patch semantics, not policy)
        /// open one explicit test transaction around the call, exactly as the
        /// safety middleware does for a policy-approved invocation.
        /// </summary>
        protected static IDisposable BeginTestAuthoringTransaction(string label = "test: direct tool invocation")
            => com.AtelierAI.Unity.Copilot.Editor.Utils.UnityAuthoringTransactionFactory.BeginStandaloneScope(label);

        /// <summary>
        /// Starts a tool call from a background thread via <see cref="Task.Run(Action)"/>.
        /// Returns a Task that resolves to the raw tool response + rendered JSON.
        /// The caller must pump Unity's main-thread loop (e.g. by yielding <c>null</c> in a
        /// <c>[UnityTest]</c> coroutine) until the task completes so that any
        /// <see cref="MainThread.Instance.Run"/> dispatches posted via
        /// <c>EditorApplication.update</c> can actually execute.
        /// </summary>
        private static Task<(ResponseData<ResponseCallTool> result, string json)> CallToolFromBackgroundThreadInternal(string toolName, string json)
        {
            Debug.Log($"{toolName} Started (background thread) with JSON:\n{json}");
            AllowPolicyRejectionLogs();
            var request = BuildRequest(toolName, json);
            return Task.Run(async () =>
            {
                Assert.IsFalse(MainThread.Instance.IsMainThread,
                    "Task.Run should schedule work onto the thread pool, not the Unity main thread.");

                var result = await RunToolThroughPolicyAsync(request).ConfigureAwait(false);
                return (result, RenderResult(result));
            });
        }

        /// <summary>
        /// Cooperative main-thread runner: invokes <c>RunCallTool</c> on the current
        /// (main) thread but polls for completion via coroutine yield so Unity can keep
        /// ticking <c>EditorApplication.update</c>. This is essential for tools that
        /// internally <c>await Task.Yield()</c> on the main-thread sync context
        /// (e.g. <c>package-list</c>, <c>package-search</c>, <c>scene-unload</c>) —
        /// a blocking <c>task.Result</c> on the main thread would deadlock because the
        /// awaited continuation is scheduled back onto the blocked thread.
        /// </summary>
        private static IEnumerator CallToolOnMainThreadCoop(string toolName, string json, Action<ResponseData<ResponseCallTool>, string> onComplete)
        {
            Debug.Log($"{toolName} Started (main thread coop) with JSON:\n{json}");
            AllowPolicyRejectionLogs();
            var task = RunToolThroughPolicyAsync(BuildRequest(toolName, json));
            yield return WaitForTask(task);

            var result = task.Result;
            var jsonResult = RenderResult(result);
            Debug.Log($"{toolName} Completed (main thread coop). Result:\n{jsonResult}");
            onComplete(result, jsonResult);
        }

        /// <summary>
        /// Yields the coroutine until the provided task is complete, allowing Unity's
        /// main thread to keep ticking (so background-thread tool calls that dispatch
        /// work back to the main thread can make progress).
        /// </summary>
        protected static IEnumerator WaitForTask(Task task, float timeoutSeconds = 30f)
        {
            var startTime = Time.realtimeSinceStartup;
            while (!task.IsCompleted)
            {
                if (Time.realtimeSinceStartup - startTime > timeoutSeconds)
                    throw new TimeoutException($"Task did not complete within {timeoutSeconds} seconds.");
                yield return null;
            }
            if (task.IsFaulted)
                throw task.Exception!;
        }

        /// <summary>
        /// Calls a tool from a background thread and asserts the result is a success.
        /// Must be used inside a <c>[UnityTest]</c> coroutine via <c>yield return</c>
        /// so Unity's main-thread loop keeps running.
        /// </summary>
        protected IEnumerator RunToolFromBackgroundThread(string toolName, string json)
        {
            var task = CallToolFromBackgroundThreadInternal(toolName, json);
            yield return WaitForTask(task);

            var (result, jsonResult) = task.Result;
            Debug.Log($"{toolName} Completed (background thread). Result:\n{jsonResult}");
            AssertToolSucceeded(result, jsonResult);
        }

        /// <summary>
        /// Cooperative main-thread invocation that asserts the tool succeeded.
        /// See <see cref="CallToolOnMainThreadCoop"/> for why this is coroutine-based.
        /// </summary>
        protected IEnumerator RunToolMainThreadCoop(string toolName, string json)
            => CallToolOnMainThreadCoop(toolName, json, AssertToolSucceeded);

        /// <summary>
        /// Thread-safety smoke test: invokes the tool from both the main thread
        /// (cooperatively) and a background thread and only asserts the absence of a
        /// Unity main-thread violation. Business-level errors (invalid input, unsupported
        /// state, etc.) are tolerated — this helper is for tools where crafting a full
        /// happy-path input is impractical, but we still want to prove the dispatcher /
        /// body does not throw
        /// <c>UnityException: ... can only be called from the main thread</c>.
        /// </summary>
        protected IEnumerator RunToolExpectNoThreadViolation(string toolName, string json)
        {
            const string MainThreadViolationSnippet = "can only be called from the main thread";

            // Tolerate any business-level Debug.LogError surfaced by the tool body
            // (missing camera, invalid ref, unsupported state, etc.) — we are only
            // checking for main-thread violations here. Re-asserted at every helper
            // call because Unity's test framework resets this between SetUp and the
            // test body.
            string? mainJson = null;
            yield return CallToolOnMainThreadCoop(toolName, json, (_, j) => mainJson = j);
            Assert.IsNotNull(mainJson, $"[{toolName}] main-thread call produced no JSON.");
            Assert.IsFalse(mainJson!.Contains(MainThreadViolationSnippet),
                $"[{toolName}] main-thread call surfaced a thread-safety error:\n{mainJson}");

            var bgTask = CallToolFromBackgroundThreadInternal(toolName, json);
            yield return WaitForTask(bgTask);
            var bgJson = bgTask.Result.json;
            Assert.IsFalse(bgJson.Contains(MainThreadViolationSnippet),
                $"[{toolName}] background-thread call surfaced a thread-safety error:\n{bgJson}");
        }

        /// <summary>
        /// Happy-path thread coverage: asserts that the tool succeeds on both the main
        /// thread (via cooperative coroutine) and a background thread.
        /// <paramref name="mainJson"/> is used for the main-thread call and
        /// <paramref name="backgroundJson"/> (defaulting to <paramref name="mainJson"/>)
        /// for the background-thread call, so tests can target different entities when
        /// the main-thread call mutated state.
        /// </summary>
        protected IEnumerator RunToolBothThreads(string toolName, string mainJson, string? backgroundJson = null)
        {
            yield return RunToolMainThreadCoop(toolName, mainJson);
            yield return RunToolFromBackgroundThread(toolName, backgroundJson ?? mainJson);
        }

        /// <summary>
        /// Calls a tool and returns the raw JSON result string without asserting success.
        /// Useful for testing error responses.
        /// </summary>
        protected virtual string RunToolRaw(string toolName, string json)
        {
            var (result, jsonResult) = CallToolInternal(toolName, json);
            return jsonResult;
        }

        protected virtual ResponseData<ResponseCallTool> RunTool(string toolName, string json)
        {
            var (result, jsonResult) = CallToolInternal(toolName, json);
            AssertToolSucceeded(result, jsonResult);
            return result;
        }
    }
}
