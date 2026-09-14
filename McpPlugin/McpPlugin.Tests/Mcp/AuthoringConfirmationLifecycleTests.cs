#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;
using Shouldly;
using Xunit;
using static com.AtelierAI.Uco.Framework.Tests.Mcp.AuthoringTestSupport;

namespace com.AtelierAI.Uco.Framework.Tests.Mcp
{
    /// <summary>
    /// Tasks 4.4 and 4.6: the confirmation binding covers the tool, canonical
    /// arguments, logical context, deadline, policy version, paths, targets,
    /// and Undo level. Anything else than the exact inspected action is
    /// rejected with a stable code and never reaches the terminal.
    /// </summary>
    public sealed class AuthoringConfirmationLifecycleTests
    {
        private static readonly IReadOnlyDictionary<string, JsonElement> BlockArguments = Arguments(("name", "Block"));

        [Fact]
        public async Task UnknownCapabilityIssuesArgumentBoundRecordAndExecutesExactRetryOnce()
        {
            var runner = new FakeRunTool("opaque-tool");
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("argument-bound");

            var issued = await IssueAsync(middleware, runner, context, BlockArguments);

            issued.Plan.PlanLevel.ShouldBe("none");
            issued.Plan.Undo.ShouldBe("none");
            issued.Plan.Targets.ShouldBeEmpty();
            issued.Plan.PredictedEffects.ShouldBe(new[] { "undeclared" });
            issued.Plan.BeforeSummary.ShouldBeNull();
            issued.Plan.AfterSummary.ShouldBeNull();
            runner.Calls.ShouldBe(0);

            var changed = await ExecuteAsync(
                middleware,
                runner,
                Confirmed(context, issued.Token),
                Arguments(("name", "Other")));
            changed.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            runner.Calls.ShouldBe(0);

            var exact = await ExecuteAsync(
                middleware,
                runner,
                Confirmed(context, issued.Token),
                BlockArguments);
            exact.Status.ShouldBe(ResponseStatus.Success);
            runner.Calls.ShouldBe(1);

            var replay = await ExecuteAsync(
                middleware,
                runner,
                Confirmed(context, issued.Token),
                BlockArguments);
            replay.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationInvalid);
            runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task IssuedRetryPreservesExactNormalizedControlAndUnknownMembers()
        {
            var runner = new FakeRunTool("opaque-tool");
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("exact-retry");
            context.ParentCallId = "parent-call";
            context.DeadlineUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
            context.CancellationId = "cancel-opaque";
            context.IdempotencyKey = "idempotency-opaque";
            using (var member = JsonDocument.Parse("{\"generation\":7,\"enabled\":true}"))
                context.UnknownMembers["futureControl"] = member.RootElement.Clone();

            var issued = await ExecuteAsync(middleware, runner, context, BlockArguments);

            issued.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            var retry = issued.StructuredError.Details!["retryWith"]!.AsObject();
            retry["requestID"]!.GetValue<string>().ShouldBe(context.RequestID);
            var retryControlJson = retry["control"]!.ToJsonString();
            retryControlJson.ShouldNotContain("IssueConfirmationOnly");
            retryControlJson.ShouldNotContain("CancellationToken");
            retryControlJson.ShouldNotContain("Legacy");
            var retryControl = JsonSerializer.Deserialize<ToolCallControl>(retryControlJson)!;
            retryControl.Confirm.ShouldBe(true);
            retryControl.DryRun.ShouldBe("none");
            retryControl.CallId.ShouldBe(context.CallId);
            retryControl.CorrelationId.ShouldBe(context.CorrelationId);
            retryControl.ParentCallId.ShouldBe(context.ParentCallId);
            retryControl.DeadlineUnixMs.ShouldBe(context.DeadlineUnixMs);
            retryControl.CancellationId.ShouldBe(context.CancellationId);
            retryControl.IdempotencyKey.ShouldBe(context.IdempotencyKey);
            retryControl.UnknownMembers["futureControl"].GetProperty("generation").GetInt32().ShouldBe(7);

            var changedControl = retryControl.Clone();
            using (var member = JsonDocument.Parse("{\"generation\":8,\"enabled\":true}"))
                changedControl.UnknownMembers["futureControl"] = member.RootElement.Clone();
            var changedContext = ToolCallContextNormalizer.Normalize(changedControl, context.RequestID);
            var stale = await ExecuteAsync(middleware, runner, changedContext, BlockArguments);
            stale.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            runner.Calls.ShouldBe(0);

            var exactContext = ToolCallContextNormalizer.Normalize(retryControl, context.RequestID);
            var exact = await ExecuteAsync(middleware, runner, exactContext, BlockArguments);
            exact.Status.ShouldBe(ResponseStatus.Success);
            runner.Calls.ShouldBe(1);
        }

        [Theory]
        [InlineData("confirm")]
        [InlineData("CoNfIrM")]
        [InlineData("requestID")]
        [InlineData("CaNcElLaTiOnToKeN")]
        public async Task ReservedUnknownControlCollisionSuppressesCopyableRetry(string memberName)
        {
            var runner = new FakeRunTool("opaque-tool");
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("collision-suppressed-retry");
            using (var member = JsonDocument.Parse("\"Bearer fixture-collision-secret\""))
                context.UnknownMembers[memberName] = member.RootElement.Clone();

            var issued = await ExecuteAsync(middleware, runner, context, BlockArguments);

            issued.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            issued.StructuredError.Details!["retryWith"].ShouldBeNull();
            issued.StructuredError.Details!["retrySuppressedReason"]!
                .GetValue<string>().ShouldBe("retry_control_member_collision");
            issued.StructuredError.Details!.ToJsonString().ShouldNotContain("fixture-collision-secret");
            runner.Calls.ShouldBe(0);
        }

        [Theory]
        [MemberData(nameof(AuthoringTestSupport.CredentialNameCases), MemberType = typeof(AuthoringTestSupport))]
        public async Task CredentialNameVariantsAreStructurallySuppressed(
            string memberName,
            bool nested,
            bool shouldSuppress)
        {
            const string secret = "fixture-credential-secret";
            var runner = new FakeRunTool("opaque-tool");
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("credential-name-retry");
            using (var member = JsonDocument.Parse(nested
                ? JsonSerializer.Serialize(new Dictionary<string, string> { [memberName] = secret })
                : JsonSerializer.Serialize(secret)))
            {
                context.UnknownMembers[nested ? "futureControl" : memberName] = member.RootElement.Clone();
            }

            var issued = await ExecuteAsync(middleware, runner, context, BlockArguments);

            issued.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            if (shouldSuppress)
            {
                issued.StructuredError.Details!["retryWith"].ShouldBeNull();
                issued.StructuredError.Details!["retrySuppressedReason"]!
                    .GetValue<string>().ShouldBe("retry_control_sensitive_or_unbounded");
                issued.StructuredError.Details!.ToJsonString().ShouldNotContain(secret);
            }
            else
            {
                issued.StructuredError.Details!["retryWith"].ShouldNotBeNull();
                issued.StructuredError.Details!["retrySuppressedReason"].ShouldBeNull();
            }
            runner.Calls.ShouldBe(0);
        }

        [Theory]
        [InlineData("authorization", "Bearer fixture-secret", "retry_control_sensitive_or_unbounded")]
        [InlineData("futureControl", null, "retry_control_limits_exceeded")]
        public async Task UnsafeBoundControlSuppressesCopyableRetry(
            string memberName,
            string? memberValue,
            string expectedReason)
        {
            var runner = new FakeRunTool("opaque-tool");
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("suppressed-retry");
            using (var member = JsonDocument.Parse(JsonSerializer.Serialize(memberValue ?? new string('x', 161))))
                context.UnknownMembers[memberName] = member.RootElement.Clone();

            var issued = await ExecuteAsync(middleware, runner, context, BlockArguments);

            issued.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            issued.StructuredError.Details!["retryWith"].ShouldBeNull();
            issued.StructuredError.Details!["retrySuppressedReason"]!
                .GetValue<string>().ShouldBe(expectedReason);
            issued.StructuredError.Details!.ToJsonString().ShouldNotContain("fixture-secret");
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task ValidatorOnlyCapabilityIssuesValidateRecordAndRevalidatesExactRetry()
        {
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("validate-only")
            {
                DestructiveHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Delete,
                    UndoLevel = AuthoringUndoLevel.None,
                    SupportsValidation = true,
                    Validator = inspector,
                },
            };
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("validate-record");

            var issued = await IssueAsync(middleware, runner, context, BlockArguments);

            issued.Plan.PlanLevel.ShouldBe("validate");
            issued.Plan.Targets.Single().Fingerprint.ShouldBe("target-1");
            inspector.ValidateCalls.ShouldBe(1);
            inspector.PlanCalls.ShouldBe(0);
            runner.Calls.ShouldBe(0);

            var exact = await ExecuteAsync(
                middleware,
                runner,
                Confirmed(context, issued.Token),
                BlockArguments);

            exact.Status.ShouldBe(ResponseStatus.Success);
            inspector.ValidateCalls.ShouldBe(2);
            inspector.PlanCalls.ShouldBe(0);
            runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task ArgumentBoundRecordBecomesStaleWhenPlannerAppears()
        {
            var runner = new FakeRunTool("upgrade") { DestructiveHint = true };
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("level-upgrade");
            var issued = await IssueAsync(middleware, runner, context, BlockArguments);

            runner.AuthoringCapability = Capability(
                AuthoringMutationKind.Delete,
                AuthoringUndoLevel.None,
                new FakeInspector());
            var response = await ExecuteAsync(
                middleware,
                runner,
                Confirmed(context, issued.Token),
                BlockArguments);

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            response.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("plan_level_changed");
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task IssueOnlyModeNeverCallsFrictionlessRunner()
        {
            var runner = new FakeRunTool("read")
            {
                ReadOnlyHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Read,
                },
            };
            var context = ControlledContext("issue-only");
            context.IssueConfirmationOnly = true;

            var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, context, BlockArguments);

            response.Status.ShouldBe(ResponseStatus.Success);
            var decision = response.StructuredContent!["result"]!;
            decision["confirmationRequired"]!.GetValue<bool>().ShouldBeFalse();
            decision["risk"]!.GetValue<string>().ShouldBe("read");
            decision["undo"]!.GetValue<string>().ShouldBe("none");
            runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task MatchingPlanExecutesExactlyOnceAndIsConsumed()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("exact");
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);

            var execute = Confirmed(context, token);
            var first = await ExecuteAsync(fixture.Middleware, fixture.Runner, execute, BlockArguments);
            first.Status.ShouldBe(ResponseStatus.Success);
            fixture.Runner.Calls.ShouldBe(1);
            fixture.Inspector.PlanCalls.ShouldBe(2); // issue + re-inspection at acceptance

            // A replayed token is not a standing capability.
            var replay = await ExecuteAsync(fixture.Middleware, fixture.Runner, Confirmed(context, token), BlockArguments);
            replay.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationInvalid);
            replay.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("unknown_plan");
            fixture.Runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task PlanResponseCarriesBoundedTokenAndNoMutation()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("plan-shape", "plan");
            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);

            response.Status.ShouldBe(ResponseStatus.Success);
            var plan = response.StructuredContent!["result"]!["confirmationPlan"]!;
            plan["planId"]!.GetValue<string>().ShouldStartWith("plan-");
            plan["planHash"]!.GetValue<string>().ShouldStartWith("sha256-");
            plan["expiresAtUnixMs"]!.GetValue<long>().ShouldBeGreaterThan(fixture.Store.NowUnixMs());
            plan["policyVersion"]!.GetValue<int>().ShouldBe(AuthoringSafetyPolicy.PolicyVersion);
            plan["risk"]!.GetValue<string>().ShouldBe("destructive");
            plan["undo"]!.GetValue<string>().ShouldBe("none");
            plan["tool"]!.GetValue<string>().ShouldBe("delete");
            plan["callId"]!.GetValue<string>().ShouldBe("plan-shape");
            plan["targets"]![0]!["fingerprint"]!.GetValue<string>().ShouldBe("target-1");
            plan["predictedEffects"]![0]!.GetValue<string>().ShouldBe("modify Block");
            plan["argumentsHash"]!.GetValue<string>().ShouldStartWith("sha256-");
            response.StructuredContent.ToJsonString().ShouldNotContain("\"arguments\":{");
            response.Transaction.ShouldBeNull();
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task ChangedTargetFingerprintIsStale()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("target-changed");
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);

            // The scene changed underneath the approval.
            fixture.Inspector.Fingerprint = "target-2";
            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, Confirmed(context, token), BlockArguments);

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            response.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("arguments_or_targets_changed");
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task ChangedPathBindingIsStale()
        {
            using var project = TempProject.Create();
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("scene-create")
            {
                ReadOnlyHint = false,
                DestructiveHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Create,
                    UndoLevel = AuthoringUndoLevel.None,
                    SupportsValidation = true,
                    SupportsPlanning = true,
                    Validator = inspector,
                    Planner = inspector,
                    PathBindings = new[]
                    {
                        new AuthoringPathBinding
                        {
                            ArgumentName = "path",
                            Intent = AuthoringPathAccessIntent.Create,
                            RootCategory = ProjectPathRootCategories.Assets,
                            Required = true,
                        },
                    },
                },
            };
            var middleware = new AuthoringSafetyMiddleware(new AuthoringSafetyPolicy(project.Policy));
            var context = ControlledContext("path-changed");
            var token = await PlanAsync(middleware, runner, context, Arguments(("path", "Assets/Scenes/A.unity")));

            var otherPath = await ExecuteAsync(middleware, runner, Confirmed(context, token), Arguments(("path", "Assets/Scenes/B.unity")));
            otherPath.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);

            // Equivalent spelling of the same canonical path is the same action.
            var sameCanonical = await ExecuteAsync(middleware, runner, Confirmed(context, token), Arguments(("path", @"Assets\Scenes\..\Scenes\A.unity")));
            sameCanonical.Status.ShouldBe(ResponseStatus.Success);
            runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task ContainedReparseRetargetIsStaleBeforeTokenConsumption()
        {
            using var project = TempProject.Create();
            var fake = new FakeCanonicalizer(project.Root, caseSensitive: false);
            var link = Path.Combine(project.Root, "Assets", "Generated");
            var targetA = Path.Combine(project.Root, "Assets", "SafeA");
            var targetB = Path.Combine(project.Root, "Assets", "SafeB");
            fake.AddReparse(link, targetA);
            var policy = new ProjectPathPolicy(fake);
            var runner = PathRunner("reparse-retarget");
            var middleware = new AuthoringSafetyMiddleware(new AuthoringSafetyPolicy(policy));
            var context = ControlledContext("reparse-retarget");
            var arguments = Arguments(("path", "Assets/Generated/file.txt"));
            var token = await PlanAsync(middleware, runner, context, arguments);

            fake.SetReparseTarget(link, targetB);
            var stale = await ExecuteAsync(middleware, runner, Confirmed(context, token), arguments);

            stale.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            stale.StructuredError.Details!["reason"]!.GetValue<string>()
                .ShouldBe("arguments_or_targets_changed");
            runner.Calls.ShouldBe(0);

            fake.SetReparseTarget(link, targetA);
            var equivalentArguments = Arguments(("path", @"assets\GENERATED\file.txt"));
            var unchanged = await ExecuteAsync(
                middleware,
                runner,
                Confirmed(context, token),
                equivalentArguments);
            unchanged.Status.ShouldBe(ResponseStatus.Success);
            runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task OutsideRootRetargetFailsClosedWithoutConsumingOrDisclosingTarget()
        {
            using var project = TempProject.Create();
            var fake = new FakeCanonicalizer(project.Root, caseSensitive: false);
            var link = Path.Combine(project.Root, "Assets", "Generated");
            var targetA = Path.Combine(project.Root, "Assets", "SafeA");
            var outside = Path.Combine(Path.GetTempPath(), "private-host-target");
            fake.AddReparse(link, targetA);
            var policy = new ProjectPathPolicy(fake);
            var runner = PathRunner("reparse-escape");
            var middleware = new AuthoringSafetyMiddleware(new AuthoringSafetyPolicy(policy));
            var context = ControlledContext("reparse-escape");
            var arguments = Arguments(("path", @"assets\GENERATED\file.txt"));
            var token = await PlanAsync(middleware, runner, context, arguments);

            fake.SetReparseTarget(link, outside);
            var rejected = await ExecuteAsync(middleware, runner, Confirmed(context, token), arguments);

            rejected.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.PathPolicyViolation);
            rejected.StructuredError.Details!.ToJsonString().ShouldNotContain(outside, Case.Sensitive);
            rejected.StructuredError.Details!.ToJsonString().ShouldNotContain(project.Root, Case.Sensitive);
            runner.Calls.ShouldBe(0);

            fake.SetReparseTarget(link, targetA);
            (await ExecuteAsync(middleware, runner, Confirmed(context, token), arguments))
                .Status.ShouldBe(ResponseStatus.Success);
            runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task RetargetDuringAcceptanceIsStaleBeforeTokenConsumption()
        {
            using var project = TempProject.Create();
            var fake = new FakeCanonicalizer(project.Root, caseSensitive: false);
            var link = Path.Combine(project.Root, "Assets", "Generated");
            var targetA = Path.Combine(project.Root, "Assets", "SafeA");
            var targetB = Path.Combine(project.Root, "Assets", "SafeB");
            fake.AddReparse(link, targetA);
            var policy = new ProjectPathPolicy(fake);
            var runner = PathRunner("reparse-race");
            var inspector = (FakeInspector)runner.AuthoringCapability!.Planner!;
            var middleware = new AuthoringSafetyMiddleware(new AuthoringSafetyPolicy(policy));
            var context = ControlledContext("reparse-race");
            var arguments = Arguments(("path", "Assets/Generated/file.txt"));
            var token = await PlanAsync(middleware, runner, context, arguments);
            inspector.OnPlan = () => fake.SetReparseTarget(link, targetB);

            var stale = await ExecuteAsync(middleware, runner, Confirmed(context, token), arguments);

            stale.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            stale.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("path_target_changed");
            runner.Calls.ShouldBe(0);

            inspector.OnPlan = null;
            fake.SetReparseTarget(link, targetA);
            (await ExecuteAsync(middleware, runner, Confirmed(context, token), arguments))
                .Status.ShouldBe(ResponseStatus.Success);
            runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task ChangedDeadlineOrLogicalIdsAreStale()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("ids");
            context.DeadlineUnixMs = fixture.Store.NowUnixMs() + 60_000;
            context.ParentCallId = "parent-1";
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);

            var laterDeadline = Confirmed(context, token);
            laterDeadline.DeadlineUnixMs = context.DeadlineUnixMs + 1;
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, laterDeadline, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);

            var otherCorrelation = Confirmed(context, token);
            otherCorrelation.CorrelationId = "another-trace";
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, otherCorrelation, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);

            var otherParent = Confirmed(context, token);
            otherParent.ParentCallId = "parent-2";
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, otherParent, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);

            var otherRequest = Confirmed(context, token);
            otherRequest.RequestID = "another-request";
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, otherRequest, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);

            var otherOpaque = Confirmed(context, token);
            otherOpaque.UnknownMembers["futureFlag"] = JsonDocument.Parse("7").RootElement.Clone();
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, otherOpaque, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);

            fixture.Runner.Calls.ShouldBe(0);

            // The exact context still executes.
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, Confirmed(context, token), BlockArguments))
                .Status.ShouldBe(ResponseStatus.Success);
            fixture.Runner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task ExpiredPlanIsRejectedWithExpiredCode()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("expiry");
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);

            fixture.Now += (long)ConfirmationPlanStore.DefaultPlanLifetime.TotalMilliseconds;
            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, Confirmed(context, token), BlockArguments);

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationExpired);
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task PlanExpiryIsCappedByCallDeadline()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("deadline-cap");
            context.DeadlineUnixMs = fixture.Now + 2_000;
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);
            token.ExpiresAtUnixMs.ShouldBe(fixture.Now + 2_000);

            fixture.Now += 2_000;
            // The deadline itself is now reached; the middleware stops before
            // confirmation acceptance with g-004's deadline code.
            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, Confirmed(context, token), BlockArguments);
            response.StructuredError!.Code.ShouldBeOneOf(ToolCallErrorCodes.ConfirmationExpired, ToolCallErrorCodes.DeadlineExceeded);
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task DomainReloadOrRestartInvalidatesPlan()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("reload");
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);

            // Unity clears the store before an assembly reload ...
            fixture.Store.Invalidate();
            var afterReload = await ExecuteAsync(fixture.Middleware, fixture.Runner, Confirmed(context, token), BlockArguments);
            afterReload.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationInvalid);

            // ... and a restarted Editor has a brand new store/instance id.
            var restarted = Fixture.Destructive();
            var afterRestart = await ExecuteAsync(restarted.Middleware, restarted.Runner, Confirmed(context, token), BlockArguments);
            afterRestart.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationInvalid);
            fixture.Runner.Calls.ShouldBe(0);
            restarted.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task CrossInstanceTokenIsRejected()
        {
            var issuer = Fixture.Destructive();
            var verifier = Fixture.Destructive();
            var context = ControlledContext("cross-instance");
            var token = await PlanAsync(issuer.Middleware, issuer.Runner, context, BlockArguments);

            // Even if the other instance were told about the plan id, the
            // binding was computed with the issuer's instance id.
            verifier.Store.Issue(token.PlanId!, token.PlanHash!, token.ExpiresAtUnixMs!.Value, AuthoringSafetyPolicy.PolicyVersion, new AuthoringPlanSummary());
            var response = await ExecuteAsync(verifier.Middleware, verifier.Runner, Confirmed(context, token), BlockArguments);

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationStale);
            verifier.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task MissingOrMalformedTokensUseStableCodes()
        {
            var fixture = Fixture.Destructive();
            var context = ControlledContext("malformed");

            var missing = await ExecuteAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);
            missing.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            missing.StructuredError.Details!["confirmationPlan"]!["planLevel"]!
                .GetValue<string>().ShouldBe("plan");
            missing.StructuredError.Details!["retryWith"]!["control"]!["confirm"]!
                .GetValue<bool>().ShouldBeTrue();

            var confirmWithoutToken = context.Clone();
            confirmWithoutToken.Confirm = true;
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, confirmWithoutToken, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);

            var tokenWithoutConfirm = context.Clone();
            tokenWithoutConfirm.Confirmation = new ToolCallConfirmation { PlanId = "plan-x", PlanHash = "sha256-x", ExpiresAtUnixMs = fixture.Now + 1000 };
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, tokenWithoutConfirm, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);

            var malformed = context.Clone();
            malformed.Confirm = true;
            malformed.Confirmation = new ToolCallConfirmation { PlanId = "plan-x", PlanHash = "", ExpiresAtUnixMs = fixture.Now + 1000 };
            var malformedResponse = await ExecuteAsync(fixture.Middleware, fixture.Runner, malformed, BlockArguments);
            malformedResponse.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationInvalid);
            malformedResponse.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("malformed_token");

            var unknownExpired = context.Clone();
            unknownExpired.Confirm = true;
            unknownExpired.Confirmation = new ToolCallConfirmation { PlanId = "plan-unknown", PlanHash = "sha256-x", ExpiresAtUnixMs = fixture.Now - 1 };
            (await ExecuteAsync(fixture.Middleware, fixture.Runner, unknownExpired, BlockArguments))
                .StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationExpired);

            var tamperedHash = context.Clone();
            var real = await PlanAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);
            tamperedHash.Confirm = true;
            tamperedHash.Confirmation = new ToolCallConfirmation { PlanId = real.PlanId, PlanHash = "sha256-tampered", ExpiresAtUnixMs = real.ExpiresAtUnixMs };
            var tampered = await ExecuteAsync(fixture.Middleware, fixture.Runner, tamperedHash, BlockArguments);
            tampered.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationInvalid);
            tampered.StructuredError.Details!["reason"]!.GetValue<string>().ShouldBe("token_binding_mismatch");

            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task LegacyRiskyCallIsRejectedWithCopyableRetryInstruction()
        {
            var fixture = Fixture.Destructive();
            var context = LegacyContext("legacy-delete");

            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, context, BlockArguments);

            response.Status.ShouldBe(ResponseStatus.Error);
            response.RequestID.ShouldBe("legacy-delete");
            var text = response.GetMessage()!;
            text.ShouldStartWith("[" + ToolCallErrorCodes.ConfirmationRequired + "]");
            text.ShouldContain("\"control\": {}");
            text.ShouldContain("\"confirm\": true");
            text.ShouldContain("confirmation");
            text.ShouldNotContain("Block"); // no argument echo
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task LegacyReadAndOrdinaryUndoableCallsStayFrictionless()
        {
            var read = new FakeRunTool("read")
            {
                ReadOnlyHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor { MutationKind = AuthoringMutationKind.Read },
            };
            var transactions = new RecordingTransactionFactory();
            var modify = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, new FakeInspector(), transactions),
            };
            var middleware = new AuthoringSafetyMiddleware();

            var readResponse = await ExecuteAsync(middleware, read, LegacyContext("legacy-read"), EmptyArguments());
            readResponse.Status.ShouldBe(ResponseStatus.Success);
            readResponse.Transaction.ShouldBeNull();

            var modifyResponse = await ExecuteAsync(middleware, modify, LegacyContext("legacy-modify"), BlockArguments);
            modifyResponse.Status.ShouldBe(ResponseStatus.Success);
            modifyResponse.Transaction.ShouldNotBeNull();
            transactions.BeginCalls.ShouldBe(1);
            read.Calls.ShouldBe(1);
            modify.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task UndoNoneMutationRequiresConfirmationAndOpensNoTransaction()
        {
            var transactions = new RecordingTransactionFactory();
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("scene-save")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.None, inspector, transactions),
            };
            var middleware = new AuthoringSafetyMiddleware();
            var context = ControlledContext("undo-none");

            var unconfirmed = await ExecuteAsync(middleware, runner, context, BlockArguments);
            unconfirmed.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ConfirmationRequired);

            var token = await PlanAsync(middleware, runner, context, BlockArguments);
            var confirmed = await ExecuteAsync(middleware, runner, Confirmed(context, token), BlockArguments);
            confirmed.Status.ShouldBe(ResponseStatus.Success);
            confirmed.Transaction.ShouldNotBeNull();
            confirmed.Transaction!["undo"]!.GetValue<string>().ShouldBe("none");
            confirmed.Transaction["mutated"]!.GetValue<bool>().ShouldBeFalse();
            confirmed.Transaction["rollback"]!.GetValue<string>().ShouldBe("none");
            transactions.BeginCalls.ShouldBe(0);
            runner.Calls.ShouldBe(1);
        }

        private static ToolCallContext Confirmed(ToolCallContext context, ToolCallConfirmation token)
        {
            var execute = context.Clone();
            execute.DryRun = "none";
            execute.Confirm = true;
            execute.Confirmation = token.Clone();
            return execute;
        }

        private sealed class Fixture
        {
            // The store clock is test-controlled, but deadlines are compared
            // against the real clock by the pipeline/middleware guards, so
            // start from "now" and only move the store clock forward.
            public long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            public ConfirmationPlanStore Store = null!;
            public AuthoringSafetyMiddleware Middleware = null!;
            public FakeRunTool Runner = null!;
            public FakeInspector Inspector = null!;

            public static Fixture Destructive()
            {
                var fixture = new Fixture();
                fixture.Store = new ConfirmationPlanStore(ConfirmationPlanStore.DefaultPlanLifetime, () => fixture.Now);
                fixture.Inspector = new FakeInspector();
                fixture.Runner = new FakeRunTool("delete")
                {
                    DestructiveHint = true,
                    AuthoringCapability = Capability(AuthoringMutationKind.Delete, AuthoringUndoLevel.None, fixture.Inspector),
                };
                fixture.Middleware = new AuthoringSafetyMiddleware(new AuthoringSafetyPolicy(null, fixture.Store));
                return fixture;
            }
        }

        private static FakeRunTool PathRunner(string name)
        {
            var inspector = new FakeInspector();
            return new FakeRunTool(name)
            {
                ReadOnlyHint = false,
                DestructiveHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Modify,
                    UndoLevel = AuthoringUndoLevel.None,
                    SupportsValidation = true,
                    SupportsPlanning = true,
                    Validator = inspector,
                    Planner = inspector,
                    PathBindings = new[]
                    {
                        new AuthoringPathBinding
                        {
                            ArgumentName = "path",
                            Intent = AuthoringPathAccessIntent.Modify,
                            RootCategory = ProjectPathRootCategories.Assets,
                            Required = true,
                        },
                    },
                },
            };
        }

        private sealed class FakeCanonicalizer : IProjectPathCanonicalizer
        {
            private readonly StringComparer _comparer;
            private readonly HashSet<string> _existing;
            private readonly Dictionary<string, string?> _reparseTargets;

            public FakeCanonicalizer(string projectRoot, bool caseSensitive)
            {
                ProjectRoot = Path.GetFullPath(projectRoot);
                IsCaseSensitive = caseSensitive;
                _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                _existing = new HashSet<string>(_comparer);
                _reparseTargets = new Dictionary<string, string?>(_comparer);
            }

            public string ProjectRoot { get; }
            public bool IsCaseSensitive { get; }
            public string Canonicalize(string path) => Path.GetFullPath(path);
            public bool Exists(string fullPath)
                => _existing.Contains(Canonicalize(fullPath))
                    || File.Exists(fullPath)
                    || Directory.Exists(fullPath);
            public bool IsDirectory(string fullPath) => Directory.Exists(fullPath);
            public bool IsReparsePoint(string fullPath)
                => _reparseTargets.ContainsKey(Canonicalize(fullPath));

            public bool TryResolveReparsePoint(string fullPath, out string resolvedFullPath)
            {
                if (_reparseTargets.TryGetValue(Canonicalize(fullPath), out var target)
                    && !string.IsNullOrWhiteSpace(target))
                {
                    resolvedFullPath = Canonicalize(target);
                    return true;
                }

                resolvedFullPath = string.Empty;
                return false;
            }

            public void AddReparse(string path, string target)
            {
                _existing.Add(Canonicalize(path));
                _reparseTargets[Canonicalize(path)] = Canonicalize(target);
            }

            public void SetReparseTarget(string path, string target)
                => _reparseTargets[Canonicalize(path)] = Canonicalize(target);
        }

        private sealed class TempProject : IDisposable
        {
            private TempProject(string root)
            {
                Root = root;
                Policy = new ProjectPathPolicy(new FileSystemProjectPathCanonicalizer(root, caseSensitive: false));
            }

            public string Root { get; }
            public ProjectPathPolicy Policy { get; }

            public static TempProject Create()
            {
                var root = Path.Combine(Path.GetTempPath(), "mcp-confirmation-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(root, "Assets", "Scenes"));
                Directory.CreateDirectory(Path.Combine(root, "Packages"));
                return new TempProject(root);
            }

            public void Dispose()
            {
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, recursive: true);
                }
                catch
                {
                    // cleanup only
                }
            }
        }
    }
}
