using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Tests.Security.Audit.Checkpoints;

public class AuditCheckpointPolicyTests
{
    private static readonly DateTimeOffset _last = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan _hour = TimeSpan.FromHours(1);

    private static AuditCheckpointPolicy Policy(int rows = 100, TimeSpan? interval = null) =>
        new(rows, interval ?? _hour);

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(AuditCheckpointPolicy.MaxEveryRows)]
    public void Valid_row_counts_pass(int rows)
    {
        Policy(rows).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(AuditCheckpointPolicy.MaxEveryRows + 1)]
    public void Row_count_outside_floors_is_rejected(int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(rows).Validate());
    }

    [Fact]
    public void Interval_at_the_floor_and_ceiling_passes()
    {
        Policy(interval: AuditCheckpointPolicy.MinEveryInterval).Validate();
        Policy(interval: AuditCheckpointPolicy.MaxEveryInterval).Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(59)]
    public void Interval_under_one_minute_is_rejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(interval: TimeSpan.FromSeconds(seconds)).Validate());
    }

    [Fact]
    public void Interval_over_a_day_is_rejected()
    {
        // Section 5.2 requires a signed checkpoint at least daily.
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(interval: TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1)).Validate());
    }

    [Fact]
    public void Row_trigger_fires_exactly_at_every_rows()
    {
        var policy = Policy(rows: 100);

        Assert.False(policy.ShouldCheckpoint(99, _last, _last.AddMinutes(1)));
        Assert.True(policy.ShouldCheckpoint(100, _last, _last.AddMinutes(1)));
        Assert.True(policy.ShouldCheckpoint(101, _last, _last.AddMinutes(1)));
    }

    [Fact]
    public void Time_trigger_fires_exactly_at_every_interval()
    {
        var policy = Policy(rows: 100, interval: _hour);

        Assert.False(policy.ShouldCheckpoint(1, _last, _last + _hour - TimeSpan.FromTicks(1)));
        Assert.True(policy.ShouldCheckpoint(1, _last, _last + _hour));
    }

    [Fact]
    public void Zero_rows_never_triggers_even_when_time_has_passed()
    {
        var policy = Policy(rows: 1, interval: TimeSpan.FromMinutes(1));

        Assert.False(policy.ShouldCheckpoint(0, _last, _last.AddDays(1)));
    }

    [Fact]
    public void Clock_going_backwards_does_not_trigger_the_time_rule()
    {
        var policy = Policy(rows: 100, interval: _hour);

        Assert.False(policy.ShouldCheckpoint(1, _last, _last.AddMinutes(-5)));
    }
}
