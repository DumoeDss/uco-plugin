#nullable enable

using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;
using Shouldly;
using Xunit;
using static com.AtelierAI.Uco.Framework.Tests.Managers.AuthoringTestSupport;

namespace com.AtelierAI.Uco.Framework.Tests.Managers
{
    /// <summary>
    /// Task 4.1: strict none|validate|plan dispatch. Every unsupported or
    /// failing dry-run must fail closed with `dry_run_unsupported` (or the
    /// validator's own stable code) and must never reach the terminal.
    /// </summary>
    public sealed class AuthoringDryRunDispatchTests
    {
        [Fact]
        public async Task ValidateModeReturnsFactsWithoutPlanOrRunner()
        {
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };
            var context = ControlledContext("validate-call", "validate");

            var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, context, Arguments(("name", "Block")));

            response.Status.ShouldBe(ResponseStatus.Success);
            var result = response.StructuredContent!["result"]!;
            result["dryRun"]!.GetValue<string>().ShouldBe("validate");
            result["valid"]!.GetValue<bool>().ShouldBeTrue();
            result["validation"]!["targets"]![0]!["fingerprint"]!.GetValue<string>().ShouldBe("target-1");
            result["confirmationPlan"].ShouldBeNull();
            response.Transaction.ShouldBeNull();
            inspector.ValidateCalls.ShouldBe(1);
            inspector.PlanCalls.ShouldBe(0);
            runner.Calls.ShouldBe(0);
        }

        [Theory]
        [MemberData(nameof(AuthoringTestSupport.CredentialNameCases), MemberType = typeof(AuthoringTestSupport))]
        public async Task ValidatorDetailsOmitCredentialKeysAndPreserveSafeSiblings(
            string memberName,
            bool nested,
            bool shouldOmit)
        {
            const string value = "fixture-validator-credential";
            var inspector = new FakeInspector
            {
                ValidationDetails = AdapterObject(memberName, value, nested),
            };
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("validator-credentials", "validate"),
                EmptyArguments());

            response.Status.ShouldBe(ResponseStatus.Success);
            var details = response.StructuredContent!["result"]!["validation"]!["details"]!.AsObject();
            AssertAdapterObject(details, memberName, value, nested, shouldOmit);
            if (shouldOmit)
            {
                details.ToJsonString().ShouldNotContain(memberName, Case.Sensitive);
                details.ToJsonString().ShouldNotContain(value, Case.Sensitive);
            }
            runner.Calls.ShouldBe(0);
        }

        [Theory]
        [MemberData(nameof(AuthoringTestSupport.CredentialNameCases), MemberType = typeof(AuthoringTestSupport))]
        public async Task PlannerSummariesOmitCredentialKeysAndPreserveSafeSiblings(
            string memberName,
            bool nested,
            bool shouldOmit)
        {
            const string beforeValue = "fixture-before-credential";
            const string afterValue = "fixture-after-credential";
            var inspector = new FakeInspector
            {
                BeforeSummary = AdapterObject(memberName, beforeValue, nested),
                AfterSummary = AdapterObject(memberName, afterValue, nested),
            };
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                DestructiveHint = true,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };

            var middleware = new AuthoringSafetyMiddleware();
            var response = await ExecuteAsync(
                middleware,
                runner,
                ControlledContext("planner-credentials-dry-run", "plan"),
                EmptyArguments());
            var confirmation = await ExecuteAsync(
                middleware,
                runner,
                ControlledContext("planner-credentials-confirmation"),
                EmptyArguments());

            response.Status.ShouldBe(ResponseStatus.Success);
            confirmation.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            var plans = new[]
            {
                response.StructuredContent!["result"]!["confirmationPlan"]!,
                confirmation.StructuredError.Details!["confirmationPlan"]!,
            };
            foreach (var plan in plans)
            {
                var before = plan["before"]!.AsObject();
                var after = plan["after"]!.AsObject();
                AssertAdapterObject(before, memberName, beforeValue, nested, shouldOmit);
                AssertAdapterObject(after, memberName, afterValue, nested, shouldOmit);
                if (shouldOmit)
                {
                    plan.ToJsonString().ShouldNotContain(memberName, Case.Sensitive);
                    plan.ToJsonString().ShouldNotContain(beforeValue, Case.Sensitive);
                    plan.ToJsonString().ShouldNotContain(afterValue, Case.Sensitive);
                }
            }
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task AdapterJsonDoesNotInspectOrSerializeNonStringCredentialValues()
        {
            var inspector = new FakeInspector
            {
                ValidationDetails = new JsonObject
                {
                    ["safeSibling"] = "kept",
                    ["password"] = 123456,
                    ["nested"] = new JsonObject
                    {
                        ["safeNestedSibling"] = true,
                        ["secret"] = new JsonObject { ["buried"] = "fixture-object-credential" },
                    },
                },
                BeforeSummary = new JsonObject
                {
                    ["safeSibling"] = "kept",
                    ["authorization"] = false,
                },
                AfterSummary = new JsonObject
                {
                    ["safeSibling"] = "kept",
                    ["apiKey"] = new JsonArray("fixture-array-credential", 42),
                },
            };
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("non-string-credentials", "plan"),
                EmptyArguments());

            response.Status.ShouldBe(ResponseStatus.Success);
            var serialized = response.StructuredContent!["result"]!.ToJsonString();
            serialized.ShouldNotContain("password", Case.Sensitive);
            serialized.ShouldNotContain("123456", Case.Sensitive);
            serialized.ShouldNotContain("secret", Case.Sensitive);
            serialized.ShouldNotContain("fixture-object-credential", Case.Sensitive);
            serialized.ShouldNotContain("authorization", Case.Sensitive);
            serialized.ShouldNotContain("apiKey", Case.Sensitive);
            serialized.ShouldNotContain("fixture-array-credential", Case.Sensitive);
            serialized.ShouldContain("safeSibling", Case.Sensitive);
            serialized.ShouldContain("safeNestedSibling", Case.Sensitive);
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task InvalidArgumentsReturnValidationFailureWithoutRunner()
        {
            var inspector = new FakeInspector { Valid = false };
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };

            foreach (var mode in new[] { "validate", "plan" })
            {
                var response = await ExecuteAsync(
                    new AuthoringSafetyMiddleware(),
                    runner,
                    ControlledContext("invalid-" + mode, mode),
                    Arguments(("name", "Block")));

                response.Status.ShouldBe(ResponseStatus.Error);
                response.StructuredError!.Code.ShouldBe("validation_failed");
                response.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("fake_invalid");
            }

            inspector.PlanCalls.ShouldBe(0);
            runner.Calls.ShouldBe(0);
        }

        [Theory]
        [InlineData("validate")]
        [InlineData("plan")]
        public async Task OpaqueCapabilityIsUnsupportedForEveryDryRunMode(string mode)
        {
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("opaque")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(
                    AuthoringMutationKind.Modify,
                    AuthoringUndoLevel.Full,
                    inspector,
                    opaque: true),
            };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("opaque-" + mode, mode),
                EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);
            response.StructuredError.Details!["requestedMode"]!.GetValue<string>().ShouldBe(mode);
            response.StructuredError.Details!["supportedLevel"]!.GetValue<string>().ShouldBe("none");
            inspector.ValidateCalls.ShouldBe(0);
            inspector.PlanCalls.ShouldBe(0);
            runner.Calls.ShouldBe(0);
        }

        [Theory]
        [InlineData("validate")]
        [InlineData("plan")]
        public async Task DeferredCapabilityWithoutAdapterNeverFallsThroughToRunner(string mode)
        {
            // A build/test/bake/package style runner: destructive catalog
            // metadata, no authoring descriptor at all.
            var runner = new FakeRunTool("build-player") { DestructiveHint = true };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("deferred-" + mode, mode),
                EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);
            response.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("validator_unavailable");
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task ValidatorOnlyCapabilityCannotSilentlyDowngradePlanToValidate()
        {
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("validate-only")
            {
                ReadOnlyHint = false,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Modify,
                    UndoLevel = AuthoringUndoLevel.Full,
                    SupportsValidation = true,
                    Validator = inspector,
                    SupportsPlanning = false,
                },
            };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("validate-only", "plan"),
                EmptyArguments());

            response.Status.ShouldBe(ResponseStatus.Error);
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);
            response.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("planner_unavailable");
            response.StructuredError.Details!["supportedLevel"]!.GetValue<string>().ShouldBe("validate");
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task ValidatorExceptionIsUnsupportedAndRedacted()
        {
            var inspector = new FakeInspector
            {
                ValidatorException = new InvalidOperationException(@"C:\Users\secret\project\Assets\x"),
            };
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("validator-throws", "validate"),
                EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);
            response.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("validator_failed");
            response.StructuredError.Message.ShouldNotContain("secret");
            response.GetMessage()!.ShouldNotContain("secret");
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task PlannerExceptionIsUnsupportedWithoutRunner()
        {
            var inspector = new FakeInspector { PlannerException = new Exception("boom") };
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("planner-throws", "plan"),
                EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);
            response.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("planner_failed");
            response.StructuredError.Details!["supportedLevel"]!.GetValue<string>().ShouldBe("plan");
            response.StructuredError.Message.ShouldNotContain("boom");
            runner.Calls.ShouldBe(0);
        }

        [Theory]
        [InlineData("preview")]
        [InlineData("true")]
        [InlineData("")]
        public async Task ForeignDryRunValueIsInvalidControlBeforeAnyAdapter(string mode)
        {
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, inspector),
            };

            var response = await ExecuteAsync(
                new AuthoringSafetyMiddleware(),
                runner,
                ControlledContext("bad-mode", mode),
                EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.InvalidControl);
            inspector.ValidateCalls.ShouldBe(0);
            inspector.PlanCalls.ShouldBe(0);
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task ReadToolWithoutValidatorStillReportsUnsupportedDryRun()
        {
            // Reads are frictionless for `none`, but a dry-run is a
            // capability claim and is never fabricated.
            var runner = new FakeRunTool("read")
            {
                ReadOnlyHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor { MutationKind = AuthoringMutationKind.Read },
            };

            var validate = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, ControlledContext("read-validate", "validate"), EmptyArguments());
            validate.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DryRunUnsupported);

            var execute = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, ControlledContext("read-none"), EmptyArguments());
            execute.Status.ShouldBe(ResponseStatus.Success);
            runner.Calls.ShouldBe(1);
        }

        private static JsonObject AdapterObject(string memberName, string value, bool nested)
        {
            var members = new JsonObject
            {
                ["safeSibling"] = "kept",
                [memberName] = value,
            };
            return nested
                ? new JsonObject
                {
                    ["safeSibling"] = "kept",
                    ["nested"] = members,
                }
                : members;
        }

        private static void AssertAdapterObject(
            JsonObject value,
            string memberName,
            string expectedValue,
            bool nested,
            bool shouldOmit)
        {
            value["safeSibling"]!.GetValue<string>().ShouldBe("kept");
            var members = nested ? value["nested"]!.AsObject() : value;
            if (nested)
                members["safeSibling"]!.GetValue<string>().ShouldBe("kept");
            if (shouldOmit)
                members[memberName].ShouldBeNull();
            else
                members[memberName]!.GetValue<string>().ShouldBe(expectedValue);
        }
    }
}
