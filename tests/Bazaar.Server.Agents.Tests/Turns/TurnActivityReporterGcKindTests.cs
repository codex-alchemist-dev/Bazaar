using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Presence.Sources.Abstractions;

namespace Bazaar.Server.Agents.Tests.Turns;

/// <summary>The reporter puts the GC kind, GC project and agent project on the event (docs/design/23, GC context).</summary>
public sealed class TurnActivityReporterGcKindTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");
    private static readonly Guid _turn = Guid.Parse("0196a6f0-0000-7000-8000-0000000000b2");
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-0000000000c3");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Sink : ITurnActivitySink
    {
        public List<TurnActivity> Activities { get; } = [];

        public ValueTask OnTurnActivityAsync(TurnActivity activity, CancellationToken ct)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Kinds(Func<Guid, GcKindInfo> answer) : IGcKindLookup
    {
        public ValueTask<GcKindInfo> LookupAsync(Guid gcId, CancellationToken ct) => ValueTask.FromResult(answer(gcId));
    }

    private sealed class Projects(string? project) : IAgentProjectLookup
    {
        public ValueTask<string?> GetCurrentProjectAsync(Guid agentId, CancellationToken ct) => ValueTask.FromResult(project);
    }

    [Fact]
    public async Task The_event_carries_the_gc_kind_gc_project_and_agent_project()
    {
        var sink = new Sink();
        var reporter = new TurnActivityReporter(
            sink, gcKinds: new Kinds(_ => new GcKindInfo(TurnGcKind.Project, "p2")), agentProjects: new Projects("p1"));

        await reporter.ReportAsync(_agent, _turn, _gc.ToString("D"), TurnActivityKind.ModelCallStarted, ct: Ct);

        var a = Assert.Single(sink.Activities);
        Assert.Equal(TurnGcKind.Project, a.GcKind);
        Assert.Equal("p2", a.GcProjectId);
        Assert.Equal("p1", a.AgentProjectId);
    }

    [Fact]
    public async Task A_failing_lookup_still_reports_the_event_as_other()
    {
        var sink = new Sink();
        var reporter = new TurnActivityReporter(sink, gcKinds: new Kinds(_ => throw new InvalidOperationException("down")));

        await reporter.ReportAsync(_agent, _turn, _gc.ToString("D"), TurnActivityKind.TurnStarted, ct: Ct);

        var a = Assert.Single(sink.Activities);
        Assert.Equal(TurnGcKind.Other, a.GcKind);
        Assert.Null(a.GcProjectId);
    }

    [Fact]
    public async Task A_gc_id_that_is_not_a_guid_skips_the_lookup()
    {
        var sink = new Sink();
        var reporter = new TurnActivityReporter(sink, gcKinds: new Kinds(_ => throw new InvalidOperationException("must not run")));

        await reporter.ReportAsync(_agent, _turn, "gc-test-1", TurnActivityKind.TurnStarted, ct: Ct);

        Assert.Equal(TurnGcKind.Other, Assert.Single(sink.Activities).GcKind);
    }
}
