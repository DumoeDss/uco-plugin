#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using Shouldly;
using Xunit;
using static com.IvanMurzak.McpPlugin.Tests.Mcp.AuthoringTestSupport;

namespace com.IvanMurzak.McpPlugin.Tests.Mcp
{
    /// <summary>
    /// Task 6.5: expiry or cancellation observed before planning, before
    /// confirmation acceptance, before transaction start, inside a later
    /// middleware, or at g-004's terminal recheck never produces authoring
    /// state. No planner, transaction, or runner runs after the stop.
    /// </summary>
    public sealed class AuthoringDeadlineOrderingTests
    {
        [Fact]
        public async Task ExpiredDeadlineStopsBeforePlanning()
        {
            var fixture = new Fixture();
            var context = ControlledContext("expired-plan", "plan");
            context.DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;

            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, context, EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            response.StructuredError.Details!["stage"]!.GetValue<string>().ShouldBe("before_planning");
            fixture.Inspector.ValidateCalls.ShouldBe(0);
            fixture.Inspector.PlanCalls.ShouldBe(0);
            fixture.Transactions.BeginCalls.ShouldBe(0);
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task CancelledCallerStopsBeforeConfirmationAcceptance()
        {
            var fixture = new Fixture();
            var context = ControlledContext("cancel-before-confirm");
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, EmptyArguments());
            var plansBefore = fixture.Inspector.PlanCalls;

            using var cancellation = new CancellationTokenSource();
            var execute = context.Clone();
            execute.Confirm = true;
            execute.Confirmation = token;
            execute.CancellationToken = cancellation.Token;
            cancellation.Cancel();

            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, execute, EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.Cancelled);
            fixture.Inspector.PlanCalls.ShouldBe(plansBefore); // no re-inspection, no acceptance
            fixture.Transactions.BeginCalls.ShouldBe(0);
            fixture.Runner.Calls.ShouldBe(0);
            // The plan was not consumed by a cancelled acceptance.
            fixture.Store.TryGet(token.PlanId!, out _).ShouldBeTrue();
        }

        [Fact]
        public async Task CancellationDuringAcceptanceStopsBeforeTransactionStart()
        {
            var fixture = new Fixture();
            var context = ControlledContext("cancel-before-tx");
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, EmptyArguments());

            using var cancellation = new CancellationTokenSource();
            var execute = context.Clone();
            execute.Confirm = true;
            execute.Confirmation = token;
            execute.CancellationToken = cancellation.Token;
            // Cancel while the policy re-inspects the target, i.e. after the
            // pre-confirmation check already passed.
            fixture.Inspector.OnPlan = () => cancellation.Cancel();

            var response = await ExecuteAsync(fixture.Middleware, fixture.Runner, execute, EmptyArguments());

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.Cancelled);
            response.StructuredError.Details!["stage"]!.GetValue<string>().ShouldBe("before_transaction");
            fixture.Transactions.BeginCalls.ShouldBe(0);
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task CancellationWhileQueuedStopsBeforeConfirmationConsumptionAndTransaction()
        {
            var scheduler = new BlockingScheduler();
            var fixture = new Fixture(scheduler: scheduler);
            var context = ControlledContext("cancel-in-queue");
            var token = await PlanAsync(fixture.Middleware, fixture.Runner, context, EmptyArguments());
            using var cancellation = new CancellationTokenSource();
            var execute = context.Clone();
            execute.Confirm = true;
            execute.Confirmation = token;
            execute.CancellationToken = cancellation.Token;

            var execution = ExecuteAsync(fixture.Middleware, fixture.Runner, execute, EmptyArguments());
            await scheduler.Entered.Task;
            cancellation.Cancel();
            var response = await execution;

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.Cancelled);
            response.StructuredError.Details!["stage"]!.GetValue<string>().ShouldBe("scheduler_queue");
            fixture.Store.TryGet(token.PlanId!, out _).ShouldBeTrue();
            fixture.Transactions.BeginCalls.ShouldBe(0);
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task CancellationInsideLaterMiddlewareIsCaughtByTerminalRecheck()
        {
            var fixture = new Fixture(requireConfirmation: false);
            using var cancellation = new CancellationTokenSource();
            var context = ControlledContext("cancel-in-middleware");
            context.CancellationToken = cancellation.Token;
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[]
            {
                fixture.Middleware,
                new DelegateMiddleware(async (invocationContext, next) =>
                {
                    cancellation.Cancel();
                    return await next(invocationContext).ConfigureAwait(false);
                }),
            });

            ResponseCallTool response;
            using (ToolCallInvocationScope.Push(context, new AuthoringInvocation(context, fixture.Runner.Name, EmptyArguments(), fixture.Runner)))
            {
                response = await pipeline.InvokeAsync(context, _ =>
                {
                    fixture.Runner.Calls++;
                    return Task.FromResult(ResponseCallTool.Success());
                });
            }

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.Cancelled);
            fixture.Runner.Calls.ShouldBe(0);
            // The transaction had been opened by the outer safety middleware
            // and is aborted without a mutation.
            fixture.Transactions.BeginCalls.ShouldBe(1);
            fixture.Transactions.AbortCalls.ShouldBe(1);
            fixture.Transactions.Last!.Report.Mutated.ShouldBeFalse();
        }

        [Fact]
        public async Task DeadlineInsideLaterMiddlewareIsCaughtByTerminalRecheck()
        {
            var fixture = new Fixture(requireConfirmation: false);
            var context = ControlledContext("deadline-in-middleware");
            context.DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 150;
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[]
            {
                fixture.Middleware,
                new DelegateMiddleware(async (invocationContext, next) =>
                {
                    await Task.Delay(400).ConfigureAwait(false);
                    return await next(invocationContext).ConfigureAwait(false);
                }),
            });

            ResponseCallTool response;
            using (ToolCallInvocationScope.Push(context, new AuthoringInvocation(context, fixture.Runner.Name, EmptyArguments(), fixture.Runner)))
            {
                response = await pipeline.InvokeAsync(context, _ =>
                {
                    fixture.Runner.Calls++;
                    return Task.FromResult(ResponseCallTool.Success());
                });
            }

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            fixture.Runner.Calls.ShouldBe(0);
        }

        [Fact]
        public async Task PipelinePreCheckStopsExpiredCallBeforeSafetyMiddleware()
        {
            var fixture = new Fixture();
            var context = ControlledContext("expired-pipeline", "plan");
            context.DeadlineUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[] { fixture.Middleware });

            ResponseCallTool response;
            using (ToolCallInvocationScope.Push(context, new AuthoringInvocation(context, fixture.Runner.Name, EmptyArguments(), fixture.Runner)))
            {
                response = await pipeline.InvokeAsync(context, _ => Task.FromResult(ResponseCallTool.Success()));
            }

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            fixture.Inspector.PlanCalls.ShouldBe(0);
        }

        private sealed class DelegateMiddleware : IToolExecutionMiddleware
        {
            private readonly Func<ToolCallContext, ToolCallNext, Task<ResponseCallTool>> _invoke;
            public DelegateMiddleware(Func<ToolCallContext, ToolCallNext, Task<ResponseCallTool>> invoke) => _invoke = invoke;
            public Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next) => _invoke(context, next);
        }

        private sealed class Fixture
        {
            public ConfirmationPlanStore Store { get; }
            public FakeInspector Inspector { get; } = new FakeInspector();
            public RecordingTransactionFactory Transactions { get; } = new RecordingTransactionFactory();
            public FakeRunTool Runner { get; }
            public AuthoringSafetyMiddleware Middleware { get; }

            public Fixture(
                bool requireConfirmation = true,
                IToolExecutionScheduler? scheduler = null)
            {
                Store = new ConfirmationPlanStore();
                Runner = new FakeRunTool(requireConfirmation ? "delete" : "modify")
                {
                    ReadOnlyHint = false,
                    DestructiveHint = requireConfirmation ? true : (bool?)null,
                    AuthoringCapability = Capability(
                        requireConfirmation ? AuthoringMutationKind.Delete : AuthoringMutationKind.Modify,
                        AuthoringUndoLevel.Full,
                        Inspector,
                        Transactions),
                };
                Middleware = new AuthoringSafetyMiddleware(
                    new AuthoringSafetyPolicy(null, Store), scheduler);
            }
        }

        private sealed class BlockingScheduler : IToolExecutionScheduler
        {
            public TaskCompletionSource<bool> Entered { get; }
                = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<IToolExecutionLease> AcquireAsync(
                ToolExecutionSchedulingRequest request,
                CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
        }
    }
}
