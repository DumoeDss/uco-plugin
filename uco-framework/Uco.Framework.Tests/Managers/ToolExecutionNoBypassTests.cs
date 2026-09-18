#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace com.AtelierAI.Uco.Framework.Tests.Managers
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
            var toolManager = new UcoToolManager(
                NullLogger<UcoToolManager>.Instance,
                reflector,
                regularTools,
                pipeline);
            var systemManager = new UcoSystemToolManager(
                NullLogger<UcoSystemToolManager>.Instance,
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
        public void GuardedCollections_SupportDictionaryInterfaceConsumers()
        {
            var reflector = new Reflector();
            var regular = new ToolRunnerCollection(reflector, null);
            var system = new SystemToolRunnerCollection(reflector, null);

            AssertSupportedDictionaryContract(
                regular,
                regular,
                new FakeRunTool("regular-interface-tool"));
            AssertSupportedDictionaryContract(
                system,
                system,
                new FakeRunTool("system-interface-tool") { ToolType = UcoToolType.System });
        }

        [Fact]
        public void BuilderAndTypedCollections_GuardCustomRunnersImmediately()
        {
            var reflector = new Reflector();
            var collectionRunner = MutatingRunner("collection-tool", UcoToolType.Standard);
            var systemCollectionRunner = MutatingRunner("system-collection-tool", UcoToolType.System);
            var managerRunner = MutatingRunner("manager-tool", UcoToolType.Standard);
            var builderRunner = MutatingRunner("builder-tool", UcoToolType.Standard);
            var collection = new ToolRunnerCollection(reflector, null)
                .Add(new Dictionary<string, IRunTool>
                {
                    [collectionRunner.Name] = collectionRunner,
                });
            var systemCollection = new SystemToolRunnerCollection(reflector, null)
                .Add(new Dictionary<string, IRunTool>
                {
                    [systemCollectionRunner.Name] = systemCollectionRunner,
                });
            var manager = new UcoToolManager(
                NullLogger<UcoToolManager>.Instance,
                reflector,
                new ToolRunnerCollection(reflector, null));
            manager.AddTool(managerRunner.Name, managerRunner).ShouldBeTrue();
            var builder = new UcoBuilder(new com.AtelierAI.Uco.Framework.Common.Version());
            builder.AddTool(builderRunner.Name, builderRunner);
            var plugin = builder.Build(reflector);

            collection[collectionRunner.Name].ShouldNotBeSameAs(collectionRunner);
            systemCollection[systemCollectionRunner.Name].ShouldNotBeSameAs(systemCollectionRunner);
            manager.GetAllTools().Single().ShouldNotBeSameAs(managerRunner);
            plugin.UcoManager.ToolManager!.GetAllTools().Single().ShouldNotBeSameAs(builderRunner);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DictionaryRegistration_GuardsEveryValueBeforeManagerLookup(bool systemTool)
        {
            var reflector = new Reflector();
            object concreteCollection = systemTool
                ? new SystemToolRunnerCollection(reflector, null)
                : new ToolRunnerCollection(reflector, null);
            var dictionary = (IDictionary<string, IRunTool>)concreteCollection;
            var toolType = systemTool ? UcoToolType.System : UcoToolType.Standard;
            var indexerRunner = MutatingRunner("indexer-tool", toolType);
            var addRunner = MutatingRunner("add-tool", toolType);
            var collectionAddRunner = MutatingRunner("collection-add-tool", toolType);
            var bulkAddRunner = MutatingRunner("bulk-add-tool", toolType);

            dictionary[indexerRunner.Name] = indexerRunner;
            dictionary.Add(addRunner.Name, addRunner);
            ((ICollection<KeyValuePair<string, IRunTool>>)dictionary).Add(
                new KeyValuePair<string, IRunTool>(collectionAddRunner.Name, collectionAddRunner));
            var bulkAdd = concreteCollection.GetType().GetMethod(
                "Add",
                new[] { typeof(IDictionary<string, IRunTool>) });
            bulkAdd.ShouldNotBeNull();
            bulkAdd!.Invoke(concreteCollection, new object[]
            {
                new Dictionary<string, IRunTool> { [bulkAddRunner.Name] = bulkAddRunner },
            });

            var fromIndexer = dictionary[indexerRunner.Name];
            dictionary.TryGetValue(addRunner.Name, out var fromTryGetValue).ShouldBeTrue();
            var fromValues = dictionary.Values.Single(tool => tool.Name == collectionAddRunner.Name);
            var fromEnumeration = dictionary.Single(pair => pair.Key == bulkAddRunner.Name).Value;
            var exposedRunners = new[]
            {
                fromIndexer,
                fromTryGetValue,
                fromValues,
                fromEnumeration,
            };

            foreach (var exposed in exposedRunners)
            {
                var direct = await exposed.Run("direct-request", EmptyArguments());
                direct.Status.ShouldBe(ResponseStatus.Error);
                direct.StructuredError.ShouldNotBeNull();
                direct.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.SafetyUnsupported);
                direct.StructuredError.Details!["reason"]!.GetValue<string>()
                    .ShouldBe("direct_runner_bypass");
            }

            var rawRunners = new[]
            {
                indexerRunner,
                addRunner,
                collectionAddRunner,
                bulkAddRunner,
            };
            dictionary.Count.ShouldBe(rawRunners.Length);
            dictionary.ContainsKey(indexerRunner.Name.ToUpperInvariant()).ShouldBeFalse();
            dictionary.Values.ShouldAllBe(exposed => rawRunners.All(raw => !ReferenceEquals(exposed, raw)));
            rawRunners.ShouldAllBe(raw => raw.Calls == 0);

            (concreteCollection is Dictionary<string, IRunTool>).ShouldBeFalse();
            Should.Throw<InvalidCastException>(() =>
            {
                _ = (Dictionary<string, IRunTool>)concreteCollection;
            });

            // A caller's independently retained original object is not a framework execution path;
            // the registry boundary guarantees only the values it stores and exposes.
            rawRunners.ShouldAllBe(raw => dictionary.Values.All(exposed => !ReferenceEquals(raw, exposed)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GuardedRegistry_BlocksDirectAndMiddlewareBypasses_ButExecutesConfirmedManagerCallOnce(bool systemTool)
        {
            const string registrationKey = "MiXeD-Key";
            var reflector = new Reflector();
            var capability = new AuthoringCapabilityDescriptor
            {
                MutationKind = AuthoringMutationKind.Modify,
                UndoLevel = AuthoringUndoLevel.None,
            };
            var inputSchema = new JsonObject { ["type"] = "object" };
            var outputSchema = new JsonObject { ["type"] = "object" };
            var rawRunner = new FakeRunTool("declared-runner-name")
            {
                ToolType = systemTool ? UcoToolType.System : UcoToolType.Standard,
                Title = "Metadata title",
                Description = "Metadata description",
                Method = typeof(ToolExecutionNoBypassTests).GetMethod(
                    nameof(MetadataMethod),
                    BindingFlags.NonPublic | BindingFlags.Static),
                SkillDescription = "Skill description",
                SkillBody = "Skill body",
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                ReadOnlyHint = false,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
                AuthoringCapability = capability,
                TokenCount = 17,
            };
            var probe = new RunnerProbeMiddleware();
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[]
            {
                new AuthoringSafetyMiddleware(),
                probe,
            });

            Func<IEnumerable<IRunTool>> getAllTools;
            Func<string, bool> hasTool;
            Func<RequestCallTool, Task<ResponseData<ResponseCallTool>>> runTool;
            Func<string, IRunTool> getStoredRunner;
            if (systemTool)
            {
                var tools = new SystemToolRunnerCollection(reflector, null);
                tools[registrationKey] = rawRunner;
                var manager = new UcoSystemToolManager(
                    NullLogger<UcoSystemToolManager>.Instance,
                    tools,
                    pipeline);
                getAllTools = manager.GetAllTools;
                hasTool = manager.HasTool;
                runTool = request => manager.RunSystemTool(request);
                getStoredRunner = key => tools[key];
            }
            else
            {
                var tools = new ToolRunnerCollection(reflector, null);
                tools[registrationKey] = rawRunner;
                var manager = new UcoToolManager(
                    NullLogger<UcoToolManager>.Instance,
                    reflector,
                    tools,
                    pipeline);
                getAllTools = manager.GetAllTools;
                hasTool = manager.HasTool;
                runTool = request => manager.RunCallTool(request);
                getStoredRunner = key => tools[key];
            }

            var exposed = getAllTools().Single();
            probe.Runner = exposed;

            exposed.ShouldNotBeSameAs(rawRunner);
            getStoredRunner(registrationKey).ShouldBeSameAs(exposed);
            getAllTools().Single().ShouldBeSameAs(exposed);
            GuardedRunTool.Wrap(exposed).ShouldBeSameAs(exposed);
            hasTool(registrationKey).ShouldBeTrue();
            hasTool(registrationKey.ToLowerInvariant()).ShouldBeFalse();
            exposed.Name.ShouldBe(rawRunner.Name);
            exposed.Title.ShouldBe(rawRunner.Title);
            exposed.Description.ShouldBe(rawRunner.Description);
            exposed.Method.ShouldBe(rawRunner.Method);
            exposed.SkillDescription.ShouldBe(rawRunner.SkillDescription);
            exposed.SkillBody.ShouldBe(rawRunner.SkillBody);
            exposed.InputSchema.ShouldBeSameAs(inputSchema);
            exposed.OutputSchema.ShouldBeSameAs(outputSchema);
            exposed.ToolType.ShouldBe(rawRunner.ToolType);
            exposed.ReadOnlyHint.ShouldBe(rawRunner.ReadOnlyHint);
            exposed.DestructiveHint.ShouldBe(rawRunner.DestructiveHint);
            exposed.IdempotentHint.ShouldBe(rawRunner.IdempotentHint);
            exposed.OpenWorldHint.ShouldBe(rawRunner.OpenWorldHint);
            exposed.AuthoringCapability.ShouldBeSameAs(capability);
            exposed.TokenCount.ShouldBe(rawRunner.TokenCount);
            exposed.Enabled = false;
            rawRunner.Enabled.ShouldBeFalse();
            exposed.Enabled = true;

            var direct = await exposed.Run("direct-request", EmptyArguments());
            direct.Status.ShouldBe(ResponseStatus.Error);
            direct.RequestID.ShouldBe("direct-request");
            direct.StructuredError.ShouldNotBeNull();
            direct.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.SafetyUnsupported);
            direct.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("direct_runner_bypass");
            rawRunner.Calls.ShouldBe(0);

            var issueRequest = ControlledRequest(
                "manager-request",
                registrationKey,
                "manager-call",
                "manager-trace");
            var issued = await runTool(issueRequest);
            issued.Status.ShouldBe(ResponseStatus.Error);
            issued.RequestID.ShouldBe("manager-request");
            issued.StructuredError.ShouldNotBeNull();
            issued.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            rawRunner.Calls.ShouldBe(0);

            var plan = issued.StructuredError.Details!["confirmationPlan"]!.AsObject();
            var confirmedControl = issueRequest.Control!.Clone();
            confirmedControl.Confirm = true;
            confirmedControl.Confirmation = new ToolCallConfirmation
            {
                PlanId = plan["planId"]!.GetValue<string>(),
                PlanHash = plan["planHash"]!.GetValue<string>(),
                ExpiresAtUnixMs = plan["expiresAtUnixMs"]!.GetValue<long>(),
            };

            ResponseCallTool? nestedAttempt = null;
            rawRunner.DuringRun = async () =>
            {
                nestedAttempt = await exposed.Run("nested-request", EmptyArguments());
            };
            var confirmed = await runTool(new RequestCallTool(
                issueRequest.RequestID,
                registrationKey,
                EmptyArguments(),
                confirmedControl));

            confirmed.Status.ShouldBe(ResponseStatus.Success);
            confirmed.RequestID.ShouldBe("manager-request");
            rawRunner.Calls.ShouldBe(1);
            probe.Attempt.ShouldNotBeNull();
            probe.Attempt!.Status.ShouldBe(ResponseStatus.Error);
            probe.Attempt.RequestID.ShouldBe("middleware-request");
            probe.Attempt.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.SafetyUnsupported);
            probe.Attempt.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("direct_runner_bypass");
            nestedAttempt.ShouldNotBeNull();
            nestedAttempt!.Status.ShouldBe(ResponseStatus.Error);
            nestedAttempt.RequestID.ShouldBe("nested-request");
            nestedAttempt.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.SafetyUnsupported);
            nestedAttempt.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("direct_runner_bypass");
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
            var toolManager = new UcoToolManager(
                NullLogger<UcoToolManager>.Instance,
                reflector,
                regularTools,
                pipeline);
            var systemManager = new UcoSystemToolManager(
                NullLogger<UcoSystemToolManager>.Instance,
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

        private static void AssertSupportedDictionaryContract(
            IDictionary<string, IRunTool> writable,
            IReadOnlyDictionary<string, IRunTool> readable,
            IRunTool rawRunner)
        {
            writable.Add(rawRunner.Name, rawRunner);

            writable.ContainsKey(rawRunner.Name).ShouldBeTrue();
            writable.TryGetValue(rawRunner.Name, out var writableRunner).ShouldBeTrue();
            readable.TryGetValue(rawRunner.Name, out var readableRunner).ShouldBeTrue();
            readable[rawRunner.Name].ShouldBeSameAs(readableRunner);
            writableRunner.ShouldBeSameAs(readableRunner);
            readableRunner.ShouldNotBeSameAs(rawRunner);
            readable.Keys.Single().ShouldBe(rawRunner.Name);
            readable.Values.Single().ShouldBeSameAs(readableRunner);
            readable.Single().Value.ShouldBeSameAs(readableRunner);
            readable.Count.ShouldBe(1);

            writable.Remove(rawRunner.Name).ShouldBeTrue();
            writable.Count.ShouldBe(0);
            writable[rawRunner.Name] = rawRunner;
            writable.Clear();
            readable.Count.ShouldBe(0);
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

        private static FakeRunTool MutatingRunner(string name, UcoToolType toolType)
            => new FakeRunTool(name)
            {
                ToolType = toolType,
                ReadOnlyHint = false,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Modify,
                    UndoLevel = AuthoringUndoLevel.None,
                },
            };

        private static void MetadataMethod()
        {
        }

        private sealed class RunnerProbeMiddleware : IToolExecutionMiddleware
        {
            public IRunTool? Runner { get; set; }
            public ResponseCallTool? Attempt { get; private set; }

            public async Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next)
            {
                if (Runner != null)
                    Attempt = await Runner.Run("middleware-request", EmptyArguments());
                return await next(context);
            }
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
            public string? Title { get; set; }
            public string? Description { get; set; }
            public MethodInfo? Method { get; set; }
            public string? SkillDescription { get; set; }
            public string? SkillBody { get; set; }
            public JsonNode? InputSchema { get; set; } = new JsonObject();
            public JsonNode? OutputSchema { get; set; }
            public UcoToolType ToolType { get; set; } = UcoToolType.Standard;
            public bool? ReadOnlyHint { get; set; } = true;
            public bool? DestructiveHint { get; set; }
            public bool? IdempotentHint { get; set; }
            public bool? OpenWorldHint { get; set; }
            public AuthoringCapabilityDescriptor? AuthoringCapability { get; set; }
            public int TokenCount { get; set; }
            public int Calls { get; private set; }
            public Func<Task>? DuringRun { get; set; }

            public async Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                _ = namedParameters;
                _ = cancellationToken;
                Calls++;
                if (DuringRun != null)
                    await DuringRun();
                return ResponseCallTool.Success().SetRequestID(requestId);
            }
        }
    }
}
