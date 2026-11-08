using Bazaar.Server.Kanban.Ryusei;

namespace Bazaar.Server.Tests.Kanban.Ryusei;

/// <summary>Each RyuseiFuda guard at its boundary (doc 38, Loop and runaway safeguards). Pure arithmetic.</summary>
public sealed class RyuseiGuardTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, RyuseiRefusal.None)]
    [InlineData(3, RyuseiRefusal.None)]
    [InlineData(4, RyuseiRefusal.ChainDepthExceeded)]
    public void Chain_depth_3_is_allowed_and_4_is_refused(int depth, RyuseiRefusal expected)
    {
        Assert.Equal(expected, RyuseiGuards.CheckChainDepth(depth, RyuseiDefaults.MaxChainDepth).Refusal);
    }

    [Fact]
    public void Chain_depth_setting_is_clamped_to_one_to_three()
    {
        // A setting of 5 cannot loosen the default of 3, and a setting of 0 cannot go below 1.
        Assert.False(RyuseiGuards.CheckChainDepth(4, 5).IsAllowed);
        Assert.True(RyuseiGuards.CheckChainDepth(1, 0).IsAllowed);
        Assert.False(RyuseiGuards.CheckChainDepth(2, 0).IsAllowed);
    }

    [Fact]
    public void Cooldown_59_seconds_is_refused()
    {
        var decision = RyuseiGuards.CheckCooldown(_start.AddSeconds(59), _start, RyuseiDefaults.CooldownSeconds);

        Assert.Equal(RyuseiRefusal.CooldownActive, decision.Refusal);
    }

    [Fact]
    public void Cooldown_60_seconds_is_allowed()
    {
        Assert.True(RyuseiGuards.CheckCooldown(_start.AddSeconds(60), _start, RyuseiDefaults.CooldownSeconds).IsAllowed);
    }

    [Fact]
    public void A_trigger_that_never_fired_has_no_cooldown()
    {
        Assert.True(RyuseiGuards.CheckCooldown(_start, null, RyuseiDefaults.CooldownSeconds).IsAllowed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void Cooldown_setting_below_the_floor_is_raised_to_ten_seconds(int configured)
    {
        // 9 s after the last fire is refused under any setting, because the floor is 10 s.
        Assert.False(RyuseiGuards.CheckCooldown(_start.AddSeconds(9), _start, configured).IsAllowed);
        Assert.True(RyuseiGuards.CheckCooldown(_start.AddSeconds(10), _start, configured).IsAllowed);
    }

    [Fact]
    public void The_96th_fire_in_a_day_is_allowed()
    {
        // 95 fires already made in the last 24 hours, so this is the 96th.
        Assert.True(RyuseiGuards.CheckDailyFireCap(95, RyuseiDefaults.MaxFiresPerDay).IsAllowed);
    }

    [Fact]
    public void The_97th_fire_in_a_day_is_refused()
    {
        // 96 fires already made, so this is the 97th.
        var decision = RyuseiGuards.CheckDailyFireCap(96, RyuseiDefaults.MaxFiresPerDay);

        Assert.Equal(RyuseiRefusal.DailyFireCapReached, decision.Refusal);
    }

    [Fact]
    public void Second_consecutive_refusal_does_not_auto_pause()
    {
        Assert.True(RyuseiGuards.CheckRefusalStreak(2, RyuseiDefaults.AutoPauseAfter).IsAllowed);
    }

    [Fact]
    public void Third_consecutive_refusal_trips_auto_pause()
    {
        var decision = RyuseiGuards.CheckRefusalStreak(3, RyuseiDefaults.AutoPauseAfter);

        Assert.Equal(RyuseiRefusal.AutoPauseTripped, decision.Refusal);
    }

    [Fact]
    public void Auto_pause_setting_is_clamped_to_one_to_twenty()
    {
        Assert.False(RyuseiGuards.CheckRefusalStreak(1, 0).IsAllowed);
        Assert.False(RyuseiGuards.CheckRefusalStreak(20, 50).IsAllowed);
        Assert.True(RyuseiGuards.CheckRefusalStreak(19, 50).IsAllowed);
    }

    [Fact]
    public void The_50th_active_trigger_is_allowed()
    {
        // 49 triggers already active, so creating this one makes 50.
        Assert.True(RyuseiGuards.CheckActiveTriggerCap(49, RyuseiDefaults.MaxTriggersPerAgent).IsAllowed);
    }

    [Fact]
    public void The_51st_active_trigger_is_refused()
    {
        // 50 triggers already active, so creating this one would make 51.
        var decision = RyuseiGuards.CheckActiveTriggerCap(50, RyuseiDefaults.MaxTriggersPerAgent);

        Assert.Equal(RyuseiRefusal.ActiveTriggerCapReached, decision.Refusal);
    }

    [Fact]
    public void Fan_out_of_8_targets_is_allowed()
    {
        Assert.True(RyuseiGuards.CheckPingFanout(8, RyuseiDefaults.MaxPingFanout).IsAllowed);
    }

    [Fact]
    public void Fan_out_of_9_targets_is_refused()
    {
        var decision = RyuseiGuards.CheckPingFanout(9, RyuseiDefaults.MaxPingFanout);

        Assert.Equal(RyuseiRefusal.PingFanoutExceeded, decision.Refusal);
    }

    [Fact]
    public void Fan_out_with_no_targets_is_refused()
    {
        Assert.Equal(RyuseiRefusal.NoTargets, RyuseiGuards.CheckPingFanout(0, RyuseiDefaults.MaxPingFanout).Refusal);
    }

    [Fact]
    public void Fan_out_setting_is_clamped_to_one_to_eight()
    {
        Assert.False(RyuseiGuards.CheckPingFanout(9, 20).IsAllowed);
        Assert.False(RyuseiGuards.CheckPingFanout(2, 1).IsAllowed);
    }

    [Fact]
    public void Decision_refuse_rejects_none_as_a_reason()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RyuseiDecision.Refuse(RyuseiRefusal.None));
    }
}
