using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Api;

/// <summary>
/// The <c>unobserved</c> derivation (doc 46 section 8.3). Each boundary is checked at exactly the budget (still observed) and one
/// tick past it (unobserved), so the edge is pinned rather than assumed.
/// </summary>
public sealed class KodoUnobservedTests
{
    private static readonly DateTimeOffset _now = KodoApiTestData.Now;
    private static readonly KodoUnobservedBudgets _budgets = KodoUnobservedBudgets.Defaults;

    [Fact]
    public void DefaultsAreTheThirtySecondHeartbeatAndTheProposedSixtySecondFreshness()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), KodoUnobservedBudgets.Defaults.Heartbeat);
        Assert.Equal(TimeSpan.FromSeconds(60), KodoUnobservedBudgets.Defaults.Freshness);
    }

    [Fact]
    public void FreshStreamShowsTheLastKnownStateNotUnobserved()
    {
        KodoDisplayState display = KodoUnobserved.Derive(KodoState.Degraded, _now, _now, _now, _budgets);

        Assert.Equal(KodoDisplayState.Degraded, display);
    }

    [Fact]
    public void SilenceExactlyAtTheHeartbeatBudgetIsStillObserved()
    {
        KodoDisplayState display = KodoUnobserved.Derive(
            KodoState.Healthy, _now, _now.AddSeconds(-30), _now, _budgets);

        Assert.Equal(KodoDisplayState.Healthy, display);
    }

    [Fact]
    public void SilenceOneTickPastTheHeartbeatBudgetIsUnobserved()
    {
        KodoDisplayState display = KodoUnobserved.Derive(
            KodoState.Healthy, _now, _now.AddSeconds(-30).AddTicks(-1), _now, _budgets);

        Assert.Equal(KodoDisplayState.Unobserved, display);
    }

    [Fact]
    public void SnapshotExactlyAtTheFreshnessBudgetIsStillObserved()
    {
        KodoDisplayState display = KodoUnobserved.Derive(
            KodoState.Healthy, _now, _now, _now.AddSeconds(-60), _budgets);

        Assert.Equal(KodoDisplayState.Healthy, display);
    }

    [Fact]
    public void SnapshotOneTickPastTheFreshnessBudgetIsUnobserved()
    {
        KodoDisplayState display = KodoUnobserved.Derive(
            KodoState.Healthy, _now, _now, _now.AddSeconds(-60).AddTicks(-1), _budgets);

        Assert.Equal(KodoDisplayState.Unobserved, display);
    }

    [Fact]
    public void StaleSnapshotIsUnobservedEvenWhenEventsKeepArriving()
    {
        // Heartbeats prove the stream is alive, but a snapshot older than its budget is still not fresh (section 8.3).
        KodoDisplayState display = KodoUnobserved.Derive(
            KodoState.Healthy, _now, _now, _now.AddMinutes(-5), _budgets);

        Assert.Equal(KodoDisplayState.Unobserved, display);
    }

    [Fact]
    public void SilentStreamIsUnobservedEvenWhenTheSnapshotIsFresh()
    {
        KodoDisplayState display = KodoUnobserved.Derive(
            KodoState.Healthy, _now, _now.AddMinutes(-1), _now, _budgets);

        Assert.Equal(KodoDisplayState.Unobserved, display);
    }

    [Fact]
    public void NoEventEverReceivedIsUnobserved()
    {
        Assert.Equal(KodoDisplayState.Unobserved, KodoUnobserved.Derive(KodoState.Healthy, _now, null, _now, _budgets));
    }

    [Fact]
    public void NoSnapshotEverReceivedIsUnobserved()
    {
        Assert.Equal(KodoDisplayState.Unobserved, KodoUnobserved.Derive(KodoState.Healthy, _now, _now, null, _budgets));
    }

    [Fact]
    public void NoStateEverReceivedIsUnobserved()
    {
        Assert.Equal(KodoDisplayState.Unobserved, KodoUnobserved.Derive(null, _now, _now, _now, _budgets));
    }

    [Fact]
    public void ReceiveTimeInTheFutureCountsAsAgeZero()
    {
        // The client clock stepped back after the receive. The age is clamped to zero, so the display is not flipped by it.
        KodoDisplayState display = KodoUnobserved.Derive(
            KodoState.Healthy, _now, _now.AddSeconds(5), _now.AddSeconds(5), _budgets);

        Assert.Equal(KodoDisplayState.Healthy, display);
    }

    [Fact]
    public void FrozenStaysFrozenWhileObserved()
    {
        KodoDisplayState display = KodoUnobserved.Derive(KodoState.Frozen, _now, _now, _now, _budgets);

        Assert.Equal(KodoDisplayState.Frozen, display);
    }

    [Theory]
    [InlineData(KodoState.Healthy, KodoDisplayState.Healthy)]
    [InlineData(KodoState.Degraded, KodoDisplayState.Degraded)]
    [InlineData(KodoState.Failing, KodoDisplayState.Failing)]
    [InlineData(KodoState.Recovering, KodoDisplayState.Recovering)]
    [InlineData(KodoState.Frozen, KodoDisplayState.Frozen)]
    public void EveryServerStateMapsToItsOwnDisplayValue(KodoState state, KodoDisplayState expected)
    {
        Assert.Equal(expected, KodoUnobserved.DisplayOf(state));
    }

    [Fact]
    public void UnobservedIsNeverAServerState()
    {
        // The server enum has no unobserved member, so a server can never report it (section 8.3).
        Assert.DoesNotContain("Unobserved", Enum.GetNames<KodoState>());
    }

    [Fact]
    public void ShorterBudgetsMoveTheEdge()
    {
        KodoUnobservedBudgets tight = KodoUnobservedBudgets.Create(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        Assert.Equal(
            KodoDisplayState.Unobserved,
            KodoUnobserved.Derive(KodoState.Healthy, _now, _now.AddSeconds(-6), _now, tight));
    }

    [Fact]
    public void DeriveRejectsNullBudgets()
    {
        Assert.Throws<ArgumentNullException>(() =>
            KodoUnobserved.Derive(KodoState.Healthy, _now, _now, _now, null!));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(30, 0)]
    [InlineData(-1, 60)]
    [InlineData(30, -1)]
    public void BudgetsMustBePositive(int heartbeatSeconds, int freshnessSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoUnobservedBudgets.Create(TimeSpan.FromSeconds(heartbeatSeconds), TimeSpan.FromSeconds(freshnessSeconds)));
    }
}
