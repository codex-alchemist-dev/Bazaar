using Bazaar.Kodo.Anchoring;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Anchoring;

public sealed class AnchorPlannerTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NothingNewIsNeverDueEvenAfterALongTime()
    {
        FakeTimeProvider time = new(_start);
        AnchorPlanner planner = new(timeProvider: time);
        time.Advance(TimeSpan.FromDays(3));

        AnchorPlan plan = planner.Evaluate(100, new AnchorProgress(100, _start));

        Assert.False(plan.ShouldAnchor);
        Assert.Equal(AnchorPlanReason.NotDue, plan.Reason);
    }

    [Theory]
    [InlineData(999, false)]
    [InlineData(1000, true)]
    [InlineData(1001, true)]
    public void RecordCountBoundary(long newRecords, bool due)
    {
        FakeTimeProvider time = new(_start);
        AnchorPlanner planner = new(timeProvider: time);

        AnchorPlan plan = planner.Evaluate(50 + newRecords, new AnchorProgress(50, _start));

        Assert.Equal(due, plan.ShouldAnchor);
        Assert.Equal(due ? AnchorPlanReason.RecordCount : AnchorPlanReason.NotDue, plan.Reason);
    }

    [Fact]
    public void IntervalBoundaryNeedsAtLeastOneNewRecord()
    {
        FakeTimeProvider time = new(_start);
        AnchorPlanner planner = new(timeProvider: time);
        AnchorProgress last = new(10, _start);

        time.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromTicks(1));
        Assert.False(planner.Evaluate(11, last).ShouldAnchor);

        time.Advance(TimeSpan.FromTicks(1));
        AnchorPlan plan = planner.Evaluate(11, last);
        Assert.True(plan.ShouldAnchor);
        Assert.Equal(AnchorPlanReason.Interval, plan.Reason);
        Assert.False(planner.Evaluate(10, last).ShouldAnchor);
    }

    [Fact]
    public void FailedAttemptWaitsForTheRetryInterval()
    {
        FakeTimeProvider time = new(_start);
        AnchorPlanner planner = new(timeProvider: time);
        AnchorProgress last = new(0, _start);
        time.Advance(TimeSpan.FromMinutes(20));
        DateTimeOffset failedAt = time.GetUtcNow();

        time.Advance(TimeSpan.FromSeconds(59));
        AnchorPlan wait = planner.Evaluate(5, last, failedAt);
        Assert.False(wait.ShouldAnchor);
        Assert.Equal(AnchorPlanReason.RetryWait, wait.Reason);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(planner.Evaluate(5, last, failedAt).ShouldAnchor);
    }

    [Fact]
    public void CustomOptionsAreHonouredAndInvalidOnesRejected()
    {
        FakeTimeProvider time = new(_start);
        AnchorPlanner planner = new(new AnchorPlanOptions { MaxRecords = 5 }, time);
        Assert.True(planner.Evaluate(5, new AnchorProgress(0, _start)).ShouldAnchor);

        Assert.Throws<ArgumentException>(() => new AnchorPlanner(new AnchorPlanOptions { MaxRecords = 0 }));
        Assert.Throws<ArgumentException>(() => new AnchorPlanner(new AnchorPlanOptions { MaxInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new AnchorPlanner(new AnchorPlanOptions { RetryInterval = TimeSpan.FromDays(1) }));
    }
}
