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
    public sealed class AuthoringSafetyPolicyTests
    {
        [Theory]
        [InlineData(true, false, AuthoringMutationKind.Read, "read")]
        [InlineData(false, false, AuthoringMutationKind.Modify, "mutating")]
        [InlineData(false, true, AuthoringMutationKind.Modify, "destructive")]
        [InlineData(true, true, AuthoringMutationKind.Read, "destructive")]
        [InlineData(null, null, AuthoringMutationKind.Unknown, "unknown")]
        [InlineData(true, false, AuthoringMutationKind.Modify, "unknown")]
        [InlineData(false, false, AuthoringMutationKind.Read, "unknown")]
        [InlineData(null, null, AuthoringMutationKind.Create, "mutating")]
        [InlineData(null, null, AuthoringMutationKind.Delete, "destructive")]
        public void Classifier_UsesConservativePrecedence(
            bool? readOnlyHint,
            bool? destructiveHint,
            AuthoringMutationKind mutationKind,
            string expected)
        {
            var descriptor = new AuthoringCapabilityDescriptor
            {
                MutationKind = mutationKind,
                UndoLevel = AuthoringUndoLevel.Full,
            };

            var result = AuthoringSafetyPolicy.Classify(readOnlyHint, destructiveHint, descriptor);

            result.ToWireValue().ShouldBe(expected);
        }

        [Fact]
        public void Classifier_DoesNotUseIdempotentOrOpenWorldAsAuthority()
        {
            var descriptor = new AuthoringCapabilityDescriptor
            {
                MutationKind = AuthoringMutationKind.Modify,
                UndoLevel = AuthoringUndoLevel.None,
            };

            // Those hints are descriptive catalog metadata and are not inputs
            // to a privilege decision. A write remains mutating and has no
            // implicit Undo authority.
            AuthoringSafetyPolicy.Classify(null, null, descriptor)
                .ShouldBe(AuthoringRiskLevel.Mutating);
            descriptor.UndoLevel.ShouldBe(AuthoringUndoLevel.None);
        }

        [Fact]
        public async Task Middleware_AllowsReadAndIssuesConfirmationForUnknownWithoutRunner()
        {
            var readRunner = new FakeRunTool("read")
            {
                ReadOnlyHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Read,
                },
            };
            var unknownRunner = new FakeRunTool("unknown");
            var readContext = ControlledContext("read-call");
            var unknownContext = ControlledContext("unknown-call");
            var middleware = new AuthoringSafetyMiddleware();

            using (ToolCallInvocationScope.Push(
                readContext,
                new AuthoringInvocation(readContext, readRunner.Name, EmptyArguments(), readRunner)))
            {
                var response = await middleware.InvokeAsync(
                    readContext,
                    _ =>
                    {
                        readRunner.Calls++;
                        return Task.FromResult(ResponseCallTool.Success());
                    });
                response.Status.ShouldBe(ResponseStatus.Success);
            }

            using (ToolCallInvocationScope.Push(
                unknownContext,
                new AuthoringInvocation(unknownContext, unknownRunner.Name, EmptyArguments(), unknownRunner)))
            {
                var response = await middleware.InvokeAsync(
                    unknownContext,
                    _ =>
                    {
                        unknownRunner.Calls++;
                        return Task.FromResult(ResponseCallTool.Success());
                    });
                response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
                response.StructuredError.Details!["confirmationPlan"]!["planLevel"]!
                    .GetValue<string>().ShouldBe("none");
            }

            readRunner.Calls.ShouldBe(1);
            unknownRunner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task Middleware_RequiresConfirmationForDestructiveAndNeverCallsNext()
        {
            var runner = new FakeRunTool("delete")
            {
                DestructiveHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Delete,
                    UndoLevel = AuthoringUndoLevel.None,
                },
            };
            var context = ControlledContext("delete-call");
            var nextCalls = 0;
            var middleware = new AuthoringSafetyMiddleware();

            using (ToolCallInvocationScope.Push(
                context,
                new AuthoringInvocation(context, runner.Name, EmptyArguments(), runner)))
            {
                var response = await middleware.InvokeAsync(context, _ =>
                {
                    nextCalls++;
                    runner.Calls++;
                    return Task.FromResult(ResponseCallTool.Success());
                });

                response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            }

            nextCalls.ShouldBe(0);
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task Middleware_DryRunNeverFallsThroughToRunner()
        {
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Modify,
                    UndoLevel = AuthoringUndoLevel.Full,
                },
            };
            var context = ControlledContext("plan-call");
            context.DryRun = "plan";

            using (ToolCallInvocationScope.Push(
                context,
                new AuthoringInvocation(context, runner.Name, EmptyArguments(), runner)))
            {
                var response = await new AuthoringSafetyMiddleware().InvokeAsync(context, _ =>
                {
                    runner.Calls++;
                    return Task.FromResult(ResponseCallTool.Success());
                });
                response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);
            }

            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task Middleware_MissingInvocationScopeFailsClosed()
        {
            var context = ControlledContext("missing-scope");
            var nextCalls = 0;
            var response = await new AuthoringSafetyMiddleware().InvokeAsync(context, _ =>
            {
                nextCalls++;
                return Task.FromResult(ResponseCallTool.Success());
            });

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.SafetyUnsupported);
            nextCalls.ShouldBe(0);
        }

        [Fact]
        public void PlanSummary_BoundsAndRedactsTargetDetails()
        {
            var summary = new AuthoringPlanSummary
            {
                ToolName = "tool",
                RiskLevel = AuthoringRiskLevel.Destructive,
                UndoLevel = AuthoringUndoLevel.Partial,
                Targets = new[]
                {
                    new AuthoringTargetSummary
                    {
                        RelativePath = @"C:\\Users\\secret\\project\\Assets\\Thing.asset",
                        Name = new string('x', 400),
                    },
                },
                PredictedEffects = new[] { new string('y', 400) },
            };

            var bounded = summary.CloneBounded();
            bounded.Risk.ShouldBe("destructive");
            bounded.Undo.ShouldBe("partial");
            bounded.Targets[0].RelativePath.ShouldBeNull();
            bounded.Targets[0].Name!.Length.ShouldBeLessThanOrEqualTo(160);
            bounded.PredictedEffects[0].Length.ShouldBeLessThanOrEqualTo(160);
        }

        private static ToolCallContext ControlledContext(string callId)
            => new ToolCallContext
            {
                Version = ToolCallControl.CurrentVersion,
                RequestID = callId + "-request",
                CallId = callId,
                CorrelationId = callId + "-trace",
                DryRun = "none",
                Legacy = false,
                CancellationToken = CancellationToken.None,
            };

        private static IReadOnlyDictionary<string, JsonElement> EmptyArguments()
            => new Dictionary<string, JsonElement>();

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
                _ = namedParameters;
                _ = cancellationToken;
                Calls++;
                return Task.FromResult(ResponseCallTool.Success().SetRequestID(requestId));
            }
        }
    }
}
