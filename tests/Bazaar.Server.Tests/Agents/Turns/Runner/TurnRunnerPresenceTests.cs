using System.Globalization;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Presence.Sources.Abstractions;
using Bazaar.Server.Tests.Agents.Data;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;
using TurnRunner = Bazaar.Server.Agents.Turns.Runner.TurnRunner;
using TurnRunnerOptions = Bazaar.Server.Agents.Turns.Runner.TurnRunnerOptions;

namespace Bazaar.Server.Tests.Agents.Turns.Runner;

/// <summary>The <see cref="ITurnRunner"/> loop reports its lifecycle to the presence port, against PostgreSQL (doc 06, docs/design/23).</summary>
public sealed class TurnRunnerPresenceTests(AgentDb db) : IClassFixture<AgentDb>
{
    private static readonly ModelToolDefinition _lookup = new("lookup", "Looks a thing up.", """{"type":"object"}""");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private sealed class Rig
    {
        public FakeProvider Provider { get; } = new();
        public FakeToolDispatcher Tools { get; } = new();
        public TurnActivityReporterTests.FakeSink Sink { get; set; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-10-30T12:00:00Z", CultureInfo.InvariantCulture));

        public TurnRunner Create(AgentDb db) => new(
            new LeaseRepository(db.DataSource),
            new InboxRepository(db.DataSource),
            new TurnRepository(db.DataSource),
            new TurnStepRepository(db.DataSource),
            new RunnerGateway(Provider),
            new FakeContextAssembler(),
            Tools,
            new FakePostSink(),
            Time,
            new TurnRunnerOptions { ToolCatalogue = [_lookup] },
            activity: new TurnActivityReporter(Sink));
    }

    private async Task<Guid> NewAgentAsync()
    {
        var id = Guid.CreateVersion7();
        await db.ExecAsync("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'presence test', 'active')", ("id", id));
        return id;
    }

    private static TurnRunRequest Request(Guid agent, string? gc) =>
        new(agent, Guid.CreateVersion7(), TurnRole.Actor, "mention", [], "turn-a", "test-route", ["lookup"], TriggerGcId: gc);

    private static ScriptStep.Emit Emit(ModelEvent e) => new(e);

    [Fact]
    public async Task A_turn_that_calls_a_tool_and_posts_reports_every_stage_in_order()
    {
        SkipWithoutDatabase();
        var rig = new Rig();
        var agent = await NewAgentAsync();
        rig.Provider.EnqueueCall(
            Emit(new ModelEvent.ToolCallStart("c1", "lookup")),
            Emit(new ModelEvent.ToolCallArgsDelta("c1", "{}")),
            Emit(new ModelEvent.ToolCallEnd("c1")),
            Emit(new ModelEvent.Done(StopReason.ToolUse, "test-model")));
        rig.Provider.EnqueueCall(Emit(new ModelEvent.TextDelta("Done.")), Emit(new ModelEvent.Done(StopReason.EndTurn, "test-model")));

        var outcome = await rig.Create(db).RunAsync(Request(agent, Guid.CreateVersion7().ToString("D")), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal(
            [
                TurnActivityKind.TurnStarted,
                TurnActivityKind.ModelCallStarted,
                TurnActivityKind.ToolStarted,
                TurnActivityKind.ToolFinished,
                TurnActivityKind.ModelCallStarted,
                TurnActivityKind.Drafting,
                TurnActivityKind.TurnEnded,
            ],
            rig.Sink.Activities.Select(a => a.Kind));
        Assert.All(rig.Sink.Activities, a => Assert.Equal((agent, outcome.TurnId), (a.AgentId, a.TurnId)));
    }

    [Fact]
    public async Task A_tool_that_needs_approval_starts_a_wait_that_ends_before_the_next_model_call()
    {
        SkipWithoutDatabase();
        var rig = new Rig();
        var agent = await NewAgentAsync();
        rig.Tools.Handler = (_, _) => Task.FromResult(new ToolDispatchResult(ToolDispatchOutcome.PendingApproval, null, "high impact"));
        rig.Provider.EnqueueCall(
            Emit(new ModelEvent.ToolCallStart("c1", "lookup")),
            Emit(new ModelEvent.ToolCallArgsDelta("c1", "{}")),
            Emit(new ModelEvent.ToolCallEnd("c1")),
            Emit(new ModelEvent.Done(StopReason.ToolUse, "test-model")));
        rig.Provider.EnqueueCall(Emit(new ModelEvent.TextDelta("Waiting.")), Emit(new ModelEvent.Done(StopReason.EndTurn, "test-model")));

        await rig.Create(db).RunAsync(Request(agent, null), Ct);

        var kinds = rig.Sink.Activities.Select(a => a.Kind).ToList();
        var started = kinds.IndexOf(TurnActivityKind.ApprovalWaitStarted);
        Assert.True(started > kinds.IndexOf(TurnActivityKind.ToolFinished));
        Assert.Equal(TurnActivityKind.ApprovalWaitEnded, kinds[started + 1]);
        Assert.Equal(TurnActivityKind.ModelCallStarted, kinds[started + 2]);
    }

    [Fact]
    public async Task A_throwing_sink_does_not_fail_the_turn()
    {
        SkipWithoutDatabase();
        var rig = new Rig { Sink = new TurnActivityReporterTests.FakeSink(_ => new InvalidOperationException("sink down")) };
        var agent = await NewAgentAsync();
        rig.Provider.EnqueueCall(Emit(new ModelEvent.TextDelta("Done.")), Emit(new ModelEvent.Done(StopReason.EndTurn, "test-model")));

        var outcome = await rig.Create(db).RunAsync(Request(agent, null), Ct);

        Assert.Equal(TurnEndReason.FinalMessage, outcome.Reason);
        Assert.Equal(TurnActivityKind.TurnEnded, rig.Sink.Activities[^1].Kind);
    }
}
