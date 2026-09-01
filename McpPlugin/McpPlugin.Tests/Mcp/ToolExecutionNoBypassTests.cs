#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace com.IvanMurzak.McpPlugin.Tests.Mcp
{
    /// <summary>
    /// Cross-entry regression guards for the single execution seam. The test
    /// middleware records each context and rejects it without calling next;
    /// every fake runner must therefore remain untouched.
    /// </summary>
    public class ToolExecutionNoBypassTests
    {
        private static readonly JsonSerializerOptions WireOptions = CreateWireOptions();

        [Fact]
        public async Task RejectingMiddleware_BlocksRegularSystemInternalAndBatchChildEntrypoints()
        {
            var middleware = new RejectingMiddleware();
            var pipeline = new ToolExecutionPipeline(new[] { middleware });
            var reflector = new Reflector();
            var regularRunner = new FakeRunTool("regular-tool");
            var internalRunner = new FakeRunTool("internal-tool");
            var batchRunner = new FakeRunTool("batch-execute");
            var systemRunner = new FakeRunTool("system-tool");
            var regularTools = new ToolRunnerCollection(reflector, null)
                .Add(new Dictionary<string, IRunTool>
                {
                    [regularRunner.Name] = regularRunner,
                    [internalRunner.Name] = internalRunner,
                    [batchRunner.Name] = batchRunner,
                });
            var systemTools = new SystemToolRunnerCollection(reflector, null)
                .Add(new Dictionary<string, IRunTool> { [systemRunner.Name] = systemRunner });
            var toolManager = new McpToolManager(
                NullLogger<McpToolManager>.Instance,
                reflector,
                regularTools,
                pipeline);
            var systemManager = new McpSystemToolManager(
                NullLogger<McpSystemToolManager>.Instance,
                systemTools,
                pipeline);

            var regularResponse = await toolManager.RunCallTool(ControlledRequest(
                "regular-request", regularRunner.Name, "regular-call", "trace-root"));
            var internalResponse = await toolManager.RunCallTool(ControlledRequest(
                "internal-request", internalRunner.Name, "internal-call", "trace-root"));

            // Model the Unity-owned batch dispatcher: the parent and each
            // child still enter the regular manager, but the child gets a
            // fresh request/logical id and the inherited correlation id.
            var parentRequest = ControlledRequest(
                "batch-request", batchRunner.Name, "batch-parent", "trace-batch");
            var parentResponse = await toolManager.RunCallTool(parentRequest);
            var parentContext = ToolCallContextNormalizer.Normalize(parentRequest).Context;
            var childContext = ToolCallContextNormalizer.DeriveChild(
                parentContext,
                new ToolCallControl { CallId = "batch-child" },
                requestId: "batch-child-request",
                cancellationToken: parentContext.CancellationToken);
            var childResponse = await toolManager.RunCallTool(
                new RequestCallTool(
                    childContext.RequestID,
                    regularRunner.Name,
                    EmptyArguments(),
                    childContext.ToControl()),
                childContext.CancellationToken);

            var systemResponse = await systemManager.RunSystemTool(ControlledRequest(
                "system-request", systemRunner.Name, "system-call", "trace-root"));

            foreach (var response in new[]
            {
                regularResponse,
                internalResponse,
                parentResponse,
                childResponse,
                systemResponse,
            })
            {
                response.Status.ShouldBe(ResponseStatus.Error);
                response.StructuredError.ShouldNotBeNull();
                response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.MiddlewareRejected);
            }

            toolManager.ExecutionPipeline.ShouldBeSameAs(pipeline);
            systemManager.ExecutionPipeline.ShouldBeSameAs(pipeline);
            middleware.Contexts.Count.ShouldBe(5);
            middleware.Contexts.ConvertAll(context => context.CallId).ShouldBe(new[]
            {
                "regular-call",
                "internal-call",
                "batch-parent",
                "batch-child",
                "system-call",
            });
            middleware.Contexts[3].ParentCallId.ShouldBe("batch-parent");
            middleware.Contexts[3].CorrelationId.ShouldBe("trace-batch");

            regularRunner.Calls.ShouldBe(0);
            internalRunner.Calls.ShouldBe(0);
            batchRunner.Calls.ShouldBe(0);
            systemRunner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task WsDispatcherHandlers_RejectRegularAndSystemCallsBeforeRunners()
        {
            var middleware = new RejectingMiddleware();
            var pipeline = new ToolExecutionPipeline(new[] { middleware });
            var reflector = new Reflector();
            var regularRunner = new FakeRunTool("regular-tool");
            var systemRunner = new FakeRunTool("system-tool");
            var regularTools = new ToolRunnerCollection(reflector, null)
                .Add(new Dictionary<string, IRunTool> { [regularRunner.Name] = regularRunner });
            var systemTools = new SystemToolRunnerCollection(reflector, null)
                .Add(new Dictionary<string, IRunTool> { [systemRunner.Name] = systemRunner });
            var toolManager = new McpToolManager(
                NullLogger<McpToolManager>.Instance,
                reflector,
                regularTools,
                pipeline);
            var systemManager = new McpSystemToolManager(
                NullLogger<McpSystemToolManager>.Instance,
                systemTools,
                pipeline);
            var dispatcher = new WsRpcDispatcher(WireOptions);
            var sent = new List<WsResponse>();
            dispatcher.SendFunc = (bytes, _) =>
            {
                WsEnvelope.TryParseMessage(bytes, WireOptions, out var parsed).ShouldBeTrue();
                parsed.ShouldNotBeNull();
                parsed!.Type.ShouldBe(WsMessageType.Response);
                sent.Add(new WsResponse
                {
                    Id = parsed.Id!,
                    Result = parsed.Result,
                    Error = parsed.Error,
                });
                return Task.CompletedTask;
            };
            dispatcher.RegisterHandler<RequestCallTool, ResponseData<ResponseCallTool>>(
                "RunCallTool",
                async (request, cancellationToken) =>
                    await toolManager.RunCallTool(request!, cancellationToken).ConfigureAwait(false));
            dispatcher.RegisterHandler<RequestCallTool, ResponseData<ResponseCallTool>>(
                "RunSystemTool",
                async (request, cancellationToken) =>
                    await systemManager.RunSystemTool(request!, cancellationToken).ConfigureAwait(false));

            await DispatchToolRequest(dispatcher, "ws-regular-envelope", "RunCallTool",
                ControlledRequest("ws-regular-request", regularRunner.Name, "ws-regular-call", "ws-trace"));
            await DispatchToolRequest(dispatcher, "ws-system-envelope", "RunSystemTool",
                ControlledRequest("ws-system-request", systemRunner.Name, "ws-system-call", "ws-trace"));

            sent.Count.ShouldBe(2);
            sent[0].Id.ShouldBe("ws-regular-envelope");
            sent[1].Id.ShouldBe("ws-system-envelope");
            var regularResult = DeserializeResult(sent[0]);
            var systemResult = DeserializeResult(sent[1]);
            regularResult.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.MiddlewareRejected);
            regularResult.StructuredError.CallId.ShouldBe("ws-regular-call");
            regularResult.StructuredError.CorrelationId.ShouldBe("ws-trace");
            systemResult.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.MiddlewareRejected);
            systemResult.StructuredError.CallId.ShouldBe("ws-system-call");
            systemResult.StructuredError.CorrelationId.ShouldBe("ws-trace");
            middleware.Contexts.ConvertAll(context => context.CallId).ShouldBe(new[]
            {
                "ws-regular-call",
                "ws-system-call",
            });
            regularRunner.Calls.ShouldBe(0);
            systemRunner.Calls.ShouldBe(0);
        }

        private static async Task DispatchToolRequest(
            WsRpcDispatcher dispatcher,
            string envelopeId,
            string method,
            RequestCallTool request)
        {
            var envelope = new WsRequest
            {
                Id = envelopeId,
                Method = method,
                Params = JsonSerializer.SerializeToElement(request, WireOptions),
            };
            var bytes = WsEnvelope.SerializeRequest(envelope, WireOptions);
            WsEnvelope.TryParseMessage(bytes, WireOptions, out var parsed).ShouldBeTrue();
            parsed.ShouldNotBeNull();
            await dispatcher.HandleIncomingAsync(parsed!).ConfigureAwait(false);
        }

        private static ResponseData<ResponseCallTool> DeserializeResult(WsResponse response)
        {
            response.Result.ShouldNotBeNull();
            return response.Result!.Value.Deserialize<ResponseData<ResponseCallTool>>(WireOptions)!;
        }

        private static RequestCallTool ControlledRequest(
            string requestId,
            string name,
            string callId,
            string correlationId)
            => new RequestCallTool(
                requestId,
                name,
                EmptyArguments(),
                new ToolCallControl
                {
                    CallId = callId,
                    CorrelationId = correlationId,
                });

        private static IReadOnlyDictionary<string, JsonElement> EmptyArguments()
            => new Dictionary<string, JsonElement>();

        private static JsonSerializerOptions CreateWireOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            return options;
        }

        private sealed class RejectingMiddleware : IToolExecutionMiddleware
        {
            public List<ToolCallContext> Contexts { get; } = new List<ToolCallContext>();

            public Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next)
            {
                _ = next;
                Contexts.Add(context);
                return Task.FromResult(ResponseCallTool.Error(new ToolCallError(
                    ToolCallErrorCodes.MiddlewareRejected,
                    "Rejected by no-bypass test middleware.",
                    callId: context.CallId,
                    correlationId: context.CorrelationId)));
            }
        }

        private sealed class FakeRunTool : IRunTool
        {
            public FakeRunTool(string name) => Name = name;

            public string Name { get; }
            public bool Enabled { get; set; } = true;
            public string? Title => Name;
            public string? Description => null;
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
            public int Calls { get; private set; }

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                _ = namedParameters;
                _ = cancellationToken;
                Calls++;
                return Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
            }
        }
    }
}
