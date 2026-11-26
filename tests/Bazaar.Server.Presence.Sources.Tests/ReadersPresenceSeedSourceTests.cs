using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Presence.Sources.Tests;

/// <summary>The restart seed reads work-life state, schedules and open turns, never the presence table (docs/design/23, "Restart").</summary>
public class ReadersPresenceSeedSourceTests
{
    private static readonly Guid _a = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly Guid _b = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a2");
    private static readonly Guid _c = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a3");
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private sealed class States(params AgentAvailabilityState[] items) : IAvailabilityStateReader
    {
        public ValueTask<IReadOnlyList<AgentAvailabilityState>> ListAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<AgentAvailabilityState>>(items);
    }

    private sealed class Schedules(params AgentScheduleInputs[] items) : IAgentScheduleReader
    {
        public ValueTask<IReadOnlyList<AgentScheduleInputs>> ListAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<AgentScheduleInputs>>(items);
    }

    private sealed class Turns(params OpenTurn[] items) : IOpenTurnReader
    {
        public ValueTask<IReadOnlyList<OpenTurn>> ListOpenAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<OpenTurn>>(items);
    }

    private sealed class Fixed : IPresenceSeedSource
    {
        public ValueTask<IReadOnlyList<PresenceSeed>> LoadAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PresenceSeed>>([]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ReadersPresenceSeedSource Source(IAvailabilityStateReader? s = null, IAgentScheduleReader? sc = null, IOpenTurnReader? t = null) =>
        new(new FakeTimeProvider(_now), s, sc, t);

    private static AgentAvailabilityState Saved(Guid id, AvailabilityMachineState state, params AvailabilityOverlay[] overlays) =>
        new(id, new AvailabilitySnapshot(state, overlays, 3, "test"));

    private static AgentScheduleInputs Shift(Guid id, int fromHour, int toHour) =>
        new(id, new AgentSchedule("UTC", [new ShiftWindow(new HashSet<DayOfWeek>(Enum.GetValues<DayOfWeek>()), new TimeOnly(fromHour, 0), new TimeOnly(toHour, 0))]), []);

    [Fact]
    public async Task Saved_availability_alone_seeds_the_state_and_overlays()
    {
        var seeds = await Source(new States(Saved(_a, AvailabilityMachineState.Draining, new AvailabilityOverlay.Throttled(_now.AddMinutes(5)), new AvailabilityOverlay.Covered("x")))).LoadAsync(Ct);

        var seed = Assert.Single(seeds);
        Assert.Equal(PresenceAvailability.Draining, seed.Availability);
        Assert.True(seed.Throttled);
        Assert.True(seed.Covered);
        Assert.Null(seed.OpenTurn);
    }

    [Fact]
    public async Task An_expired_throttle_does_not_seed_throttled()
    {
        var seeds = await Source(new States(Saved(_a, AvailabilityMachineState.OnShift, new AvailabilityOverlay.Throttled(_now.AddMinutes(-1))))).LoadAsync(Ct);

        Assert.False(Assert.Single(seeds).Throttled);
    }

    [Fact]
    public async Task Without_saved_state_availability_comes_from_the_schedule_at_now()
    {
        var seeds = await Source(sc: new Schedules(Shift(_a, 8, 17), Shift(_b, 13, 17))).LoadAsync(Ct);

        Assert.Equal([PresenceAvailability.OnShift, PresenceAvailability.Off], seeds.Select(s => s.Availability));
    }

    [Fact]
    public async Task Saved_state_wins_over_the_schedule()
    {
        var seeds = await Source(new States(Saved(_a, AvailabilityMachineState.Break)), new Schedules(Shift(_a, 8, 17))).LoadAsync(Ct);

        Assert.Equal(PresenceAvailability.Break, Assert.Single(seeds).Availability);
    }

    [Fact]
    public async Task An_open_turn_is_seeded_as_a_model_call_with_its_ids()
    {
        var turn = Guid.NewGuid();
        var gc = Guid.NewGuid();
        var started = _now.AddMinutes(-2);
        var seeds = await Source(new States(Saved(_a, AvailabilityMachineState.OnShift)), t: new Turns(new OpenTurn(_a, turn, started, gc, TurnGcKind.Council))).LoadAsync(Ct);

        var open = Assert.Single(seeds).OpenTurn;
        Assert.NotNull(open);
        Assert.Equal(PresenceActivityKind.ModelCall, open.Activity);
        Assert.Equal(PresenceGcContext.CouncilOrVote, open.GcContext);
        Assert.Equal(turn, open.TurnId);
        Assert.Equal(gc, open.GcId);
        Assert.Equal(started, open.SourceTime);
    }

    [Fact]
    public async Task An_open_turn_with_no_availability_data_implies_on_shift()
    {
        var seeds = await Source(t: new Turns(new OpenTurn(_a, Guid.NewGuid(), _now))).LoadAsync(Ct);

        var seed = Assert.Single(seeds);
        Assert.Equal(PresenceAvailability.OnShift, seed.Availability);
        Assert.NotNull(seed.OpenTurn);
    }

    [Fact]
    public async Task The_latest_of_several_open_turns_wins()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var seeds = await Source(t: new Turns(new OpenTurn(_a, newer, _now), new OpenTurn(_a, older, _now.AddMinutes(-9)))).LoadAsync(Ct);

        Assert.Equal(newer, Assert.Single(seeds).OpenTurn!.TurnId);
    }

    [Fact]
    public async Task No_data_and_no_readers_give_no_seeds()
    {
        Assert.Empty(await Source().LoadAsync(Ct));
        Assert.Empty(await Source(new States(), new Schedules(), new Turns()).LoadAsync(Ct));
    }

    [Fact]
    public async Task Seeds_are_ordered_by_agent_id_across_all_three_readers()
    {
        var seeds = await Source(new States(Saved(_c, AvailabilityMachineState.Off)), new Schedules(Shift(_b, 8, 17)), new Turns(new OpenTurn(_a, Guid.NewGuid(), _now))).LoadAsync(Ct);

        Assert.Equal([_a, _b, _c], seeds.Select(s => s.AgentId));
    }

    [Fact]
    public async Task The_presence_sources_registration_provides_the_seed_and_a_host_seed_wins()
    {
        var services = new ServiceCollection().AddBazaarPresenceSources();
        services.AddSingleton<IOpenTurnReader>(new Turns(new OpenTurn(_a, Guid.NewGuid(), _now)));
        await using var provider = services.BuildServiceProvider();

        var seeds = await provider.GetRequiredService<IPresenceSeedSource>().LoadAsync(Ct);
        Assert.Equal(_a, Assert.Single(seeds).AgentId);

        var custom = new ServiceCollection();
        custom.AddSingleton<IPresenceSeedSource>(new Fixed());
        custom.AddBazaarPresenceSources();
        await using var provider2 = custom.BuildServiceProvider();
        Assert.IsType<Fixed>(provider2.GetRequiredService<IPresenceSeedSource>());
    }
}
