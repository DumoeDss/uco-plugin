#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using com.AtelierAI.Uco.Framework.Common.Model;
using Shouldly;
using Xunit;
using static com.AtelierAI.Uco.Framework.Tests.Managers.AuthoringTestSupport;

namespace com.AtelierAI.Uco.Framework.Tests.Managers
{
    /// <summary>
    /// Tasks 5.1 and 5.6: one transaction per top-level call, disposal on
    /// every path, truthful full/partial/none and complete/partial/none
    /// rollback reporting, and no durable state.
    /// </summary>
    public sealed class AuthoringTransactionTests
    {
        [Fact]
        public void CompleteCollapsesOnceAndReportsRecordedObjects()
        {
            var completes = 0;
            var aborts = 0;
            var created = new List<object>();
            var transaction = new AuthoringTransaction(
                "Uco: create",
                AuthoringUndoLevel.Full,
                groupId: 7,
                recordCreated: created.Add,
                complete: () => completes++,
                abort: () => aborts++);

            transaction.RecordCreated("go");
            transaction.MarkMutated();
            transaction.Complete();
            transaction.Complete();
            transaction.Dispose();

            completes.ShouldBe(1);
            aborts.ShouldBe(0);
            created.Count.ShouldBe(1);
            var report = transaction.Report;
            report.Completed.ShouldBeTrue();
            report.Aborted.ShouldBeFalse();
            report.Mutated.ShouldBeTrue();
            report.Undo.ShouldBe("full");
            report.GroupId.ShouldBe(7);
            report.GroupLabel.ShouldBe("Uco: create");
            report.Rollback.ShouldBe(AuthoringRollbackStatus.None);
            report.AffectedObjects.Count.ShouldBe(1);
            report.ToJson()["undo"]!.GetValue<string>().ShouldBe("full");
        }

        [Fact]
        public void DisposeWithoutCompleteAbortsExactlyOnce()
        {
            var aborts = 0;
            var transaction = new AuthoringTransaction("t", AuthoringUndoLevel.Full, abort: () => aborts++);
            transaction.RecordModified("x", completeSnapshot: true);
            transaction.MarkMutated();

            transaction.Dispose();
            transaction.Dispose();
            transaction.Abort();

            aborts.ShouldBe(1);
            transaction.Report.Aborted.ShouldBeTrue();
            transaction.Report.Completed.ShouldBeFalse();
            transaction.Report.Rollback.ShouldBe(AuthoringRollbackStatus.Complete);
            Should.Throw<InvalidOperationException>(() => transaction.RecordCreated("late"));
        }

        [Fact]
        public void CompleteCallbackExceptionAbortsAndPropagates()
        {
            var aborts = 0;
            var transaction = new AuthoringTransaction(
                "t",
                AuthoringUndoLevel.Full,
                complete: () => throw new InvalidOperationException("collapse failed"),
                abort: () => aborts++);
            transaction.RecordCreated("go");
            transaction.MarkMutated();

            Should.Throw<InvalidOperationException>(() => transaction.Complete());

            aborts.ShouldBe(1);
            transaction.Report.Completed.ShouldBeFalse();
            transaction.Report.Aborted.ShouldBeTrue();
            transaction.Report.Mutated.ShouldBeTrue();
            transaction.Report.Rollback.ShouldBe(AuthoringRollbackStatus.Complete);
        }

        [Fact]
        public void AbortReportsCompletePartialAndNoneTruthfully()
        {
            var full = new AuthoringTransaction("full", AuthoringUndoLevel.Full, abort: () => { });
            full.RecordModified("x", completeSnapshot: true);
            full.MarkMutated();
            full.Abort();
            full.Report.Rollback.ShouldBe(AuthoringRollbackStatus.Complete);
            full.Report.Undo.ShouldBe("full");

            var partialSnapshot = new AuthoringTransaction("partial-snapshot", AuthoringUndoLevel.Full, abort: () => { });
            partialSnapshot.RecordModified("x", completeSnapshot: false);
            partialSnapshot.MarkMutated();
            partialSnapshot.Abort();
            partialSnapshot.Report.Rollback.ShouldBe(AuthoringRollbackStatus.Partial);
            partialSnapshot.Report.Undo.ShouldBe("partial");

            var partialCapability = new AuthoringTransaction("partial", AuthoringUndoLevel.Partial, abort: () => { });
            partialCapability.RecordModified("x", completeSnapshot: true);
            partialCapability.MarkMutated();
            partialCapability.Abort();
            partialCapability.Report.Rollback.ShouldBe(AuthoringRollbackStatus.Partial);
            partialCapability.Report.Undo.ShouldBe("partial");

            var throwingRevert = new AuthoringTransaction("throwing", AuthoringUndoLevel.Full, abort: () => throw new Exception("revert failed"));
            throwingRevert.RecordDeleted("x");
            throwingRevert.MarkMutated();
            throwingRevert.Abort();
            throwingRevert.Report.Rollback.ShouldBe(AuthoringRollbackStatus.Partial);
            throwingRevert.Report.Undo.ShouldBe("partial");
            throwingRevert.Report.Aborted.ShouldBeTrue();

            var noRevert = new AuthoringTransaction("none", AuthoringUndoLevel.None);
            noRevert.MarkMutated();
            noRevert.Abort();
            noRevert.Report.Rollback.ShouldBe(AuthoringRollbackStatus.None);
            noRevert.Report.Undo.ShouldBe("none");

            var noneCapabilityWithRevert = new AuthoringTransaction("none-with-callback", AuthoringUndoLevel.None, abort: () => { });
            noneCapabilityWithRevert.MarkMutated();
            noneCapabilityWithRevert.Abort();
            noneCapabilityWithRevert.Report.Rollback.ShouldBe(AuthoringRollbackStatus.None);
        }

        [Fact]
        public void MutationBeforeRecordingIsNeverReportedAsFullUndo()
        {
            var transaction = new AuthoringTransaction("t", AuthoringUndoLevel.Full, complete: () => { }, abort: () => { });
            transaction.MarkMutated(); // regression: mutated before any record
            transaction.RecordModified("x", completeSnapshot: true);
            transaction.Complete();

            transaction.Report.Undo.ShouldBe("partial");
            transaction.Report.Mutated.ShouldBeTrue();
        }

        [Fact]
        public void HostRegisteredObjectsAppearInReportWithoutHostCallback()
        {
            var created = 0;
            var transaction = new AuthoringTransaction("t", AuthoringUndoLevel.Full, recordCreated: _ => created++, complete: () => { });
            transaction.RecordHostRegistered("component");
            transaction.MarkMutated();
            transaction.Complete();

            created.ShouldBe(0);
            transaction.Report.AffectedObjects.Count.ShouldBe(1);
            transaction.Report.Undo.ShouldBe("full");
        }

        [Fact]
        public void ReportNeverAdvertisesFullWithoutRecords()
        {
            var transaction = new AuthoringTransaction("t", AuthoringUndoLevel.Full, complete: () => { });
            transaction.Complete();
            transaction.Report.Undo.ShouldBe("partial");
            transaction.Report.Mutated.ShouldBeFalse();
        }

        [Fact]
        public void ReportJsonIsBoundedAndSafe()
        {
            var transaction = new AuthoringTransaction("t", AuthoringUndoLevel.Full, complete: () => { });
            for (var index = 0; index < 100; index++)
                transaction.RecordCreated(new string('n', 400));
            transaction.MarkMutated();
            transaction.Complete();

            var json = transaction.Report.ToJson();
            json["affectedObjects"]!.AsArray().Count.ShouldBe(64);
            json["affectedObjects"]![0]!["name"]!.GetValue<string>().Length.ShouldBe(160);
            json["rollback"]!.GetValue<string>().ShouldBe("none");
            json["completed"]!.GetValue<bool>().ShouldBeTrue();
        }

        [Fact]
        public async Task MiddlewareOpensOneTransactionAndAttachesReportOnSuccess()
        {
            var transactions = new RecordingTransactionFactory();
            var runner = new FakeRunTool("create")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Create, AuthoringUndoLevel.Full, new FakeInspector(), transactions),
            };
            var middleware = new AuthoringSafetyMiddleware();

            IAuthoringTransaction? observed = null;
            var response = await ExecuteAsync(middleware, runner, ControlledContext("tx"), EmptyArguments(), _ =>
            {
                observed = AuthoringTransactionScope.Current;
                observed!.RecordCreated("go");
                observed.MarkMutated();
                return Task.FromResult(ResponseCallTool.Success("created"));
            });

            response.Status.ShouldBe(ResponseStatus.Success);
            transactions.BeginCalls.ShouldBe(1);
            transactions.CompleteCalls.ShouldBe(1);
            transactions.AbortCalls.ShouldBe(0);
            response.Transaction!["undo"]!.GetValue<string>().ShouldBe("full");
            response.Transaction["mutated"]!.GetValue<bool>().ShouldBeTrue();
            response.Transaction["completed"]!.GetValue<bool>().ShouldBeTrue();
            AuthoringTransactionScope.Current.ShouldBeNull();
        }

        [Fact]
        public async Task ToolErrorBeforeMutationKeepsToolErrorAndRevertsNothing()
        {
            var transactions = new RecordingTransactionFactory();
            var runner = new FakeRunTool("create")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Create, AuthoringUndoLevel.Full, new FakeInspector(), transactions),
            };

            var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, ControlledContext("tool-error"), EmptyArguments(), _ =>
                Task.FromResult(ResponseCallTool.Error(new ToolCallError(ToolCallErrorCodes.ToolExecutionFailed, "GameObject not found."))));

            response.Status.ShouldBe(ResponseStatus.Error);
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ToolExecutionFailed);
            response.StructuredError.Message.ShouldBe("GameObject not found.");
            transactions.AbortCalls.ShouldBe(1);
            response.Transaction!["mutated"]!.GetValue<bool>().ShouldBeFalse();
            response.Transaction["aborted"]!.GetValue<bool>().ShouldBeTrue();
        }

        [Fact]
        public async Task RunnerExceptionAfterCompleteRecordingIsRevertedTruthfully()
        {
            var transactions = new RecordingTransactionFactory();
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, new FakeInspector(), transactions),
            };

            var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, ControlledContext("runner-throws"), EmptyArguments(), _ =>
            {
                var transaction = AuthoringTransactionScope.Current!;
                transaction.RecordModified("go", completeSnapshot: true);
                transaction.MarkMutated();
                throw new InvalidOperationException(@"C:\host\secret");
            });

            response.Status.ShouldBe(ResponseStatus.Error);
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.ToolExecutionFailed);
            response.StructuredError.Message.ShouldNotContain("secret");
            transactions.AbortCalls.ShouldBe(1);
            response.Transaction!["rollback"]!.GetValue<string>().ShouldBe("complete");
            response.Transaction["completed"]!.GetValue<bool>().ShouldBeFalse();
        }

        [Fact]
        public async Task IncompleteSnapshotFailureReportsTransactionFailed()
        {
            var transactions = new RecordingTransactionFactory();
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, new FakeInspector(), transactions),
            };

            var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, ControlledContext("partial-snapshot"), EmptyArguments(), _ =>
            {
                var transaction = AuthoringTransactionScope.Current!;
                transaction.RecordModified("nested", completeSnapshot: false);
                transaction.MarkMutated();
                return Task.FromResult(ResponseCallTool.Error(new ToolCallError(ToolCallErrorCodes.ToolExecutionFailed, "patch failed")));
            });

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.AuthoringTransactionFailed);
            response.StructuredError.Details!["rollback"]!.GetValue<string>().ShouldBe("partial");
            response.StructuredError.Details["stage"]!.GetValue<string>().ShouldBe("abort");
            response.Transaction!["undo"]!.GetValue<string>().ShouldBe("partial");
        }

        [Fact]
        public async Task ExternalOperationWithoutRevertReportsNoneNotDurableRollback()
        {
            var transactions = new RecordingTransactionFactory { ProvideAbort = false, AdvertisedUndo = AuthoringUndoLevel.Partial };
            var runner = new FakeRunTool("scene-create")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Create, AuthoringUndoLevel.Partial, new FakeInspector(), transactions),
            };

            var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, ControlledContext("external"), EmptyArguments(), _ =>
            {
                AuthoringTransactionScope.Current!.MarkMutated();
                throw new Exception("asset write failed");
            });

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.AuthoringTransactionFailed);
            response.StructuredError.Details!["rollback"]!.GetValue<string>().ShouldBe("none");
            response.Transaction!["undo"]!.GetValue<string>().ShouldBe("partial");
            response.Transaction["rollback"]!.GetValue<string>().ShouldBe("none");
        }

        [Fact]
        public async Task CompleteFailureAfterAcceptedMutationIsNotReportedAsSuccess()
        {
            var transactions = new RecordingTransactionFactory { CompleteException = new InvalidOperationException("collapse failed") };
            var runner = new FakeRunTool("create")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Create, AuthoringUndoLevel.Full, new FakeInspector(), transactions),
            };

            var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), runner, ControlledContext("collapse"), EmptyArguments(), _ =>
            {
                AuthoringTransactionScope.Current!.RecordCreated("go");
                AuthoringTransactionScope.Current!.MarkMutated();
                return Task.FromResult(ResponseCallTool.Success("created"));
            });

            response.Status.ShouldBe(ResponseStatus.Error);
            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.AuthoringTransactionFailed);
            response.StructuredError.Details!["stage"]!.GetValue<string>().ShouldBe("complete");
            transactions.AbortCalls.ShouldBe(1);
            response.Transaction!["aborted"]!.GetValue<bool>().ShouldBeTrue();
        }

        [Fact]
        public async Task NonCooperativeCancellationAfterMutationAbortsOnlyThisTransaction()
        {
            var transactions = new RecordingTransactionFactory();
            var runner = new FakeRunTool("modify")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Modify, AuthoringUndoLevel.Full, new FakeInspector(), transactions),
            };
            using var cancellation = new CancellationTokenSource();
            var context = ControlledContext("cancel-late");
            context.CancellationToken = cancellation.Token;
            var pipeline = new ToolExecutionPipeline(new IToolExecutionMiddleware[] { new AuthoringSafetyMiddleware() });

            ResponseCallTool response;
            using (ToolCallInvocationScope.Push(context, new AuthoringInvocation(context, runner.Name, EmptyArguments(), runner)))
            {
                response = await pipeline.InvokeAsync(context, invocationContext =>
                {
                    // The runner ignores its token until after it mutated.
                    var transaction = AuthoringTransactionScope.Current!;
                    transaction.RecordModified("go", completeSnapshot: true);
                    transaction.MarkMutated();
                    cancellation.Cancel();
                    invocationContext.CancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(ResponseCallTool.Success());
                });
            }

            response.StructuredError!.Code.ShouldBe(ToolCallErrorCodes.Cancelled);
            transactions.AbortCalls.ShouldBe(1);
            transactions.CompleteCalls.ShouldBe(0);
            transactions.Last!.Report.Rollback.ShouldBe(AuthoringRollbackStatus.Complete);
        }

        [Fact]
        public async Task ReadAndDryRunCallsNeverOpenATransaction()
        {
            var transactions = new RecordingTransactionFactory();
            var inspector = new FakeInspector();
            var runner = new FakeRunTool("create")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Create, AuthoringUndoLevel.Full, inspector, transactions),
            };
            var readRunner = new FakeRunTool("read")
            {
                ReadOnlyHint = true,
                AuthoringCapability = new AuthoringCapabilityDescriptor
                {
                    MutationKind = AuthoringMutationKind.Read,
                    TransactionFactory = transactions,
                },
            };
            var middleware = new AuthoringSafetyMiddleware();

            (await ExecuteAsync(middleware, runner, ControlledContext("v", "validate"), EmptyArguments())).Status.ShouldBe(ResponseStatus.Success);
            (await ExecuteAsync(middleware, runner, ControlledContext("p", "plan"), EmptyArguments())).Status.ShouldBe(ResponseStatus.Success);
            (await ExecuteAsync(middleware, readRunner, ControlledContext("r"), EmptyArguments())).Status.ShouldBe(ResponseStatus.Success);

            transactions.BeginCalls.ShouldBe(0);
            runner.Calls.ShouldBe(0);
            readRunner.Calls.ShouldBe(1);
        }

        [Fact]
        public async Task ChildCallInsideParentScopeAttachesToParentTransaction()
        {
            var childTransactions = new RecordingTransactionFactory();
            var child = new FakeRunTool("gameobject-create")
            {
                ReadOnlyHint = false,
                AuthoringCapability = Capability(AuthoringMutationKind.Create, AuthoringUndoLevel.Full, new FakeInspector(), childTransactions),
            };
            var parentTransaction = new AuthoringTransaction("Uco: batch-execute", AuthoringUndoLevel.Partial, complete: () => { }, abort: () => { });
            var context = ControlledContext("child");
            context.ParentCallId = "batch-parent";

            IAuthoringTransaction? observed = null;
            using (AuthoringTransactionScope.Push(parentTransaction))
            {
                var response = await ExecuteAsync(new AuthoringSafetyMiddleware(), child, context, EmptyArguments(), _ =>
                {
                    observed = AuthoringTransactionScope.Current;
                    observed!.RecordCreated("child-go");
                    observed.MarkMutated();
                    return Task.FromResult(ResponseCallTool.Success());
                });
                response.Status.ShouldBe(ResponseStatus.Success);
            }

            ReferenceEquals(observed, parentTransaction).ShouldBeTrue();
            childTransactions.BeginCalls.ShouldBe(0);
            parentTransaction.Report.Mutated.ShouldBeTrue();
        }
    }
}
