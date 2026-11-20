using Bazaar.Contracts.Kodo;
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
    public void DefaultIsTheThirtySecondHeartbeat()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), KodoUnobservedBudgets.Defaults.Heartbeat);
    }

    [Fact]
    public void FreshStreamShowsTheLastKnownStateNotUnobserved()
    {
        Assert.Equal(KodoDisplayState.Degraded, KodoUnobserved.Derive(KodoDisplayState.Degraded, _now, _now, _budgets));
    }

    [Fact]
    public void SilenceExactlyAtTheBudgetIsStillObserved()
    {
        Assert.Equal(
            KodoDisplayState.Healthy,
            KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, _now.AddSeconds(-30), _budgets));
    }

    [Fact]
    public void SilenceOneTickPastTheBudgetIsUnobserved()
    {
        Assert.Equal(
            KodoDisplayState.Unobserved,
            KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, _now.AddSeconds(-30).AddTicks(-1), _budgets));
    }

    [Fact]
    public void ContinuousHeartbeatsKeepAnOldSnapshotObserved()
    {
        // The snapshot arrived at connect, ten minutes ago. Heartbeats every 10 seconds since then each refresh freshness,
        // so a healthy connected client never goes unobserved just because the snapshot is old (section 8.3).
        DateTimeOffset snapshotAt = _now.AddMinutes(-10);
        DateTimeOffset lastEventAt = snapshotAt;
        for (DateTimeOffset at = snapshotAt.AddSeconds(10); at <= _now; at = at.AddSeconds(10))
        {
            Assert.Equal(KodoDisplayState.Healthy, KodoUnobserved.Derive(KodoDisplayState.Healthy, at, lastEventAt, _budgets));
            lastEventAt = at;
        }

        Assert.Equal(KodoDisplayState.Healthy, KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, lastEventAt, _budgets));
    }

    [Fact]
    public void SilenceBeyondTheWindowAfterTheLastHeartbeatBecomesUnobserved()
    {
        DateTimeOffset lastHeartbeat = _now.AddMinutes(-1);

        Assert.Equal(
            KodoDisplayState.Unobserved,
            KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, lastHeartbeat, _budgets));
        Assert.Equal(
            KodoDisplayState.Healthy,
            KodoUnobserved.Derive(KodoDisplayState.Healthy, lastHeartbeat.AddSeconds(30), lastHeartbeat, _budgets));
    }

    [Fact]
    public void NoEventEverReceivedIsUnobserved()
    {
        Assert.Equal(KodoDisplayState.Unobserved, KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, null, _budgets));
    }

    [Fact]
    public void NoStateEverReceivedIsUnobserved()
    {
        Assert.Equal(KodoDisplayState.Unobserved, KodoUnobserved.Derive(null, _now, _now, _budgets));
    }

    [Fact]
    public void ReceiveTimeInTheFutureCountsAsAgeZero()
    {
        Assert.Equal(
            KodoDisplayState.Healthy,
            KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, _now.AddSeconds(5), _budgets));
    }

    [Fact]
    public void FrozenStaysFrozenWhileObserved()
    {
        Assert.Equal(KodoDisplayState.Frozen, KodoUnobserved.Derive(KodoDisplayState.Frozen, _now, _now, _budgets));
    }

    [Fact]
    public void UnobservedIsNotAKnownServerState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            KodoUnobserved.Derive(KodoDisplayState.Unobserved, _now, _now, _budgets));
    }

    [Theory]
    [InlineData(KodoState.Healthy, KodoDisplayState.Healthy)]
    [InlineData(KodoState.Degraded, KodoDisplayState.Degraded)]
    [InlineData(KodoState.Failing, KodoDisplayState.Failing)]
    [InlineData(KodoState.Recovering, KodoDisplayState.Recovering)]
    [InlineData(KodoState.Frozen, KodoDisplayState.Frozen)]
    public void EveryServerStateMapsToItsOwnDisplayValueByEnumAndByWireString(KodoState state, KodoDisplayState expected)
    {
        Assert.Equal(expected, KodoWireNames.DisplayOf(state));
        Assert.True(KodoUnobserved.TryDisplayOf(KodoWireNames.State(state), out KodoDisplayState parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("unobserved")]
    [InlineData("Healthy")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownWireStatesDoNotParse(string? wire)
    {
        Assert.False(KodoUnobserved.TryDisplayOf(wire, out _));
    }

    [Fact]
    public void UnobservedIsNeverAServerState()
    {
        Assert.DoesNotContain("Unobserved", Enum.GetNames<KodoState>());
    }

    [Fact]
    public void ShorterBudgetMovesTheEdge()
    {
        KodoUnobservedBudgets tight = KodoUnobservedBudgets.Create(TimeSpan.FromSeconds(5));

        Assert.Equal(
            KodoDisplayState.Unobserved,
            KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, _now.AddSeconds(-6), tight));
    }

    [Fact]
    public void DeriveRejectsNullBudgets()
    {
        Assert.Throws<ArgumentNullException>(() => KodoUnobserved.Derive(KodoDisplayState.Healthy, _now, _now, null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BudgetMustBePositive(int heartbeatSeconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KodoUnobservedBudgets.Create(TimeSpan.FromSeconds(heartbeatSeconds)));
    }
}
