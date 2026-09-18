#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;
using Shouldly;
using Xunit;

namespace com.AtelierAI.Uco.Framework.Tests.Managers
{
    public sealed class AuthoringDryRunTests
    {
        [Fact]
        public async Task PlanIsPolicyOwnedAndNeverCallsRunner()
        {
            var runner = new FakeRunTool("authoring")
            {
                ReadOnlyHint = false,
                AuthoringCapability = ConfirmationCapability(),
            };
            var context = Context("plan-call");
            context.DryRun = "plan";
            var invocation = new AuthoringInvocation(context, runner.Name, Arguments("name", "Block"), runner);

            using (ToolCallInvocationScope.Push(context, invocation))
            {
                var response = await new AuthoringSafetyMiddleware(new AuthoringSafetyPolicy())
                    .InvokeAsync(context, _ =>
                    {
                        runner.Calls++;
                        return Task.FromResult(ResponseCallTool.Success("must not run"));
                    });

                response.Status.ShouldBe(ResponseStatus.Success);
                response.Transaction.ShouldBeNull();
                response.StructuredContent!["result"]!["confirmationPlan"]!["planId"]
                    .GetValue<string>().ShouldStartWith("plan-");
            }

            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task MatchingPlanAllowsOneExactExecution()
        {
            var runner = new FakeRunTool("authoring")
            {
                ReadOnlyHint = false,
                AuthoringCapability = ConfirmationCapability(),
            };
            var policy = new AuthoringSafetyPolicy();
            var middleware = new AuthoringSafetyMiddleware(policy);
            var planContext = Context("same-call");
            planContext.DryRun = "plan";
            var planInvocation = new AuthoringInvocation(
                planContext,
                runner.Name,
                Arguments("name", "Block"),
                runner);

            ToolCallConfirmation confirmation;
            using (ToolCallInvocationScope.Push(planContext, planInvocation))
            {
                var planResponse = await middleware.InvokeAsync(
                    planContext,
                    _ => Task.FromResult(ResponseCallTool.Success("must not run")));
                var plan = planResponse.StructuredContent!["result"]!["confirmationPlan"]!.AsObject();
                confirmation = new ToolCallConfirmation
                {
                    PlanId = plan["planId"]!.GetValue<string>(),
                    PlanHash = plan["planHash"]!.GetValue<string>(),
                    ExpiresAtUnixMs = plan["expiresAtUnixMs"]!.GetValue<long>(),
                };
            }

            var executeContext = planContext.Clone();
            executeContext.DryRun = "none";
            executeContext.Confirm = true;
            executeContext.Confirmation = confirmation;
            var executeInvocation = new AuthoringInvocation(
                executeContext,
                runner.Name,
                Arguments("name", "Block"),
                runner);
            using (ToolCallInvocationScope.Push(executeContext, executeInvocation))
            {
                var response = await middleware.InvokeAsync(
                    executeContext,
                    _ => Task.FromResult(ResponseCallTool.Success("executed")));
                response.Status.ShouldBe(ResponseStatus.Success);
            }

            runner.Calls.ShouldBe(0); // terminal delegate is the runner substitute
        }

        [Fact]
        public async Task ChangedArgumentsAreStaleAndDoNotReachTerminal()
        {
            var runner = new FakeRunTool("authoring")
            {
                ReadOnlyHint = false,
                AuthoringCapability = ConfirmationCapability(),
            };
            var policy = new AuthoringSafetyPolicy();
            var middleware = new AuthoringSafetyMiddleware(policy);
            var planContext = Context("changed-call");
            planContext.DryRun = "plan";
            ToolCallConfirmation token;
            using (ToolCallInvocationScope.Push(
                planContext,
                new AuthoringInvocation(planContext, runner.Name, Arguments("name", "Block"), runner)))
            {
                var response = await middleware.InvokeAsync(planContext, _ =>
                    Task.FromResult(ResponseCallTool.Success()));
                var plan = response.StructuredContent!["result"]!["confirmationPlan"]!.AsObject();
                token = new ToolCallConfirmation
                {
                    PlanId = plan["planId"]!.GetValue<string>(),
                    PlanHash = plan["planHash"]!.GetValue<string>(),
                    ExpiresAtUnixMs = plan["expiresAtUnixMs"]!.GetValue<long>(),
                };
            }

            var executeContext = planContext.Clone();
            executeContext.DryRun = "none";
            executeContext.Confirm = true;
            executeContext.Confirmation = token;
            var terminalCalls = 0;
            using (ToolCallInvocationScope.Push(
                executeContext,
                new AuthoringInvocation(executeContext, runner.Name, Arguments("name", "Other"), runner)))
            {
                var response = await middleware.InvokeAsync(executeContext, _ =>
                {
                    terminalCalls++;
                    return Task.FromResult(ResponseCallTool.Success());
                });
                response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            }

            terminalCalls.ShouldBe(0);
        }

        [Fact]
        public async Task MissingPlannerReturnsUnsupportedWithoutRunner()
        {
            var runner = new FakeRunTool("opaque")
            {
                ReadOnlyHint = false,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Modify,
                    UndoLevel = AuthoringUndoLevel.Full,
                    SupportsValidation = true,
                    Validator = new Validator(),
                    SupportsPlanning = false,
                },
            };
            var context = Context("unsupported-plan");
            context.DryRun = "plan";
            var calls = 0;
            using (ToolCallInvocationScope.Push(
                context,
                new AuthoringInvocation(context, runner.Name, EmptyArguments(), runner)))
            {
                var response = await new AuthoringSafetyMiddleware().InvokeAsync(context, _ =>
                {
                    calls++;
                    return Task.FromResult(ResponseCallTool.Success());
                });
                response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);
            }
            calls.ShouldBe(0);
        }

        private static AuthoringCapabilityDescriptor Capability()
            => new AuthoringCapabilityDescriptor
            {
                MutationKind = AuthoringMutationKind.Modify,
                UndoLevel = AuthoringUndoLevel.Full,
                SupportsValidation = true,
                SupportsPlanning = true,
                Validator = new Validator(),
                Planner = new Planner(),
            };

        private static AuthoringCapabilityDescriptor ConfirmationCapability()
            => new AuthoringCapabilityDescriptor
            {
                MutationKind = AuthoringMutationKind.Delete,
                UndoLevel = AuthoringUndoLevel.None,
                SupportsValidation = true,
                SupportsPlanning = true,
                Validator = new Validator(),
                Planner = new Planner(),
            };

        private static ToolCallContext Context(string id)
            => new ToolCallContext
            {
                Version = ToolCallControl.CurrentVersion,
                RequestID = id + "-request",
                CallId = id,
                CorrelationId = id + "-trace",
                DryRun = "none",
                Legacy = false,
                CancellationToken = CancellationToken.None,
            };

        private static IReadOnlyDictionary<string, JsonElement> Arguments(string name, string value)
        {
            using var document = JsonDocument.Parse("{\"" + name + "\":\"" + value + "\"}");
            var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                result[property.Name] = property.Value.Clone();
            return result;
        }

        private static IReadOnlyDictionary<string, JsonElement> EmptyArguments()
            => new Dictionary<string, JsonElement>();

        private sealed class Validator : IAuthoringValidator
        {
            public AuthoringValidationResult Validate(AuthoringInvocation invocation)
                => new AuthoringValidationResult
                {
                    Valid = true,
                    Targets = new[]
                    {
                        new AuthoringTargetSummary
                        {
                            Kind = "GameObject",
                            Name = invocation.Arguments.ContainsKey("name")
                                ? invocation.Arguments["name"].GetString()
                                : "Block",
                            Fingerprint = "target-1",
                        },
                    },
                };
        }

        private sealed class Planner : IAuthoringPlanner
        {
            public AuthoringPlanSummary Plan(AuthoringInvocation invocation)
                => new AuthoringPlanSummary
                {
                    Targets = new[]
                    {
                        new AuthoringTargetSummary
                        {
                            Kind = "GameObject",
                            Name = invocation.Arguments.ContainsKey("name")
                                ? invocation.Arguments["name"].GetString()
                                : "Block",
                            Fingerprint = "target-1",
                        },
                    },
                    PredictedEffects = new[] { "modify" },
                };
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
            public UcoToolType ToolType => UcoToolType.Standard;
            public bool? ReadOnlyHint { get; set; }
            public bool? DestructiveHint { get; set; }
            public bool? IdempotentHint { get; set; }
            public bool? OpenWorldHint { get; set; }
            public AuthoringCapabilityDescriptor? AuthoringCapability { get; set; }
            public int TokenCount => 0;
            public int Calls { get; set; }

            public Task<ResponseCallTool> Run(
                string requestId,
                IReadOnlyDictionary<string, JsonElement>? namedParameters,
                CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
            }
        }
    }
}
