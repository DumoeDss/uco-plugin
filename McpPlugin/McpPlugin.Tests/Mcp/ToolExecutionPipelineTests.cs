#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace com.IvanMurzak.McpPlugin.Tests.Mcp
{
    public class ToolExecutionPipelineTests
    {
        [Fact]
        public async Task InvokeAsync_ComposesMiddlewareInRegistrationOrder_AndGatesTerminal()
        {
            var events = new List<string>();
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[]
            {
                new DelegateMiddleware(async (context, next) =>
                {
                    events.Add("first-before");
                    var result = await next(context);
                    events.Add("first-after");
                    return result;
                }),
                new DelegateMiddleware(async (context, next) =>
                {
                    events.Add("second-before");
                    var result = await next(context);
                    events.Add("second-after");
                    return result;
                }),
                new DelegateMiddleware(async (context, next) =>
                {
                    events.Add("duplicate-before");
                    var first = await next(context);
                    var second = await next(context);
                    events.Add("duplicate-after");
                    first.ShouldBeSameAs(second);
                    return first;
                }),
            });
            var context = ControlledContext("call-order", "trace-order");
            var terminalCalls = 0;

            var response = await pipeline.InvokeAsync(context, _ =>
            {
                terminalCalls++;
                return Task.FromResult(ResponseCallTool.Success("ok"));
            });

            terminalCalls.ShouldBe(1);
            events.ShouldBe(new[]
            {
                "first-before",
                "second-before",
                "duplicate-before",
                "duplicate-after",
                "second-after",
                "first-after",
            });
            response.Status.ShouldBe(ResponseStatus.Success);
        }

        [Fact]
        public async Task InvokeAsync_ShortCircuitPreventsLaterMiddlewareAndRunner()
        {
            var firstCalls = 0;
            var laterCalls = 0;
            var terminalCalls = 0;
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[]
            {
                new DelegateMiddleware(async (context, next) =>
                {
                    firstCalls++;
                    return await next(context);
                }),
                new DelegateMiddleware((context, next) =>
                {
                    _ = next;
                    return Task.FromResult(ResponseCallTool.Error(new ToolCallError(
                        ToolCallErrorCodes.MiddlewareRejected,
                        "Rejected by focused test.",
                        callId: context.CallId,
                        correlationId: context.CorrelationId)));
                }),
                new DelegateMiddleware(async (context, next) =>
                {
                    laterCalls++;
                    return await next(context);
                }),
            });

            var response = await pipeline.InvokeAsync(
                ControlledContext("call-rejected", "trace-rejected"),
                _ =>
                {
                    terminalCalls++;
                    return Task.FromResult(ResponseCallTool.Success());
                });

            firstCalls.ShouldBe(1);
            laterCalls.ShouldBe(0);
            terminalCalls.ShouldBe(0);
            response.StructuredError.ShouldNotBeNull();
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.MiddlewareRejected);
            response.StructuredError.CallId.ShouldBe("call-rejected");
            response.StructuredError.CorrelationId.ShouldBe("trace-rejected");
        }

        [Fact]
        public async Task InvokeAsync_ExpiredDeadlineStopsMiddlewareAndRunner()
        {
            var middlewareCalls = 0;
            var terminalCalls = 0;
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[]
            {
                new DelegateMiddleware(async (context, next) =>
                {
                    middlewareCalls++;
                    return await next(context);
                }),
            });
            var context = ControlledContext(
                "call-expired",
                "trace-expired",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);

            var response = await pipeline.InvokeAsync(context, _ =>
            {
                terminalCalls++;
                return Task.FromResult(ResponseCallTool.Success());
            });

            response.Status.ShouldBe(ResponseStatus.Error);
            response.StructuredError.ShouldNotBeNull();
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            response.StructuredError.CallId.ShouldBe("call-expired");
            response.StructuredError.CorrelationId.ShouldBe("trace-expired");
            middlewareCalls.ShouldBe(0);
            terminalCalls.ShouldBe(0);
        }

        [Fact]
        public async Task InvokeAsync_DelayedMiddlewareCannotStartRunnerAfterDeadline()
        {
            var middlewareEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var runnerCalls = 0;
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[]
            {
                new DelegateMiddleware(async (context, next) =>
                {
                    middlewareEntered.TrySetResult(true);
                    // Deliberately ignore the linked cancellation token. This
                    // models middleware that is still doing work when the
                    // effective deadline expires.
                    await Task.Delay(200).ConfigureAwait(false);
                    return await next(context).ConfigureAwait(false);
                }),
            });
            var context = ControlledContext(
                "call-delayed-deadline",
                "trace-delayed-deadline",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 25);

            var execution = pipeline.InvokeAsync(context, _ =>
            {
                Interlocked.Increment(ref runnerCalls);
                // The runner also ignores cancellation; the terminal guard must
                // prevent this delegate from being reached at all.
                return Task.FromResult(ResponseCallTool.Success("runner-started"));
            });

            await middlewareEntered.Task.ConfigureAwait(false);
            var response = await execution.ConfigureAwait(false);

            response.Status.ShouldBe(ResponseStatus.Error);
            response.StructuredError.ShouldNotBeNull();
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            response.StructuredError.CallId.ShouldBe("call-delayed-deadline");
            response.StructuredError.CorrelationId.ShouldBe("trace-delayed-deadline");
            response.RequestID.ShouldBe("call-delayed-deadline-request");
            runnerCalls.ShouldBe(0);
        }

        [Fact]
        public async Task InvokeAsync_FutureDeadlineCancelsLinkedRunnerToken()
        {
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken runnerToken = default;
            var pipeline = new ToolExecutionPipeline();
            var context = ControlledContext(
                "call-deadline",
                "trace-deadline",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 100);

            var task = pipeline.InvokeAsync(context, async invocationContext =>
            {
                runnerToken = invocationContext.CancellationToken;
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, invocationContext.CancellationToken).ConfigureAwait(false);
                return ResponseCallTool.Success();
            });

            await started.Task.ConfigureAwait(false);
            var response = await task.ConfigureAwait(false);

            runnerToken.CanBeCanceled.ShouldBeTrue();
            runnerToken.IsCancellationRequested.ShouldBeTrue();
            response.StructuredError.ShouldNotBeNull();
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            response.StructuredError.CallId.ShouldBe("call-deadline");
        }

        [Fact]
        public async Task InvokeAsync_CallerCancellationIsDistinctFromDeadline()
        {
            using var cancellation = new CancellationTokenSource();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pipeline = new ToolExecutionPipeline();
            var context = ControlledContext(
                "call-cancelled",
                "trace-cancelled",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2_000,
                cancellation.Token);

            var task = pipeline.InvokeAsync(context, async invocationContext =>
            {
                started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, invocationContext.CancellationToken).ConfigureAwait(false);
                return ResponseCallTool.Success();
            });

            await started.Task.ConfigureAwait(false);
            cancellation.Cancel();
            var response = await task.ConfigureAwait(false);

            response.StructuredError.ShouldNotBeNull();
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.Cancelled);
            response.StructuredError.CallId.ShouldBe("call-cancelled");
            response.StructuredError.CorrelationId.ShouldBe("trace-cancelled");
        }

        [Fact]
        public async Task InvokeAsync_MapsControlledMiddlewareAndRunnerFailures()
        {
            var middlewareFailure = new ToolCallControlException(
                ToolCallErrorCodes.MiddlewareRejected,
                "Rejected by middleware.");
            var middlewareResponse = await new ToolExecutionPipeline(new[]
                {
                    new DelegateMiddleware((_, _) => Task.FromException<ResponseCallTool>(middlewareFailure)),
                })
                .InvokeAsync(ControlledContext("call-middleware", "trace-middleware"),
                    _ => Task.FromResult(ResponseCallTool.Success()));

            middlewareResponse.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.MiddlewareRejected);
            middlewareResponse.StructuredError.CallId.ShouldBe("call-middleware");
            middlewareResponse.StructuredError.CorrelationId.ShouldBe("trace-middleware");

            var runnerResponse = await new ToolExecutionPipeline()
                .InvokeAsync(ControlledContext("call-runner", "trace-runner"),
                    _ => Task.FromException<ResponseCallTool>(new InvalidOperationException("secret detail")));

            runnerResponse.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ToolExecutionFailed);
            runnerResponse.StructuredError.Message.ShouldBe("Tool execution failed.");
            runnerResponse.StructuredError.Message.ShouldNotContain("secret detail");
            runnerResponse.StructuredError.CallId.ShouldBe("call-runner");
        }

        [Fact]
        public async Task Managers_UseTheSamePipelineForRegularAndSystemTools()
        {
            var middleware = new RecordingMiddleware();
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

            toolManager.ExecutionPipeline.ShouldBeSameAs(pipeline);
            systemManager.ExecutionPipeline.ShouldBeSameAs(pipeline);
            var regularResponse = await toolManager.RunCallTool(new RequestCallTool(
                "request-regular",
                regularRunner.Name,
                EmptyArguments(),
                new ToolCallControl
                {
                    CallId = "call-regular",
                    CorrelationId = "trace-shared",
                    DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000,
                }));
            var systemResponse = await systemManager.RunSystemTool(new RequestCallTool(
                "request-system",
                systemRunner.Name,
                EmptyArguments(),
                new ToolCallControl
                {
                    CallId = "call-system",
                    CorrelationId = "trace-shared",
                    DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000,
                }));

            regularResponse.Status.ShouldBe(ResponseStatus.Success);
            systemResponse.Status.ShouldBe(ResponseStatus.Success);
            middleware.Contexts.Count.ShouldBe(2);
            middleware.Contexts[0].CallId.ShouldBe("call-regular");
            middleware.Contexts[1].CallId.ShouldBe("call-system");
            regularRunner.LastCancellationToken.CanBeCanceled.ShouldBeTrue();
            systemRunner.LastCancellationToken.CanBeCanceled.ShouldBeTrue();
        }

        [Fact]
        public async Task Managers_ReturnStructuredDeadlineErrorWithoutRunningRunner()
        {
            var runner = new FakeRunTool("regular-tool");
            var reflector = new Reflector();
            var tools = new ToolRunnerCollection(reflector, null)
                .Add(new Dictionary<string, IRunTool> { [runner.Name] = runner });
            var manager = new McpToolManager(
                NullLogger<McpToolManager>.Instance,
                reflector,
                tools,
                new ToolExecutionPipeline());

            var response = await manager.RunCallTool(new RequestCallTool(
                "request-expired",
                runner.Name,
                EmptyArguments(),
                new ToolCallControl
                {
                    CallId = "call-expired-manager",
                    CorrelationId = "trace-expired-manager",
                    DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1,
                }));

            response.Status.ShouldBe(ResponseStatus.Error);
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            response.StructuredError.CallId.ShouldBe("call-expired-manager");
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task InvocationScope_IsolatedAcrossConcurrentAsyncFlows()
        {
            var first = ControlledContext("scope-first", "trace-first");
            var second = ControlledContext("scope-second", "trace-second");

            var values = await Task.WhenAll(
                ReadScopedCallId(first),
                ReadScopedCallId(second));

            values.ShouldBe(new[] { "scope-first", "scope-second" });
            ToolCallInvocationScope.Current.ShouldBeNull();
        }

        [Fact]
        public void RunTool_ExcludesInjectedContextFromSchemaAndPrunesDefinitions()
        {
            var reflector = new Reflector();
            var method = typeof(SchemaTool).GetMethod(nameof(SchemaTool.Invoke));
            method.ShouldNotBeNull();

            var tool = new RunTool(
                reflector,
                NullLogger<RunTool>.Instance,
                "schema-tool",
                method!);
            var schema = tool.InputSchema;

            schema.ShouldNotBeNull();
            schema!["properties"]!["context"].ShouldBeNull();
            var serialized = schema.ToJsonString();
            serialized.ShouldNotContain("ToolCallContext");
            serialized.ShouldNotContain("\"error\"");
        }

        private static async Task<string?> ReadScopedCallId(ToolCallContext context)
        {
            using var scope = ToolCallInvocationScope.Push(context);
            await Task.Delay(5).ConfigureAwait(false);
            return ToolCallInvocationScope.Current?.CallId;
        }

        private static ToolCallContext ControlledContext(
            string callId,
            string correlationId,
            long? deadlineUnixMs = null,
            CancellationToken cancellationToken = default)
            => new ToolCallContext
            {
                Version = ToolCallControl.CurrentVersion,
                RequestID = callId + "-request",
                CallId = callId,
                CorrelationId = correlationId,
                DeadlineUnixMs = deadlineUnixMs,
                CancellationToken = cancellationToken,
                Legacy = false,
            };

        private static IReadOnlyDictionary<string, JsonElement> EmptyArguments()
            => new Dictionary<string, JsonElement>();

        private sealed class DelegateMiddleware : IToolExecutionMiddleware
        {
            private readonly Func<ToolCallContext, ToolCallNext, Task<ResponseCallTool>> _handler;

            public DelegateMiddleware(Func<ToolCallContext, ToolCallNext, Task<ResponseCallTool>> handler)
                => _handler = handler;

            public Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next)
                => _handler(context, next);
        }

        private sealed class RecordingMiddleware : IToolExecutionMiddleware
        {
            public List<ToolCallContext> Contexts { get; } = new List<ToolCallContext>();

            public async Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next)
            {
                Contexts.Add(context);
                return await next(context).ConfigureAwait(false);
            }
        }

        private static class SchemaTool
        {
            public static Task<ResponseCallTool> Invoke(
                string value,
                [ToolCallContext] ToolCallContext? context = null)
            {
                _ = value;
                _ = context;
                return Task.FromResult(ResponseCallTool.Success());
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
            public CancellationToken LastCancellationToken { get; private set; }

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                LastCancellationToken = cancellationToken;
                return Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
            }
        }
    }
}
