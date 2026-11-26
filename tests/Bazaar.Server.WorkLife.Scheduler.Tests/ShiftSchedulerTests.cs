using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife.Scheduler.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.WorkLife.Scheduler.Tests;

/// <summary>The scheduler walks each agent's availability machine from its schedule (doc 09).</summary>
public class ShiftSchedulerTests
{
    internal static readonly Guid Ada = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly DateTimeOffset _sundayNight = new(2026, 11, 22, 23, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Monday(int hour, int minute = 0) => new(2026, 11, 23, hour, minute, 0, TimeSpan.Zero);

    internal static AgentSchedule WeekdayNine(string tz = "UTC") =>
        new(tz, [new ShiftWindow(new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday }, new TimeOnly(9, 0), new TimeOnly(17, 0))]);

    internal sealed class RecordingSink : IAvailabilityTransitionSink
    {
        public List<(Guid Agent, AvailabilityMachineState State, DateTimeOffset At)> Seen { get; } = [];

        public ValueTask OnTransitionAsync(Guid agentId, AvailabilityTransition transition, DateTimeOffset at, CancellationToken ct)
        {
            Seen.Add((agentId, transition.Snapshot.State, at));
            return ValueTask.CompletedTask;
        }

        public IEnumerable<AvailabilityMachineState> States => Seen.Select(s => s.State);
    }

    private sealed class HoldingDrain : IDrainGate
    {
        public bool Done { get; set; }

        public ValueTask<bool> IsDrainCompleteAsync(Guid agentId, CancellationToken ct) => ValueTask.FromResult(Done);
    }

    private sealed class Rig
    {
        public Rig(DateTimeOffset start, IDrainGate? drain = null, ShiftSchedulerOptions? options = null)
        {
            Time = new FakeTimeProvider(start);
            Scheduler = new ShiftScheduler(Source, Store, drain ?? new ImmediateDrainGate(), options ?? new ShiftSchedulerOptions(), Time, Sink);
        }

        public FakeTimeProvider Time { get; }

        public InMemoryAgentScheduleSource Source { get; } = new();

        public InMemoryAvailabilityStateStore Store { get; } = new();

        public RecordingSink Sink { get; } = new();

        public ShiftScheduler Scheduler { get; }

        public async Task TickAtAsync(DateTimeOffset at)
        {
            Time.SetUtcNow(at);
            await Scheduler.TickAsync(Ct);
        }

        public AvailabilityMachineState State(Guid agent = default) =>
            Scheduler.SnapshotOf(agent == default ? Ada : agent)!.State;
    }

    [Fact]
    public async Task An_agent_crosses_into_shift_and_presence_is_told()
    {
        var rig = new Rig(_sundayNight);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), []));

        await rig.TickAtAsync(Monday(8, 59));
        Assert.Equal(AvailabilityMachineState.Off, rig.State());
        Assert.Empty(rig.Sink.Seen);

        await rig.TickAtAsync(Monday(9));

        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
        var seen = Assert.Single(rig.Sink.Seen);
        Assert.Equal((Ada, AvailabilityMachineState.OnShift), (seen.Agent, seen.State));
        Assert.Equal(AvailabilityMachineState.OnShift, await rig.Store.LoadAsync(Ada, Ct));
    }

    [Fact]
    public async Task Shift_end_drains_then_goes_off_once_the_drain_is_complete()
    {
        var drain = new HoldingDrain();
        var rig = new Rig(_sundayNight, drain);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), []));
        await rig.TickAtAsync(Monday(9));

        await rig.TickAtAsync(Monday(17));
        Assert.Equal(AvailabilityMachineState.Draining, rig.State());

        await rig.TickAtAsync(Monday(17, 1));
        Assert.Equal(AvailabilityMachineState.Draining, rig.State());

        drain.Done = true;
        await rig.TickAtAsync(Monday(17, 2));
        Assert.Equal(AvailabilityMachineState.Off, rig.State());
        Assert.Equal(
            [AvailabilityMachineState.OnShift, AvailabilityMachineState.Draining, AvailabilityMachineState.Off],
            rig.Sink.States);
    }

    [Fact]
    public async Task A_break_drains_lasts_its_length_and_returns_to_shift()
    {
        var rig = new Rig(_sundayNight);
        var breaks = new BreakPolicy(TimeSpan.FromHours(2), TimeSpan.FromMinutes(20));
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), [], breaks));
        await rig.TickAtAsync(Monday(9));

        await rig.TickAtAsync(Monday(10, 59));
        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());

        await rig.TickAtAsync(Monday(11));
        Assert.Equal(AvailabilityMachineState.Break, rig.State());

        await rig.TickAtAsync(Monday(11, 19));
        Assert.Equal(AvailabilityMachineState.Break, rig.State());

        await rig.TickAtAsync(Monday(11, 20));
        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
        Assert.Equal(
            [
                AvailabilityMachineState.OnShift,
                AvailabilityMachineState.Draining,
                AvailabilityMachineState.Break,
                AvailabilityMachineState.OnShift,
            ],
            rig.Sink.States);
    }

    [Fact]
    public async Task A_break_that_would_run_past_the_shift_end_is_not_started()
    {
        var rig = new Rig(_sundayNight);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), [], new BreakPolicy(TimeSpan.FromHours(7), TimeSpan.FromMinutes(30))));
        await rig.TickAtAsync(Monday(9));

        await rig.TickAtAsync(Monday(16, 40));

        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
    }

    [Fact]
    public async Task Vacation_beats_off_and_an_off_agent_goes_straight_to_vacation()
    {
        var rig = new Rig(_sundayNight);
        var vacation = new CalendarEvent(Guid.NewGuid(), "vacation", Monday(0), Monday(0).AddDays(2), affectsAvailability: true);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), [vacation]));

        await rig.TickAtAsync(Monday(9));

        Assert.Equal(AvailabilityMachineState.Vacation, rig.State());
        Assert.Equal([AvailabilityMachineState.Vacation], rig.Sink.States);
    }

    [Fact]
    public async Task Vacation_starting_mid_shift_drains_and_its_end_returns_the_agent_to_shift()
    {
        var rig = new Rig(_sundayNight);
        var vacation = new CalendarEvent(Guid.NewGuid(), "vacation", Monday(12), Monday(14), affectsAvailability: true);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), [vacation]));
        await rig.TickAtAsync(Monday(9));

        await rig.TickAtAsync(Monday(12));
        Assert.Equal(AvailabilityMachineState.Vacation, rig.State());

        await rig.TickAtAsync(Monday(14));
        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
        Assert.Equal(
            [
                AvailabilityMachineState.OnShift,
                AvailabilityMachineState.Draining,
                AvailabilityMachineState.Vacation,
                AvailabilityMachineState.Off,
                AvailabilityMachineState.OnShift,
            ],
            rig.Sink.States);
    }

    [Fact]
    public async Task A_calendar_event_that_does_not_affect_availability_changes_nothing()
    {
        var rig = new Rig(_sundayNight);
        var meeting = new CalendarEvent(Guid.NewGuid(), "meeting", Monday(9), Monday(10), affectsAvailability: false);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), [meeting]));

        await rig.TickAtAsync(Monday(9, 30));

        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
    }

    [Theory]
    [InlineData("America/New_York", 2026, 10, 26, 13, 0)] // Monday, EDT (UTC-4): 09:00 local is 13:00Z.
    [InlineData("America/New_York", 2026, 11, 2, 14, 0)] // Monday after fall back, EST (UTC-5): 09:00 local is 14:00Z.
    [InlineData("Asia/Tokyo", 2026, 11, 23, 0, 0)] // JST (UTC+9): 09:00 local is 00:00Z.
    public async Task Shifts_follow_the_agents_zone_across_dst(string zone, int y, int mo, int d, int h, int mi)
    {
        var start = new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero);
        var rig = new Rig(start.AddHours(-3));
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(zone), []));

        await rig.TickAtAsync(start.AddMinutes(-1));
        Assert.Equal(AvailabilityMachineState.Off, rig.State());

        await rig.TickAtAsync(start);
        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
    }

    [Fact]
    public async Task A_restart_resumes_from_the_stored_state_without_telling_presence_again()
    {
        var rig = new Rig(_sundayNight);
        await rig.Store.SaveAsync(Ada, AvailabilityMachineState.OnShift, Ct);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), []));

        await rig.TickAtAsync(Monday(10));

        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
        Assert.Empty(rig.Sink.Seen);
    }

    [Fact]
    public async Task A_restart_during_a_break_with_no_end_time_ends_the_break()
    {
        var rig = new Rig(_sundayNight);
        await rig.Store.SaveAsync(Ada, AvailabilityMachineState.Break, Ct);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), []));

        await rig.TickAtAsync(Monday(10));

        Assert.Equal(AvailabilityMachineState.OnShift, rig.State());
    }

    [Fact]
    public async Task A_failing_agent_does_not_stop_the_others_and_a_failing_sink_does_not_fail_the_tick()
    {
        var rig = new Rig(_sundayNight);
        var bob = Guid.Parse("00000000-0000-7000-8000-00000000000b");
        var failing = new FailingStore(Ada);
        var scheduler = new ShiftScheduler(rig.Source, failing, new ImmediateDrainGate(), new ShiftSchedulerOptions(), rig.Time, new ThrowingSink());
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), []));
        rig.Source.Set(new AgentShiftEntry(bob, WeekdayNine(), []));
        rig.Time.SetUtcNow(Monday(9));

        await scheduler.TickAsync(Ct);

        Assert.Equal(AvailabilityMachineState.OnShift, scheduler.SnapshotOf(bob)!.State);
    }

    [Fact]
    public async Task An_agent_removed_from_the_source_is_forgotten()
    {
        var rig = new Rig(_sundayNight);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), []));
        await rig.TickAtAsync(Monday(9));

        rig.Source.Remove(Ada);
        await rig.TickAtAsync(Monday(10));

        Assert.Null(rig.Scheduler.SnapshotOf(Ada));
    }

    [Fact]
    public async Task Scheduler_creates_no_throttled_or_covered_overlay()
    {
        var rig = new Rig(_sundayNight);
        rig.Source.Set(new AgentShiftEntry(Ada, WeekdayNine(), [], new BreakPolicy(TimeSpan.FromHours(1), TimeSpan.FromMinutes(10))));
        await rig.TickAtAsync(Monday(9));
        await rig.TickAtAsync(Monday(10));
        await rig.TickAtAsync(Monday(17));

        Assert.Empty(rig.Scheduler.SnapshotOf(Ada)!.Overlays);
    }

    private sealed class ThrowingSink : IAvailabilityTransitionSink
    {
        public ValueTask OnTransitionAsync(Guid agentId, AvailabilityTransition transition, DateTimeOffset at, CancellationToken ct) =>
            throw new InvalidOperationException("presence down");
    }

    /// <summary>A store that throws for one agent, so that agent's tick fails after its transitions.</summary>
    private sealed class FailingStore(Guid failFor) : IAvailabilityStateStore
    {
        public ValueTask<AvailabilityMachineState?> LoadAsync(Guid agentId, CancellationToken ct) =>
            agentId == failFor ? throw new InvalidOperationException("store down") : ValueTask.FromResult<AvailabilityMachineState?>(null);

        public ValueTask SaveAsync(Guid agentId, AvailabilityMachineState state, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
