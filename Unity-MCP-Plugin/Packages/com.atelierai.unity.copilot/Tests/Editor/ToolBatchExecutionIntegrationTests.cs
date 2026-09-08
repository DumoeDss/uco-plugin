#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Unity.Copilot.Editor.API;
using com.IvanMurzak.McpPlugin;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    [TestFixture]
    public sealed class ToolBatchExecutionIntegrationTests : BaseTest
    {
        private static readonly BindingFlags PrivateStatic =
            BindingFlags.NonPublic | BindingFlags.Static;

        [UnityTest]
        public IEnumerator Execute_UsesProductionDispatchWithChildContextsAndFailFast()
        {
            var originalInstance = UnityCopilotPluginEditor.Instance;
            originalInstance.BuildMcpPluginIfNeeded();
            var originalPlugin = originalInstance.McpPluginInstance;
            // Console separation (COCli-07): the replacement singleton has no
            // collector of its own; the still-installed diagnostics channel
            // sink belongs to the original instance's collector.
            var diagnostics = originalInstance.LogCollector;
            Assert.IsNotNull(diagnostics,
                "The plugin log collector must be installed to assert console-separated diagnostics.");
            var middleware = new RecordingMiddleware();
            var successfulRunner = new BatchTestRunner("batch-test-success", shouldFail: false, targetBound: false);
            var failingRunner = new BatchTestRunner("batch-test-failure", shouldFail: true, targetBound: true);
            var replacement = new TestUnityCopilotPluginEditor(
                middleware,
                successfulRunner,
                failingRunner);

            SetEditorSingleton(replacement);
            try
            {
                replacement.BuildMcpPluginIfNeeded();
                var manager = replacement.Tools;
                Assert.IsNotNull(manager, "The test plugin should expose a production tool manager.");

                // g-005: batch-execute carries DestructiveHint=true, so the
                // parent call must obtain a read-only plan and confirm it.
                // The plan short-circuits inside the outermost safety
                // middleware and therefore never reaches the recording
                // middleware or any child runner.
                var planning = manager!.RunCallTool(BuildBatchRequest(planControl: true, confirmation: null));
                yield return WaitForTask(planning);
                var planResponse = planning.Result;
                Assert.AreEqual(ResponseStatus.Success, planResponse.Status, planResponse.Message);
                var plan = planResponse.Value!.StructuredContent!["result"]!["confirmationPlan"]!.AsObject();
                var childRecords = plan["childRecords"]!.AsArray();
                Assert.AreEqual(3, childRecords.Count);
                Assert.AreEqual(0, middleware.Contexts.Count, "A plan must not reach later middleware.");
                Assert.AreEqual(0, successfulRunner.InvocationCount + failingRunner.InvocationCount,
                    "A plan must not invoke a child runner.");

                var request = BuildBatchRequest(planControl: false, confirmation: new ToolCallConfirmation
                {
                    PlanId = plan["planId"]!.GetValue<string>(),
                    PlanHash = plan["planHash"]!.GetValue<string>(),
                    ExpiresAtUnixMs = plan["expiresAtUnixMs"]!.GetValue<long>(),
                });
                var execution = manager!.RunCallTool(request);
                yield return WaitForTask(execution);

                var response = execution.Result;
                Assert.AreEqual(ResponseStatus.Success, response.Status, response.Message);
                Assert.IsNotNull(response.Value, "The production batch tool should return a tool response.");
                Assert.AreEqual(ResponseStatus.Success, response.Value!.Status);

                var batchResult = DeserializeBatchResult(response.Value);
                Assert.AreEqual(3, batchResult.TotalCommands);
                Assert.AreEqual(1, batchResult.Succeeded);
                Assert.AreEqual(1, batchResult.Failed);
                Assert.IsTrue(batchResult.Aborted);
                Assert.AreEqual(3, batchResult.Results.Length);
                Assert.IsTrue(batchResult.Results[0].Ok);
                Assert.IsNotNull(batchResult.Results[0].Transaction,
                    "A completed child's transaction report must survive a later runtime failure.");
                Assert.IsTrue(batchResult.Results[0].Transaction!["completed"]!.GetValue<bool>());
                Assert.AreEqual("none", batchResult.Results[0].Transaction!["undo"]!.GetValue<string>());
                Assert.IsFalse(batchResult.Results[1].Ok);
                Assert.IsFalse(batchResult.Results[1].Skipped);
                Assert.IsTrue(batchResult.Results[2].Skipped);
                Assert.IsFalse(batchResult.Results[2].Ok);

                // The first middleware invocation is the production batch
                // call. The next two are the child calls dispatched by the
                // production Tool_Batch.Execute implementation.
                Assert.AreEqual(3, middleware.Contexts.Count);
                var parent = middleware.Contexts[0];
                Assert.AreEqual("batch-parent", parent.CallId);
                Assert.AreEqual("batch-trace", parent.CorrelationId);
                Assert.IsNull(parent.ParentCallId);

                var firstChild = middleware.Contexts[1];
                var secondChild = middleware.Contexts[2];
                Assert.AreEqual("batch-trace", firstChild.CorrelationId);
                Assert.AreEqual("batch-trace", secondChild.CorrelationId);
                Assert.AreEqual("batch-parent", firstChild.ParentCallId);
                Assert.AreEqual("batch-parent", secondChild.ParentCallId);
                Assert.AreEqual(childRecords[0]!["requestID"]!.GetValue<string>(), firstChild.RequestID);
                Assert.AreEqual(childRecords[0]!["callId"]!.GetValue<string>(), firstChild.CallId);
                Assert.AreEqual(childRecords[1]!["requestID"]!.GetValue<string>(), secondChild.RequestID);
                Assert.AreEqual(childRecords[1]!["callId"]!.GetValue<string>(), secondChild.CallId);
                Assert.AreEqual(true, firstChild.Confirm);
                Assert.AreEqual(true, secondChild.Confirm);
                Assert.AreEqual(childRecords[0]!["planId"]!.GetValue<string>(), firstChild.Confirmation!.PlanId);
                Assert.AreEqual(childRecords[1]!["planId"]!.GetValue<string>(), secondChild.Confirmation!.PlanId,
                    "Execution must redeem the aggregate-issued token rather than mint a replacement.");
                Assert.AreEqual(childRecords[1]!["planHash"]!.GetValue<string>(), secondChild.Confirmation.PlanHash);
                Assert.AreEqual(childRecords[1]!["expiresAtUnixMs"]!.GetValue<long>(), secondChild.Confirmation.ExpiresAtUnixMs);
                Assert.AreEqual(1, successfulRunner.InvocationCount);
                Assert.AreEqual(1, failingRunner.InvocationCount);

                // Console separation (COCli-07): the failing child's
                // "Error Response to AI" log lives in the plugin diagnostics
                // channel, not the Unity Console — assert it there.
                AssertDiagnosticsContains(
                    UnityEngine.LogType.Error, "Intentional batch test failure", diagnostics);
            }
            finally
            {
                RestoreEditorSingleton(originalInstance, originalPlugin, replacement);
            }
        }

        [UnityTest]
        public IEnumerator Execute_RejectsTamperedApprovedChildBeforeAnyRunner()
        {
            var originalInstance = UnityCopilotPluginEditor.Instance;
            originalInstance.BuildMcpPluginIfNeeded();
            var originalPlugin = originalInstance.McpPluginInstance;
            var middleware = new RecordingMiddleware { TamperApprovedChild = true };
            var first = new BatchTestRunner("batch-test-success", shouldFail: false, targetBound: false);
            var second = new BatchTestRunner("batch-test-failure", shouldFail: true, targetBound: true);
            var replacement = new TestUnityCopilotPluginEditor(middleware, first, second);

            SetEditorSingleton(replacement);
            try
            {
                replacement.BuildMcpPluginIfNeeded();
                var manager = replacement.Tools;
                Assert.IsNotNull(manager);

                var planning = manager!.RunCallTool(BuildBatchRequest(planControl: true, confirmation: null));
                yield return WaitForTask(planning);
                var planResponse = planning.Result;
                Assert.AreEqual(ResponseStatus.Success, planResponse.Status, planResponse.Message);
                var plan = planResponse.Value!.StructuredContent!["result"]!["confirmationPlan"]!.AsObject();

                var execution = manager.RunCallTool(BuildBatchRequest(
                    planControl: false,
                    confirmation: new ToolCallConfirmation
                    {
                        PlanId = plan["planId"]!.GetValue<string>(),
                        PlanHash = plan["planHash"]!.GetValue<string>(),
                        ExpiresAtUnixMs = plan["expiresAtUnixMs"]!.GetValue<long>(),
                    }));
                yield return WaitForTask(execution);

                var response = execution.Result;
                Assert.AreEqual(ResponseStatus.Success, response.Status, response.Message);
                var batchResult = DeserializeBatchResult(response.Value!);
                Assert.IsTrue(batchResult.Aborted);
                Assert.AreEqual(0, batchResult.Succeeded);
                Assert.AreEqual(0, first.InvocationCount + second.InvocationCount,
                    "No child runner may start after an approved child binding changes.");
                Assert.AreEqual(ToolCallErrorCodes.ConfirmationStale, batchResult.Results[0].ErrorCode);
            }
            finally
            {
                RestoreEditorSingleton(originalInstance, originalPlugin, replacement);
            }
        }

        private static RequestCallTool BuildBatchRequest(bool planControl, ToolCallConfirmation? confirmation)
        {
            var commands = new[]
            {
                new Tool_Batch.BatchCommand
                {
                    Tool = "batch-test-success",
                    Params = new Dictionary<string, JsonElement>()
                },
                new Tool_Batch.BatchCommand
                {
                    Tool = "batch-test-failure",
                    Params = new Dictionary<string, JsonElement>()
                },
                new Tool_Batch.BatchCommand
                {
                    Tool = "batch-test-success",
                    Params = new Dictionary<string, JsonElement>()
                },
            };
            var arguments = new Dictionary<string, JsonElement>
            {
                ["commands"] = JsonSerializer.SerializeToElement(commands),
                ["failFast"] = JsonSerializer.SerializeToElement(true),
            };
            // The plan and the execution share requestID, callId, and
            // correlationId: the confirmation binding covers all of them.
            return new RequestCallTool(
                "batch-request",
                Tool_Batch.BatchExecuteToolId,
                arguments,
                new ToolCallControl
                {
                    Version = ToolCallControl.CurrentVersion,
                    CallId = "batch-parent",
                    CorrelationId = "batch-trace",
                    DryRun = planControl ? "plan" : null,
                    Confirm = confirmation == null ? (bool?)null : true,
                    Confirmation = confirmation,
                });
        }

        private static Tool_Batch.BatchResult DeserializeBatchResult(ResponseCallTool response)
        {
            Assert.IsNotNull(response.StructuredContent, "The batch response should contain structured content.");
            var resultNode = response.StructuredContent!["result"];
            Assert.IsNotNull(resultNode, "The structured response should contain a result member.");
            return JsonSerializer.Deserialize<Tool_Batch.BatchResult>(resultNode!.ToJsonString())
                ?? throw new AssertionException("The batch result could not be deserialized.");
        }

        private static void SetEditorSingleton(UnityCopilotPluginEditor replacement)
        {
            var instanceField = typeof(UnityCopilotPluginEditor).GetField("instance", PrivateStatic);
            Assert.IsNotNull(instanceField, "Editor singleton field was not found.");
            instanceField!.SetValue(null, replacement);
        }

        private static void RestoreEditorSingleton(
            UnityCopilotPluginEditor original,
            IMcpPlugin? originalPlugin,
            TestUnityCopilotPluginEditor replacement)
        {
            replacement.DisposeMcpPluginInstance();
            replacement.Dispose();

            var instanceField = typeof(UnityCopilotPluginEditor).GetField("instance", PrivateStatic);
            Assert.IsNotNull(instanceField, "Editor singleton field was not found.");
            instanceField!.SetValue(null, original);

            var setCurrentPlugin = typeof(UnityCopilotPluginEditor).GetMethod(
                "SetCurrentPlugin",
                PrivateStatic);
            Assert.IsNotNull(setCurrentPlugin, "Editor current-plugin setter was not found.");
            setCurrentPlugin!.Invoke(null, new object?[] { originalPlugin });
        }

        private sealed class TestUnityCopilotPluginEditor : UnityCopilotPluginEditor
        {
            private readonly RecordingMiddleware _middleware;
            private readonly IReadOnlyList<IRunTool> _runners;

            public TestUnityCopilotPluginEditor(
                RecordingMiddleware middleware,
                params IRunTool[] runners)
            {
                _middleware = middleware;
                _runners = runners;
                ConnectionConfigForTests.KeepConnected = false;
            }

            protected override IMcpPlugin BuildMcpPlugin(
                com.IvanMurzak.McpPlugin.Common.Version version,
                Reflector reflector,
                ILoggerProvider? loggerProvider = null,
                Action<IMcpPluginBuilder>? configure = null)
            {
                return base.BuildMcpPlugin(
                    version,
                    reflector,
                    loggerProvider,
                    builder =>
                    {
                        configure?.Invoke(builder);
                        builder.AddToolExecutionMiddleware(_middleware);
                        foreach (var runner in _runners)
                            builder.AddTool(runner.Name, runner);
                    });
            }
        }

        private sealed class RecordingMiddleware : IToolExecutionMiddleware
        {
            public List<ToolCallContext> Contexts { get; } = new();
            public bool TamperApprovedChild { get; set; }

            public async Task<ResponseCallTool> InvokeAsync(
                ToolCallContext context,
                ToolCallNext next)
            {
                Contexts.Add(context.Clone());
                var invocation = ToolCallInvocationScope.CurrentInvocation;
                if (TamperApprovedChild
                    && string.Equals(invocation?.Name, Tool_Batch.BatchExecuteToolId, StringComparison.Ordinal)
                    && invocation.ApprovedPlan?.ChildRecords is AuthoringChildPlanSummary[] children
                    && children.Length > 0)
                {
                    children[0].ArgumentsHash = "sha256-tampered";
                    TamperApprovedChild = false;
                }
                return await next(context).ConfigureAwait(false);
            }
        }

        private sealed class BatchTestRunner : IRunTool
        {
            private readonly bool _shouldFail;

            public BatchTestRunner(string name, bool shouldFail, bool targetBound)
            {
                Name = name;
                _shouldFail = shouldFail;
                AuthoringCapability = targetBound
                    ? new AuthoringCapabilityDescriptor
                    {
                        MutationKind = AuthoringMutationKind.Delete,
                        UndoLevel = AuthoringUndoLevel.None,
                        SupportsValidation = true,
                        SupportsPlanning = true,
                        Validator = new BatchInspector(),
                        Planner = new BatchInspector(),
                    }
                    : null;
            }

            public string Name { get; }
            public bool Enabled { get; set; } = true;
            public string? Title => Name;
            public string? Description => "Test-only batch runner.";
            public MethodInfo? Method => null;
            public string? SkillDescription => null;
            public string? SkillBody => null;
            public JsonNode? InputSchema => new JsonObject();
            public JsonNode? OutputSchema => null;
            public McpToolType ToolType => McpToolType.Standard;
            public bool? ReadOnlyHint => null;
            public bool? DestructiveHint => AuthoringCapability == null ? (bool?)null : true;
            public bool? IdempotentHint => null;
            public bool? OpenWorldHint => null;
            public AuthoringCapabilityDescriptor? AuthoringCapability { get; }
            public int TokenCount => 0;
            public int InvocationCount { get; private set; }

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                _ = namedParameters;
                _ = cancellationToken;
                InvocationCount++;
                if (_shouldFail)
                {
                    return Task.FromResult(ResponseCallTool.Error(new ToolCallError(
                        ToolCallErrorCodes.ToolExecutionFailed,
                        "Intentional batch test failure.")).SetRequestID(requestId));
                }

                return Task.FromResult(ResponseCallTool.Success("ok").SetRequestID(requestId));
            }
        }

        private sealed class BatchInspector : IAuthoringValidator, IAuthoringPlanner
        {
            public AuthoringValidationResult Validate(AuthoringInvocation invocation)
                => new AuthoringValidationResult
                {
                    Valid = true,
                    Targets = new[]
                    {
                        new AuthoringTargetSummary
                        {
                            Kind = "batch-test",
                            Name = invocation.Name,
                            Fingerprint = AuthoringConfirmationBinding.ComputeArgumentsHash(invocation.Arguments),
                        },
                    },
                };

            public AuthoringPlanSummary Plan(AuthoringInvocation invocation)
                => new AuthoringPlanSummary
                {
                    Targets = Validate(invocation).Targets,
                    PredictedEffects = new[] { "test failure" },
                };
        }
    }
}
