using System.Globalization;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Tests.WorkLife;

public class AvailabilityMachineTests
{
    private const string _agent = "agent-a";

    private static DateTimeOffset At(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static readonly DateTimeOffset _t0 = At("2026-06-15T09:00:00Z");
    private static readonly DateTimeOffset _t1 = At("2026-06-15T10:00:00Z");
    private static readonly DateTimeOffset _t2 = At("2026-06-15T11:00:00Z");

    private static AvailabilityMachine Machine(AvailabilityMachineState state = AvailabilityMachineState.OnShift) =>
        new(_agent, state);

    public static TheoryData<AvailabilityEvent> DrainTriggers => new()
    {
        new AvailabilityEvent.ShiftEnd(_t0),
        new AvailabilityEvent.BreakDue(_t0),
        new AvailabilityEvent.VacationStarts(_t0),
        new AvailabilityEvent.TokenBreak(_t0),
    };

    public static TheoryData<AvailabilityMachineState, AvailabilityEvent> ReturnToShiftRows => new()
    {
        { AvailabilityMachineState.Break, new AvailabilityEvent.ScheduleStart(_t0) },
        { AvailabilityMachineState.Break, new AvailabilityEvent.BreakEnd(_t0) },
        { AvailabilityMachineState.Off, new AvailabilityEvent.ScheduleStart(_t0) },
        { AvailabilityMachineState.Off, new AvailabilityEvent.BreakEnd(_t0) },
    };

    public static TheoryData<AvailabilityMachineState> AllStates => new()
    {
        AvailabilityMachineState.OnShift,
        AvailabilityMachineState.Draining,
        AvailabilityMachineState.Break,
        AvailabilityMachineState.Off,
        AvailabilityMachineState.Vacation,
    };

    [Theory]
    [MemberData(nameof(DrainTriggers))]
    public void On_shift_moves_to_draining_on_drain_triggers(AvailabilityEvent evt)
    {
        var machine = Machine();

        var transition = machine.Submit(evt);

        Assert.True(transition.Applied);
        Assert.Equal(AvailabilityMachineState.Draining, machine.Snapshot.State);
        Assert.Equal(1, machine.Snapshot.Version);
    }

    [Theory]
    [InlineData(AvailabilityMachineState.Break)]
    [InlineData(AvailabilityMachineState.Off)]
    [InlineData(AvailabilityMachineState.Vacation)]
    public void Drain_complete_moves_to_its_target(AvailabilityMachineState target)
    {
        var machine = Machine(AvailabilityMachineState.Draining);

        var transition = machine.Submit(new AvailabilityEvent.DrainComplete(_t1, target));

        Assert.True(transition.Applied);
        Assert.Equal(target, machine.Snapshot.State);
    }

    [Theory]
    [InlineData(AvailabilityMachineState.OnShift)]
    [InlineData(AvailabilityMachineState.Draining)]
    public void Drain_complete_rejects_targets_other_than_break_off_or_vacation(AvailabilityMachineState target)
    {
        var machine = Machine(AvailabilityMachineState.Draining);

        var transition = machine.Submit(new AvailabilityEvent.DrainComplete(_t1, target));

        Assert.False(transition.Applied);
        Assert.Contains("cannot target", transition.Reason);
        Assert.Equal(AvailabilityMachineState.Draining, machine.Snapshot.State);
        Assert.Equal(0, machine.Snapshot.Version);
    }

    [Theory]
    [MemberData(nameof(ReturnToShiftRows))]
    public void Break_or_off_returns_to_shift_on_schedule_start_or_break_end(
        AvailabilityMachineState from,
        AvailabilityEvent evt)
    {
        var machine = Machine(from);

        var transition = machine.Submit(evt);

        Assert.True(transition.Applied);
        Assert.Equal(AvailabilityMachineState.OnShift, machine.Snapshot.State);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Rate_limit_sets_throttle_overlay_and_keeps_state_in_any_state(AvailabilityMachineState state)
    {
        var machine = Machine(state);
        var until = _t2;

        var transition = machine.Submit(new AvailabilityEvent.RateLimited(_t1, until));

        Assert.True(transition.Applied);
        Assert.Equal(state, machine.Snapshot.State);
        Assert.Equal([new AvailabilityOverlay.Throttled(until)], machine.Snapshot.Overlays);
    }

    [Fact]
    public void Later_rate_limit_replaces_the_throttle_overlay()
    {
        var machine = Machine();
        machine.Submit(new AvailabilityEvent.RateLimited(_t0, _t1));

        machine.Submit(new AvailabilityEvent.RateLimited(_t1, _t2));

        Assert.Equal([new AvailabilityOverlay.Throttled(_t2)], machine.Snapshot.Overlays);
    }

    [Fact]
    public void Spend_cap_with_no_allowed_source_goes_to_vacation_and_keeps_reset_time()
    {
        var machine = Machine();

        var transition = machine.Submit(new AvailabilityEvent.SpendCapped(_t1, true, _t2));

        Assert.True(transition.Applied);
        Assert.Equal(AvailabilityMachineState.Vacation, machine.Snapshot.State);
        Assert.Equal("provider cap", machine.Snapshot.Reason);
        Assert.Equal(_t2, machine.Snapshot.ProviderResetAt);
    }

    [Fact]
    public void Spend_cap_with_an_allowed_source_left_does_not_change_state()
    {
        var machine = Machine();

        var transition = machine.Submit(new AvailabilityEvent.SpendCapped(_t1, false, _t2));

        Assert.False(transition.Applied);
        Assert.Equal(AvailabilityMachineState.OnShift, machine.Snapshot.State);
        Assert.Equal(0, machine.Snapshot.Version);
        Assert.Null(machine.Snapshot.ProviderResetAt);
    }

    [Fact]
    public void Owner_emergency_bypasses_vacation_and_keeps_the_throttle()
    {
        var machine = Machine();
        machine.Submit(new AvailabilityEvent.RateLimited(_t0, _t2));
        machine.Submit(new AvailabilityEvent.SpendCapped(_t0, true, _t2));
        Assert.Equal(AvailabilityMachineState.Vacation, machine.Snapshot.State);

        var transition = machine.Submit(new AvailabilityEvent.OwnerEmergency(_t1));

        Assert.True(transition.Applied);
        Assert.Equal(AvailabilityMachineState.OnShift, machine.Snapshot.State);
        Assert.Equal([new AvailabilityOverlay.Throttled(_t2)], machine.Snapshot.Overlays);
        Assert.Equal(_t2, machine.Snapshot.ProviderResetAt);
    }

    [Fact]
    public void Duplicate_key_is_a_no_op_that_returns_the_current_snapshot()
    {
        var machine = Machine();
        machine.Submit(new AvailabilityEvent.ShiftEnd(_t1));
        var before = machine.Snapshot;

        var transition = machine.Submit(new AvailabilityEvent.ShiftEnd(_t1));

        Assert.False(transition.Applied);
        Assert.Equal(Describe(before), Describe(transition.Snapshot));
        Assert.Equal(Describe(before), Describe(machine.Snapshot));
    }

    [Fact]
    public void Same_instant_at_a_different_offset_is_the_same_key()
    {
        var machine = Machine();
        machine.Submit(new AvailabilityEvent.ShiftEnd(At("2026-06-15T10:00:00Z")));

        var transition = machine.Submit(new AvailabilityEvent.ShiftEnd(At("2026-06-15T11:00:00+01:00")));

        Assert.False(transition.Applied);
        Assert.Equal(1, machine.Snapshot.Version);
    }

    [Fact]
    public void Same_event_at_a_different_instant_is_applied_again_when_valid()
    {
        var machine = Machine();
        machine.Submit(new AvailabilityEvent.ShiftEnd(_t0));
        machine.Submit(new AvailabilityEvent.DrainComplete(_t0, AvailabilityMachineState.Off));
        machine.Submit(new AvailabilityEvent.ScheduleStart(_t0));

        var transition = machine.Submit(new AvailabilityEvent.ShiftEnd(_t1));

        Assert.True(transition.Applied);
        Assert.Equal(AvailabilityMachineState.Draining, machine.Snapshot.State);
    }

    [Theory]
    [InlineData(AvailabilityMachineState.Off, "ShiftEnd")]
    [InlineData(AvailabilityMachineState.Break, "ShiftEnd")]
    [InlineData(AvailabilityMachineState.Vacation, "ShiftEnd")]
    [InlineData(AvailabilityMachineState.OnShift, "ScheduleStart")]
    [InlineData(AvailabilityMachineState.Draining, "ScheduleStart")]
    [InlineData(AvailabilityMachineState.Vacation, "ScheduleStart")]
    [InlineData(AvailabilityMachineState.OnShift, "BreakEnd")]
    [InlineData(AvailabilityMachineState.Break, "DrainComplete")]
    [InlineData(AvailabilityMachineState.OnShift, "DrainComplete")]
    public void Invalid_transitions_are_rejected_with_a_reason(AvailabilityMachineState state, string eventName)
    {
        var machine = Machine(state);
        AvailabilityEvent evt = eventName switch
        {
            "ShiftEnd" => new AvailabilityEvent.ShiftEnd(_t1),
            "ScheduleStart" => new AvailabilityEvent.ScheduleStart(_t1),
            "BreakEnd" => new AvailabilityEvent.BreakEnd(_t1),
            _ => new AvailabilityEvent.DrainComplete(_t1, AvailabilityMachineState.Break),
        };

        var transition = machine.Submit(evt);

        Assert.False(transition.Applied);
        Assert.False(string.IsNullOrWhiteSpace(transition.Reason));
        Assert.Equal(state, machine.Snapshot.State);
        Assert.Equal(0, machine.Snapshot.Version);
    }

    [Fact]
    public void Version_increases_only_on_accepted_transitions()
    {
        var machine = Machine();

        machine.Submit(new AvailabilityEvent.ScheduleStart(_t0));
        Assert.Equal(0, machine.Snapshot.Version);

        machine.Submit(new AvailabilityEvent.ShiftEnd(_t1));
        Assert.Equal(1, machine.Snapshot.Version);

        machine.Submit(new AvailabilityEvent.ShiftEnd(_t1));
        Assert.Equal(1, machine.Snapshot.Version);

        machine.Submit(new AvailabilityEvent.RateLimited(_t1, _t2));
        Assert.Equal(2, machine.Snapshot.Version);

        machine.Submit(new AvailabilityEvent.SpendCapped(_t1, false, null));
        Assert.Equal(2, machine.Snapshot.Version);
    }

    [Fact]
    public void Cover_overlay_is_set_and_cleared_without_changing_state()
    {
        var machine = Machine(AvailabilityMachineState.Break);

        machine.Submit(new AvailabilityEvent.CoverAssigned(_t0, "agent-cover"));
        Assert.Equal(AvailabilityMachineState.Break, machine.Snapshot.State);
        Assert.Equal([new AvailabilityOverlay.Covered("agent-cover")], machine.Snapshot.Overlays);

        machine.Submit(new AvailabilityEvent.CoverReleased(_t1));
        Assert.Equal(AvailabilityMachineState.Break, machine.Snapshot.State);
        Assert.Empty(machine.Snapshot.Overlays);
    }

    [Fact]
    public void Same_inputs_give_the_same_result_on_replay()
    {
        AvailabilityEvent[] script =
        [
            new AvailabilityEvent.ShiftEnd(_t0),
            new AvailabilityEvent.RateLimited(_t0, _t2),
            new AvailabilityEvent.DrainComplete(_t1, AvailabilityMachineState.Vacation),
            new AvailabilityEvent.OwnerEmergency(_t2),
        ];

        var first = Machine();
        var second = Machine();
        var firstResults = script.Select(first.Submit).Select(Describe).ToList();
        var secondResults = script.Select(second.Submit).Select(Describe).ToList();

        Assert.Equal(firstResults, secondResults);
        Assert.Equal(Describe(first.Snapshot), Describe(second.Snapshot));
    }

    /// <summary>Snapshot fields as values. Record equality compares overlay collections by reference, so tests compare these instead.</summary>
    private static string Describe(AvailabilitySnapshot snapshot) =>
        $"{snapshot.State}|{snapshot.Version}|{snapshot.Reason}|{snapshot.ProviderResetAt}|{string.Join(",", snapshot.Overlays)}";

    private static string Describe(AvailabilityTransition transition) =>
        $"{transition.Applied}|{transition.Reason}|{Describe(transition.Snapshot)}";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_agent_id_throws(string? agentId)
    {
        Assert.Throws<ArgumentException>(() => new AvailabilityMachine(agentId!, AvailabilityMachineState.OnShift));
        Assert.Throws<ArgumentException>(() =>
            AvailabilityRules.Apply(
                new AvailabilitySnapshot(AvailabilityMachineState.OnShift, [], 0, "initial"),
                new AvailabilityEvent.ShiftEnd(_t0),
                agentId!,
                new HashSet<AvailabilityIdempotencyKey>()));
    }
}
