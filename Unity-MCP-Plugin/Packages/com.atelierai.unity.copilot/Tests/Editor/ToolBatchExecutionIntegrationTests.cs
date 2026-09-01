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
            var middleware = new RecordingMiddleware();
            var successfulRunner = new BatchTestRunner("batch-test-success", shouldFail: false);
            var failingRunner = new BatchTestRunner("batch-test-failure", shouldFail: true);
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

                var request = BuildBatchRequest();
                LogAssert.Expect(
                    UnityEngine.LogType.Error,
                    new System.Text.RegularExpressions.Regex("Error Response to AI"));
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
                Assert.IsFalse(string.Equals(parent.CallId, firstChild.CallId, StringComparison.Ordinal));
                Assert.IsFalse(string.Equals(firstChild.CallId, secondChild.CallId, StringComparison.Ordinal));
                Assert.IsFalse(string.Equals(firstChild.RequestID, secondChild.RequestID, StringComparison.Ordinal));
                Assert.AreEqual(1, successfulRunner.InvocationCount);
                Assert.AreEqual(1, failingRunner.InvocationCount);
            }
            finally
            {
                RestoreEditorSingleton(originalInstance, originalPlugin, replacement);
            }
        }

        private static RequestCallTool BuildBatchRequest()
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
            return new RequestCallTool(
                "batch-request",
                Tool_Batch.BatchExecuteToolId,
                arguments,
                new ToolCallControl
                {
                    Version = ToolCallControl.CurrentVersion,
                    CallId = "batch-parent",
                    CorrelationId = "batch-trace",
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

            public async Task<ResponseCallTool> InvokeAsync(
                ToolCallContext context,
                ToolCallNext next)
            {
                Contexts.Add(context.Clone());
                return await next(context).ConfigureAwait(false);
            }
        }

        private sealed class BatchTestRunner : IRunTool
        {
            private readonly bool _shouldFail;

            public BatchTestRunner(string name, bool shouldFail)
            {
                Name = name;
                _shouldFail = shouldFail;
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
            public bool? DestructiveHint => null;
            public bool? IdempotentHint => null;
            public bool? OpenWorldHint => null;
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
    }
}
