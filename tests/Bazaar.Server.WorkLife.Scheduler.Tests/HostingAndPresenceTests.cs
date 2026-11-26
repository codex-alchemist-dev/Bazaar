using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.WorkLife.Scheduler.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.WorkLife.Scheduler.Tests;

/// <summary>The hosted loop, option parsing, registration and the end-to-end path into the real presence projector.</summary>
public class HostingAndPresenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _sundayNight = new(2026, 11, 22, 23, 0, 0, TimeSpan.Zero);

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private sealed class NullPublisher : IPresencePublisher
    {
        public ValueTask PublishDeltaAsync(PresenceRow row, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<PresenceRow>> GetSnapshotAsync(CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<PresenceRow>>([]);
    }

    [Fact]
    public async Task Shift_start_reaches_the_real_presence_projector()
    {
        var time = new FakeTimeProvider(_sundayNight);
        var store = new Bazaar.Server.Presence.InMemoryPresenceStore();
        var projector = new Bazaar.Server.Presence.ActivityProjector(store, new NullPublisher(), time, new Bazaar.Server.Presence.PresenceOptions());
        var source = new InMemoryAgentScheduleSource();
        source.Set(new AgentShiftEntry(ShiftSchedulerTests.Ada, ShiftSchedulerTests.WeekdayNine(), []));
        var scheduler = new ShiftScheduler(
            source,
            new InMemoryAvailabilityStateStore(),
            new ImmediateDrainGate(),
            new ShiftSchedulerOptions(),
            time,
            new AvailabilityPresenceSource(projector));

        time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 8, 59, 0, TimeSpan.Zero));
        await scheduler.TickAsync(Ct);
        Assert.Null(await store.GetAsync(ShiftSchedulerTests.Ada, Ct));

        time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        await scheduler.TickAsync(Ct);
        var onShift = await store.GetAsync(ShiftSchedulerTests.Ada, Ct);
        Assert.Equal(PresenceAvailability.OnShift, onShift!.Availability);

        time.SetUtcNow(new DateTimeOffset(2026, 11, 23, 17, 0, 0, TimeSpan.Zero));
        await scheduler.TickAsync(Ct);
        var off = await store.GetAsync(ShiftSchedulerTests.Ada, Ct);
        Assert.Equal(PresenceAvailability.Off, off!.Availability);
        Assert.True(off.Revision > onShift.Revision);
    }

    private sealed class FlakySource(TaskCompletionSource secondCall) : IAgentScheduleSource
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<IReadOnlyList<AgentShiftEntry>> ListAsync(CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1)
            {
                throw new InvalidOperationException("source down");
            }

            secondCall.TrySetResult();
            return ValueTask.FromResult<IReadOnlyList<AgentShiftEntry>>([]);
        }
    }

    [Fact]
    public async Task A_tick_failure_does_not_stop_the_loop()
    {
        var time = new FakeTimeProvider(_sundayNight);
        var options = new ShiftSchedulerOptions { TickInterval = TimeSpan.FromSeconds(30) };
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FlakySource(second);
        var scheduler = new ShiftScheduler(source, new InMemoryAvailabilityStateStore(), new ImmediateDrainGate(), options, time);
        using var service = new ShiftSchedulerHostedService(scheduler, options, time);

        await service.StartAsync(Ct);
        // The first tick throws; the timer may not exist yet when we advance, so keep advancing until the second tick runs.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!second.Task.IsCompleted && DateTime.UtcNow < deadline)
        {
            time.Advance(options.TickInterval);
            await Task.Delay(10, Ct);
        }

        await service.StopAsync(Ct);
        Assert.True(second.Task.IsCompleted);
        Assert.True(source.Calls >= 2);
    }

    [Fact]
    public async Task A_disabled_scheduler_never_ticks()
    {
        var time = new FakeTimeProvider(_sundayNight);
        var options = new ShiftSchedulerOptions { Enabled = false };
        var source = new FlakySource(new TaskCompletionSource());
        var scheduler = new ShiftScheduler(source, new InMemoryAvailabilityStateStore(), new ImmediateDrainGate(), options, time);
        using var service = new ShiftSchedulerHostedService(scheduler, options, time);

        await service.StartAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(5));
        await service.StopAsync(Ct);

        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public void Options_default_safely_and_read_from_configuration()
    {
        var defaults = ShiftSchedulerOptionsReader.Read(Config().GetSection(ShiftSchedulerOptions.SectionName));
        Assert.True(defaults.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.TickInterval);
        Assert.Empty(defaults.CalendarAvailabilityKinds);

        var read = ShiftSchedulerOptionsReader.Read(Config(
            ("Bazaar:WorkLifeScheduler:Enabled", "false"),
            ("Bazaar:WorkLifeScheduler:TickIntervalS", "5"),
            ("Bazaar:WorkLifeScheduler:CalendarAvailabilityKinds", "leave, offsite")).GetSection(ShiftSchedulerOptions.SectionName));
        Assert.False(read.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(5), read.TickInterval);
        Assert.Equal(["leave", "offsite"], read.CalendarAvailabilityKinds);
    }

    [Theory]
    [InlineData("TickIntervalS", "0")]
    [InlineData("TickIntervalS", "-1")]
    [InlineData("TickIntervalS", "99999")]
    [InlineData("TickIntervalS", "soon")]
    [InlineData("Enabled", "maybe")]
    public void Bad_option_values_fail_at_start(string key, string value)
    {
        var section = Config(("Bazaar:WorkLifeScheduler:" + key, value)).GetSection(ShiftSchedulerOptions.SectionName);

        Assert.Throws<InvalidOperationException>(() => ShiftSchedulerOptionsReader.Read(section));
    }

    [Fact]
    public async Task The_extra_calendar_kinds_option_turns_a_flagless_event_into_a_vacation()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));
        var options = new ShiftSchedulerOptions { CalendarAvailabilityKinds = ["leave"] };
        var source = new InMemoryAgentScheduleSource();
        var leave = new CalendarEvent(Guid.NewGuid(), "leave", time.GetUtcNow().AddHours(-1), time.GetUtcNow().AddHours(3), affectsAvailability: false);
        source.Set(new AgentShiftEntry(ShiftSchedulerTests.Ada, ShiftSchedulerTests.WeekdayNine(), [leave]));
        var scheduler = new ShiftScheduler(source, new InMemoryAvailabilityStateStore(), new ImmediateDrainGate(), options, time);

        await scheduler.TickAsync(Ct);

        Assert.Equal(AvailabilityMachineState.Vacation, scheduler.SnapshotOf(ShiftSchedulerTests.Ada)!.State);
    }

    [Fact]
    public void Component_manifest_and_registration_resolve_the_scheduler_and_its_hosted_service()
    {
        var component = new WorkLifeSchedulerComponent();
        Assert.Equal("work-life-scheduler", component.Manifest.Id);
        Assert.Contains("presence-sources", component.Manifest.DependsOn);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_sundayNight));
        component.AddServices(services, Config());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ShiftScheduler>());
        Assert.Same(provider.GetRequiredService<InMemoryAgentScheduleSource>(), provider.GetRequiredService<IAgentScheduleSource>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is ShiftSchedulerHostedService);
    }
}
