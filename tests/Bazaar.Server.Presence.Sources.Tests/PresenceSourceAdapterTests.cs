using Bazaar.Contracts.Gateway;
using Cdax.Core;
using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.PsychWard.Abstractions;
using Bazaar.Server.WorkLife;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Presence.Sources.Tests;

/// <summary>Each adapter turns its source's event into the right <see cref="ActivityEvent"/> (docs/design/23).</summary>
public class PresenceSourceAdapterTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly DateTimeOffset _t = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private sealed class FakeProjector : IActivityProjector
    {
        public List<ActivityEvent> Events { get; } = [];

        public ValueTask OnEventAsync(ActivityEvent e, CancellationToken ct)
        {
            Events.Add(e);
            return ValueTask.CompletedTask;
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AvailabilityTransition Transition(AvailabilityMachineState state, params AvailabilityOverlay[] overlays) =>
        new(new AvailabilitySnapshot(state, overlays, 1, "test"), true, "test");

    [Theory]
    [InlineData(AvailabilityMachineState.OnShift, PresenceAvailability.OnShift)]
    [InlineData(AvailabilityMachineState.Draining, PresenceAvailability.Draining)]
    [InlineData(AvailabilityMachineState.Break, PresenceAvailability.Break)]
    [InlineData(AvailabilityMachineState.Off, PresenceAvailability.Off)]
    [InlineData(AvailabilityMachineState.Vacation, PresenceAvailability.Vacation)]
    public async Task Availability_state_maps_one_to_one(AvailabilityMachineState state, PresenceAvailability expected)
    {
        var projector = new FakeProjector();
        await new AvailabilityPresenceSource(projector).OnTransitionAsync(_agent, Transition(state), _t, Ct);

        var e = Assert.Single(projector.Events);
        Assert.Equal(ActivityEventKind.AvailabilityChange, e.Kind);
        Assert.Equal(expected, e.Availability);
        Assert.Equal(_t, e.SourceTime);
    }

    [Fact]
    public async Task A_transition_that_was_not_applied_sends_nothing()
    {
        var projector = new FakeProjector();
        var duplicate = new AvailabilityTransition(Transition(AvailabilityMachineState.Off).Snapshot, false, "duplicate");
        await new AvailabilityPresenceSource(projector).OnTransitionAsync(_agent, duplicate, _t, Ct);

        Assert.Empty(projector.Events);
    }

    [Fact]
    public async Task Throttled_and_covered_overlays_send_flags_only_when_they_change()
    {
        var projector = new FakeProjector();
        var source = new AvailabilityPresenceSource(projector);
        var throttled = new AvailabilityOverlay.Throttled(_t.AddMinutes(5));
        var covered = new AvailabilityOverlay.Covered("cover-agent");

        await source.OnTransitionAsync(_agent, Transition(AvailabilityMachineState.OnShift, throttled, covered), _t, Ct);
        Assert.Collection(
            projector.Events,
            e => Assert.Equal(ActivityEventKind.AvailabilityChange, e.Kind),
            e => Assert.True(e.Kind == ActivityEventKind.QuotaThrottle && e.Active),
            e => Assert.True(e.Kind == ActivityEventKind.Covered && e.Active));

        projector.Events.Clear();
        await source.OnTransitionAsync(_agent, Transition(AvailabilityMachineState.OnShift, throttled, covered), _t.AddSeconds(1), Ct);
        Assert.Single(projector.Events);

        projector.Events.Clear();
        await source.OnTransitionAsync(_agent, Transition(AvailabilityMachineState.OnShift, covered), _t.AddSeconds(2), Ct);
        Assert.Contains(projector.Events, e => e.Kind == ActivityEventKind.QuotaThrottle && !e.Active);
        Assert.DoesNotContain(projector.Events, e => e.Kind == ActivityEventKind.Covered);
    }

    private static TurnActivity Turn(TurnActivityKind kind, string? tool = null, TurnGcKind gc = TurnGcKind.Project, string? gcProject = "p1", string? own = "p1") =>
        new(_agent, _t, kind, Guid.Parse("0196a6f0-0000-7000-8000-0000000000b1"), Guid.Parse("0196a6f0-0000-7000-8000-0000000000c1"), gc, gcProject, own, tool, "a label");

    private static TurnPresenceSource TurnSource(FakeProjector projector) =>
        new(projector, new NameTableToolClassifier(new PresenceSourcesOptions()));

    [Theory]
    [InlineData(TurnActivityKind.TurnStarted, PresenceActivityKind.ModelCall)]
    [InlineData(TurnActivityKind.ModelCallStarted, PresenceActivityKind.ModelCall)]
    [InlineData(TurnActivityKind.ToolFinished, PresenceActivityKind.ModelCall)]
    [InlineData(TurnActivityKind.Drafting, PresenceActivityKind.Drafting)]
    [InlineData(TurnActivityKind.TurnEnded, PresenceActivityKind.Idle)]
    public async Task Turn_lifecycle_maps_to_the_activity(TurnActivityKind kind, PresenceActivityKind expected)
    {
        var projector = new FakeProjector();
        await TurnSource(projector).OnTurnActivityAsync(Turn(kind), Ct);

        var e = Assert.Single(projector.Events);
        Assert.Equal(ActivityEventKind.Turn, e.Kind);
        Assert.Equal(expected, e.Activity);
    }

    [Theory]
    [InlineData("web.search", PresenceToolClass.Research)]
    [InlineData("read_file", PresenceToolClass.Research)]
    [InlineData("review.pr", PresenceToolClass.Review)]
    [InlineData("edit_file", PresenceToolClass.EditOrBuild)]
    [InlineData("dotnet-build", PresenceToolClass.EditOrBuild)]
    [InlineData("mystery", PresenceToolClass.None)]
    [InlineData(null, PresenceToolClass.None)]
    public async Task A_tool_event_carries_the_tool_class(string? tool, PresenceToolClass expected)
    {
        var projector = new FakeProjector();
        await TurnSource(projector).OnTurnActivityAsync(Turn(TurnActivityKind.ToolStarted, tool), Ct);

        var e = Assert.Single(projector.Events);
        Assert.Equal(PresenceActivityKind.Tool, e.Activity);
        Assert.Equal(expected, e.ToolClass);
        Assert.NotNull(e.GcId);
        Assert.NotNull(e.TurnId);
        Assert.Equal("a label", e.Label);
    }

    [Theory]
    [InlineData(TurnGcKind.Project, "p1", "p1", PresenceGcContext.ProjectGc)]
    [InlineData(TurnGcKind.Project, "p2", "p1", PresenceGcContext.OtherProjectGc)]
    [InlineData(TurnGcKind.Project, "p2", null, PresenceGcContext.ProjectGc)]
    [InlineData(TurnGcKind.Council, null, "p1", PresenceGcContext.CouncilOrVote)]
    [InlineData(TurnGcKind.Vote, null, "p1", PresenceGcContext.CouncilOrVote)]
    [InlineData(TurnGcKind.Other, null, "p1", PresenceGcContext.Other)]
    public async Task The_gc_kind_maps_to_the_gc_context(TurnGcKind kind, string? gcProject, string? own, PresenceGcContext expected)
    {
        var projector = new FakeProjector();
        await TurnSource(projector).OnTurnActivityAsync(Turn(TurnActivityKind.ModelCallStarted, gc: kind, gcProject: gcProject, own: own), Ct);

        Assert.Equal(expected, Assert.Single(projector.Events).GcContext);
    }

    [Theory]
    [InlineData(TurnActivityKind.ApprovalWaitStarted, true)]
    [InlineData(TurnActivityKind.ApprovalWaitEnded, false)]
    public async Task An_approval_wait_is_the_approval_flag(TurnActivityKind kind, bool active)
    {
        var projector = new FakeProjector();
        await TurnSource(projector).OnTurnActivityAsync(Turn(kind), Ct);

        var e = Assert.Single(projector.Events);
        Assert.Equal(ActivityEventKind.ApprovalWait, e.Kind);
        Assert.Equal(active, e.Active);
    }

    [Fact]
    public async Task The_classifier_uses_operator_prefix_lists()
    {
        var options = new PresenceSourcesOptions { ResearchToolPrefixes = ["zork"], ReviewToolPrefixes = [], EditOrBuildToolPrefixes = [] };
        var classifier = new NameTableToolClassifier(options);

        Assert.Equal(PresenceToolClass.Research, classifier.Classify("zork.plumb"));
        Assert.Equal(PresenceToolClass.None, classifier.Classify("read_file"));
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(false, QuotaState.RateLimited, true, true)]
    [InlineData(false, QuotaState.DailyExhausted, true, true)]
    [InlineData(true, QuotaState.Available, true, false)]
    [InlineData(false, QuotaState.SpendCapped, false, false)]
    [InlineData(false, QuotaState.Down, false, false)]
    public async Task Capacity_changes_set_or_clear_the_throttle_flag(bool available, QuotaState reason, bool sends, bool active)
    {
        var projector = new FakeProjector();
        var source = new QuotaPresenceSource(projector, new FakeTimeProvider(_t));
        await source.OnCapacityChangedAsync(new AgentCapacityChanged(_agent.ToString(), available, _t.AddMinutes(1), reason), _t, Ct);

        if (!sends)
        {
            Assert.Empty(projector.Events);
            return;
        }

        var e = Assert.Single(projector.Events);
        Assert.Equal(ActivityEventKind.QuotaThrottle, e.Kind);
        Assert.Equal(active, e.Active);
        Assert.Equal(_agent, e.AgentId);
    }

    [Fact]
    public async Task The_gateway_shaped_publish_forwards_capacity_events_with_the_clock_time_and_ignores_other_agents_ids()
    {
        var projector = new FakeProjector();
        var source = new QuotaPresenceSource(projector, new FakeTimeProvider(_t));

        source.Publish(new QuotaStateChanged("p", "k", "m", QuotaState.RateLimited, null));
        source.Publish(new AgentCapacityChanged("not-a-guid", false, null, QuotaState.RateLimited));
        source.Publish(new AgentCapacityChanged(_agent.ToString(), false, null, QuotaState.RateLimited));
        await Task.Delay(50, Ct);

        var e = Assert.Single(projector.Events);
        Assert.Equal(_t, e.SourceTime);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_kanban_wait_is_the_kanban_flag(bool waiting)
    {
        var projector = new FakeProjector();
        await new KanbanPresenceSource(projector).OnWaitChangedAsync(_agent, _t, waiting, Ct);

        var e = Assert.Single(projector.Events);
        Assert.Equal(ActivityEventKind.KanbanWait, e.Kind);
        Assert.Equal(waiting, e.Active);
    }

    [Fact]
    public async Task A_ward_stay_turns_the_ward_overlay_on_at_admission_and_off_at_discharge()
    {
        var projector = new FakeProjector();
        var source = new WardPresenceSource(projector);
        var admitted = new WardStay(Guid.NewGuid(), _agent, WardTrigger.LoopGuard, null, _t);

        await source.OnStayChangedAsync(admitted, Ct);
        await source.OnStayChangedAsync(admitted with { DischargedAt = _t.AddHours(1) }, Ct);

        Assert.Collection(
            projector.Events,
            e => Assert.True(e.Kind == ActivityEventKind.Ward && e.Active && e.SourceTime == _t),
            e => Assert.True(e.Kind == ActivityEventKind.Ward && !e.Active && e.SourceTime == _t.AddHours(1)));
    }

    [Fact]
    public void The_component_depends_on_presence_and_registers_every_port()
    {
        var component = new PresenceSourcesComponent();
        Assert.Equal("presence-sources", component.Manifest.Id);
        Assert.Equal(["presence"], component.Manifest.DependsOn);

        var services = new ServiceCollection();
        services.AddSingleton<IActivityProjector>(new FakeProjector());
        component.AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<AvailabilityPresenceSource>(provider.GetRequiredService<IAvailabilityTransitionSink>());
        Assert.IsType<TurnPresenceSource>(provider.GetRequiredService<ITurnActivitySink>());
        Assert.IsType<QuotaPresenceSource>(provider.GetRequiredService<IQuotaCapacitySink>());
        Assert.IsType<KanbanPresenceSource>(provider.GetRequiredService<IKanbanWaitSink>());
        Assert.IsType<WardPresenceSource>(provider.GetRequiredService<IWardStateSink>());
    }
}
