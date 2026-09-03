#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using Shouldly;
using Xunit;

namespace com.IvanMurzak.McpPlugin.Tests.Mcp
{
    /// <summary>
    /// Task 4.3: the confirmation-plan cache is process-local, bounded, and
    /// strictly time-limited. It is not an operation registry: it holds only
    /// digests and bounded summaries and forgets everything on invalidation.
    /// </summary>
    public sealed class ConfirmationPlanStoreTests
    {
        [Fact]
        public void DefaultLifetimeIsFiveMinutesAndCappedByDeadline()
        {
            var now = 1_000_000L;
            var store = new ConfirmationPlanStore(ConfirmationPlanStore.DefaultPlanLifetime, () => now);

            var unbounded = store.Issue("sha256-a", new AuthoringPlanSummary(), AuthoringSafetyPolicy.PolicyVersion);
            unbounded.ExpiresAtUnixMs.ShouldBe(now + (long)TimeSpan.FromMinutes(5).TotalMilliseconds);

            var capped = store.Issue("sha256-b", new AuthoringPlanSummary(), AuthoringSafetyPolicy.PolicyVersion, deadlineUnixMs: now + 1_500);
            capped.ExpiresAtUnixMs.ShouldBe(now + 1_500);
            capped.Summary.ExpiresAtUnixMs.ShouldBe(now + 1_500);
            capped.PlanId.ShouldStartWith("plan-");
            capped.PlanId.ShouldNotBe(unbounded.PlanId);
        }

        [Fact]
        public void IssueRefusesAPlanWhoseDeadlineAlreadyPassed()
        {
            var now = 5_000L;
            var store = new ConfirmationPlanStore(TimeSpan.FromMinutes(5), () => now);

            var exception = Should.Throw<ToolCallControlException>(() => store.Issue(
                "sha256-a",
                new AuthoringPlanSummary(),
                AuthoringSafetyPolicy.PolicyVersion,
                deadlineUnixMs: now));

            exception.Code.ShouldBe(ToolCallErrorCodes.DeadlineExceeded);
            store.Count.ShouldBe(0);
        }

        [Fact]
        public void ExpiredPlansArePrunedButKnownUntilExpiry()
        {
            var now = 10_000L;
            var store = new ConfirmationPlanStore(TimeSpan.FromMilliseconds(100), () => now);
            var record = store.Issue("sha256-a", new AuthoringPlanSummary(), AuthoringSafetyPolicy.PolicyVersion);

            store.TryGet(record.PlanId, out _).ShouldBeTrue();
            now += 99;
            store.Count.ShouldBe(1);
            now += 1;
            // Lookups distinguish known-but-expired from unknown until a
            // prune runs; a count/issue/prune sweeps the dead record.
            store.TryGet(record.PlanId, out var stillKnown).ShouldBeTrue();
            stillKnown!.ExpiresAtUnixMs.ShouldBeLessThanOrEqualTo(now);
            store.Count.ShouldBe(0);
            store.TryGet(record.PlanId, out _).ShouldBeFalse();
        }

        [Fact]
        public void CapacityIsBoundedAndEvictsOldestPlanFirst()
        {
            var now = 0L;
            var store = new ConfirmationPlanStore(TimeSpan.FromMinutes(5), () => now, maxRecords: 3);
            var first = store.Issue("sha256-1", new AuthoringPlanSummary(), 1);
            var second = store.Issue("sha256-2", new AuthoringPlanSummary(), 1);
            var third = store.Issue("sha256-3", new AuthoringPlanSummary(), 1);
            store.Count.ShouldBe(3);

            var fourth = store.Issue("sha256-4", new AuthoringPlanSummary(), 1);

            store.Count.ShouldBe(3);
            store.MaxRecords.ShouldBe(3);
            store.TryGet(first.PlanId, out _).ShouldBeFalse();
            store.TryGet(second.PlanId, out _).ShouldBeTrue();
            store.TryGet(third.PlanId, out _).ShouldBeTrue();
            store.TryGet(fourth.PlanId, out _).ShouldBeTrue();
        }

        [Fact]
        public void DefaultCapacityIsExplicitAndFinite()
        {
            var store = new ConfirmationPlanStore();
            store.MaxRecords.ShouldBe(ConfirmationPlanStore.DefaultMaxRecords);
            ConfirmationPlanStore.DefaultMaxRecords.ShouldBeGreaterThan(0);
            for (var index = 0; index < ConfirmationPlanStore.DefaultMaxRecords + 10; index++)
                store.Issue("sha256-" + index, new AuthoringPlanSummary(), 1);
            store.Count.ShouldBe(ConfirmationPlanStore.DefaultMaxRecords);
        }

        [Fact]
        public void ConsumeAndInvalidateForgetPlans()
        {
            var store = new ConfirmationPlanStore(TimeSpan.FromMinutes(5), () => 0);
            var kept = store.Issue("sha256-a", new AuthoringPlanSummary(), 1);
            var consumed = store.Issue("sha256-b", new AuthoringPlanSummary(), 1);

            store.Consume(consumed.PlanId).ShouldBeTrue();
            store.Consume(consumed.PlanId).ShouldBeFalse();
            store.TryGet(kept.PlanId, out _).ShouldBeTrue();

            var generation = store.Generation;
            store.Invalidate();
            store.Generation.ShouldBe(generation + 1);
            store.TryGet(kept.PlanId, out _).ShouldBeFalse();
            store.Count.ShouldBe(0);
        }

        [Fact]
        public async Task TryConsumeAllowsExactlyOneConcurrentRedemption()
        {
            var store = new ConfirmationPlanStore(TimeSpan.FromMinutes(5), () => 0);
            var record = store.Issue("sha256-once", new AuthoringPlanSummary(), 1);
            var ready = new ManualResetEventSlim(false);
            var start = new ManualResetEventSlim(false);
            var readyCount = 0;

            async Task<bool> ConsumeAsync()
            {
                return await Task.Run(() =>
                {
                    if (Interlocked.Increment(ref readyCount) == 2)
                        ready.Set();
                    start.Wait();
                    return store.TryConsume(
                        record.PlanId,
                        record.PlanHash,
                        record.ExpiresAtUnixMs,
                        out _);
                });
            }

            var first = ConsumeAsync();
            var second = ConsumeAsync();
            ready.Wait();
            start.Set();
            var results = await Task.WhenAll(first, second);

            results.ShouldContain(true);
            results.ShouldContain(false);
            results.Length.ShouldBe(2);
            store.TryGet(record.PlanId, out _).ShouldBeFalse();
        }

        [Fact]
        public void EachStoreInstanceHasItsOwnIdentity()
        {
            using var first = new ConfirmationPlanStore();
            using var second = new ConfirmationPlanStore();
            first.InstanceId.ShouldStartWith("instance-");
            first.InstanceId.ShouldNotBe(second.InstanceId);
            var record = first.Issue("sha256-a", new AuthoringPlanSummary(), 1);
            record.InstanceId.ShouldBe(first.InstanceId);
            second.TryGet(record.PlanId, out _).ShouldBeFalse();
        }

        [Fact]
        public void DisposedStoreRecognisesNothingAndRefusesIssue()
        {
            var store = new ConfirmationPlanStore();
            var record = store.Issue("sha256-a", new AuthoringPlanSummary(), 1);
            store.Dispose();

            store.TryGet(record.PlanId, out _).ShouldBeFalse();
            Should.Throw<ObjectDisposedException>(() => store.Issue("sha256-b", new AuthoringPlanSummary(), 1));
        }

        [Fact]
        public void RecordKeepsOnlyBoundedSummaryData()
        {
            var store = new ConfirmationPlanStore();
            var record = store.Issue(
                "sha256-a",
                new AuthoringPlanSummary
                {
                    Targets = new[]
                    {
                        new AuthoringTargetSummary { RelativePath = @"C:\host\secret.txt", Name = new string('n', 500) },
                    },
                },
                1);

            record.Summary.Targets[0].RelativePath.ShouldBeNull();
            record.Summary.Targets[0].Name!.Length.ShouldBe(160);
            record.Summary.PlanHash.ShouldBe("sha256-a");
        }
    }
}
